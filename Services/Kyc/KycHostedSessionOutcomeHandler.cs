// SPDX-License-Identifier: UNLICENSED

using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.HostedSessions;

namespace AZOA.WebAPI.Services.Kyc;

/// <summary>Applies verified hosted identity outcomes; see AGENTS.md hosted-outcome safety.</summary>
public sealed class KycHostedSessionOutcomeHandler : IHostedSessionOutcomeHandler, IHostedSessionProgressHandler
{
    private const string SubmissionExpiryDaysKey = "Kyc:SubmissionExpiryDays";
    private const int DefaultSubmissionExpiryDays = 30;

    private const string PersistenceMessage = "The KYC outcome could not be recorded.";

    private readonly IKycStore _kycStore;
    private readonly IKycIdentityBindingService _binding;
    private readonly IIdentomatScreeningService _screening;
    private readonly ILogger<KycHostedSessionOutcomeHandler> _logger;
    private readonly TimeProvider _clock;
    private readonly IConfiguration? _configuration;

    public KycHostedSessionOutcomeHandler(
        IKycStore kycStore,
        IKycIdentityBindingService binding,
        IIdentomatScreeningService screening,
        ILogger<KycHostedSessionOutcomeHandler>? logger = null,
        TimeProvider? clock = null,
        IConfiguration? configuration = null)
    {
        _kycStore = kycStore;
        _binding = binding;
        _screening = screening;
        _logger = logger ?? NullLogger<KycHostedSessionOutcomeHandler>.Instance;
        _clock = clock ?? TimeProvider.System;
        _configuration = configuration;
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<string?>> ResumeAsync(
        HostedSession session,
        HostedSessionOutcome outcome,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(outcome);

        if (!outcome.IsTerminal)
        {
            // Defence in depth: a non-terminal verdict must never reach a ledger write.
            return AZOAResult<string?>.Failure(
                "A non-terminal provider outcome cannot resume a KYC submission.");
        }

        if (!Guid.TryParseExact(session.SubjectUserId ?? string.Empty, "N", out var userId))
            return AZOAResult<string?>.Failure("The hosted session has no resolvable subject user.");
        Guid? tenantId = null;
        if (!string.IsNullOrWhiteSpace(session.TenantId))
        {
            if (!Guid.TryParseExact(session.TenantId, "N", out var parsedTenant))
                return AZOAResult<string?>.Failure("The hosted session has no resolvable tenant.");
            tenantId = parsedTenant;
        }

        var now = _clock.GetUtcNow();

        var existing = await _kycStore.GetActiveSubmissionAsync(userId, tenantId, ct);
        if (existing.IsError)
        {
            return AZOAResult<string?>.FailureWithCode(
                PersistenceMessage,
                AzoaErrorCodes.DependencyUnavailable);
        }

        if (existing.Result is null)
        {
            _logger.LogWarning(
                "No active KYC attempt matched hosted session {SessionId}; checking terminal or superseding attempts.",
                session.Id);
            var latest = await _kycStore.GetLatestSubmissionAsync(userId, tenantId, ct);
            if (latest.IsError) return AZOAResult<string?>.FailureWithCode(PersistenceMessage, AzoaErrorCodes.DependencyUnavailable);
            if (latest.Result is { } completed && MatchesSession(completed, session)
                && completed.Status is KycStatus.APPROVED or KycStatus.REJECTED or KycStatus.EXPIRED)
                return AZOAResult<string?>.Success(completed.Id);
            if (latest.Result is { } superseding && superseding.SubmittedAt > session.CreatedDate
                && superseding.ProviderSessionId != session.Id)
                return AZOAResult<string?>.Success(null);
            return AZOAResult<string?>.FailureWithCode("The KYC attempt is not yet attached; retry reconciliation.", AzoaErrorCodes.Conflict);
        }

        var submission = existing.Result;
        if (!MatchesSession(submission, session)
            || submission.Status is not (KycStatus.PENDING or KycStatus.IN_REVIEW)
            || submission.ExpiresAt is null || submission.ExpiresAt <= now)
        {
            _logger.LogError(
                "Hosted session {SessionId} resolved an active KYC attempt that belongs to another session; refusing to terminalize it.",
                session.Id);
            // The open attempt belongs to another session: this session's own attempt is retired or
            // superseded, so it terminalizes with no identity effect instead of retrying forever.
            if (!string.Equals(submission.ProviderSessionId?.Trim(), session.Id?.Trim(), StringComparison.OrdinalIgnoreCase))
                return AZOAResult<string?>.Success(null);
            if (MatchesSession(submission, session) && submission.ExpiresAt <= now)
                return AZOAResult<string?>.Success(null);
            return AZOAResult<string?>.FailureWithCode("The KYC attempt is not yet attached; retry reconciliation.", AzoaErrorCodes.Conflict);
        }

        if (outcome.IsApproved)
        {
            if (outcome.Subject is null)
            {
                _logger.LogError(
                    "An approved hosted session carried no subject attributes; refusing to approve user {UserId}.",
                    userId);
                return AZOAResult<string?>.Failure(
                    "The approved verification carried no verifiable person attributes.");
            }

            var lineageKey = KycAmlLineage.ForHosted(submission.Id, session.Id, outcome.Subject);
            var screened = await ScreenAsync(userId, tenantId, outcome.Subject, lineageKey, ct);
            if (screened.IsError || screened.Result is null)
            {
                return AZOAResult<string?>.FailureWithCode(
                    screened.Message,
                    screened.Code ?? AzoaErrorCodes.DependencyUnavailable);
            }

            if (screened.Result.LineageKey != lineageKey)
                return AZOAResult<string?>.Failure("The identity screening evidence did not match this attempt.");
            submission.AmlLineageKey = lineageKey;

            if (KycAmlClearance.RequiresManualReview(screened.Result))
            {
                _logger.LogWarning(
                    "AML manual review opened: screening returned {Decision} (truncated: {Truncated}, policy {PolicyVersion}) "
                        + "for verified identity {UserId} in tenant {TenantId} at {ScreenedAt}. The value gate stays closed "
                        + "and the tenant projection reports UnderReview until an operator decides.",
                    screened.Result.Decision,
                    screened.Result.ResultsTruncated,
                    screened.Result.PolicyVersion,
                    userId,
                    tenantId,
                    screened.Result.ScreenedAt);
            }

            var bound = await _binding.BindHostedAsync(
                userId,
                tenantId,
                submission.Provider,
                session.ProviderKey,
                outcome.Subject,
                submission,
                session,
                ct);
            if (bound.IsError)
            {
                return AZOAResult<string?>.FailureWithCode(
                    bound.Message,
                    bound.Code ?? AzoaErrorCodes.DependencyUnavailable);
            }

            var monitored = await RegisterMonitoringAsync(userId, tenantId, outcome.Subject, lineageKey, ct);
            if (monitored.IsError || monitored.Result is null || monitored.Result.LineageKey != lineageKey
                || string.IsNullOrWhiteSpace(monitored.Result.ProviderMonitorId))
                return AZOAResult<string?>.FailureWithCode("Ongoing identity monitoring could not be registered.",
                    AzoaErrorCodes.DependencyUnavailable);
        }

        ct.ThrowIfCancellationRequested();
        var reviewedAt = _clock.GetUtcNow();
        var approvalExpiry = KycApprovalValidity.ResolveExpiry(_configuration, reviewedAt);
        if (outcome.EvidenceValidUntil is { } evidenceExpiry && evidenceExpiry < approvalExpiry)
            approvalExpiry = evidenceExpiry;
        if (outcome.IsApproved && approvalExpiry <= reviewedAt)
            return AZOAResult<string?>.Failure("The authoritative verification evidence expired before approval could commit.");
        var saved = await _kycStore.TryCompleteHostedAsync(
            submission,
            session,
            outcome.IsApproved ? KycStatus.APPROVED : KycStatus.REJECTED,
            reviewedAt,
            outcome.IsApproved ? approvalExpiry : submission.ExpiresAt,
            outcome.IsApproved ? null : outcome.Detail,
            ct);
        if (saved.IsError)
        {
            return AZOAResult<string?>.FailureWithCode(
                PersistenceMessage,
                AzoaErrorCodes.DependencyUnavailable);
        }

        // A lost completion CAS (e.g. a concurrent manual-review mark) must retry, never close the session
        // without the decision: the next resume re-reads and converges (decided, expired, or this verdict).
        if (saved.Result is null)
            return AZOAResult<string?>.FailureWithCode("The KYC attempt changed during completion; retry.", AzoaErrorCodes.Conflict);

        return AZOAResult<string?>.Success(saved.Result.Id);
    }

    /// <summary>
    /// The vendor holds the attempt in manual review: mark it IN_REVIEW and extend its expiry to the
    /// submission lifetime so the human decision can still approve it. See AGENTS.md §manual review.
    /// </summary>
    public async Task ObserveProgressAsync(HostedSession session, HostedSessionOutcome outcome, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.IsTerminal || !outcome.IsManualReview || _kycStore is not IKycHostedProgressStore progress)
            return;
        if (!Guid.TryParseExact(session.SubjectUserId ?? string.Empty, "N", out var userId))
            return;
        Guid? tenantId = null;
        if (!string.IsNullOrWhiteSpace(session.TenantId))
        {
            if (!Guid.TryParseExact(session.TenantId, "N", out var parsedTenant))
                return;
            tenantId = parsedTenant;
        }

        var active = await _kycStore.GetActiveSubmissionAsync(userId, tenantId, ct);
        var now = _clock.GetUtcNow();
        if (active.IsError || active.Result is not { } submission || !MatchesSession(submission, session)
            || submission.ExpiresAt is null || submission.ExpiresAt <= now)
            return;

        var days = Math.Clamp(_configuration?.GetValue<int?>(SubmissionExpiryDaysKey) ?? DefaultSubmissionExpiryDays, 1, 365);
        var marked = await progress.TryMarkHostedInReviewAsync(submission, session, now, now.AddDays(days), ct);
        if (!marked.IsError && marked.Result is not null)
            _logger.LogInformation("Hosted KYC attempt {SubmissionId} is in vendor manual review; expiry extended.", submission.Id);
    }

    /// <summary>
    /// Live only while the attempt bound to this session (attempt id == session id) is still open and
    /// unexpired, i.e. while <see cref="ResumeAsync"/> would still apply a verdict. See AGENTS.md §manual review.
    /// </summary>
    public async Task<HostedOutcomeRelevance> ProbeAsync(HostedSession session, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!Guid.TryParseExact(session.Id ?? string.Empty, "N", out var attemptId))
            return HostedOutcomeRelevance.Unknown;
        var attempt = await _kycStore.GetSubmissionByIdAsync(attemptId, ct);
        if (attempt.IsError)
            return HostedOutcomeRelevance.Unknown;
        return attempt.Result is { } submission && MatchesSession(submission, session)
            && submission.Status is KycStatus.PENDING or KycStatus.IN_REVIEW
            && submission.ExpiresAt is { } expiresAt && expiresAt > now
                ? HostedOutcomeRelevance.Live
                : HostedOutcomeRelevance.Dead;
    }

    private static bool MatchesSession(KycSubmission submission, HostedSession session)
        => session.FlowKind == HostedSessionFlowKinds.Kyc
            && submission.Provider is KycProvider.IDENTOMAT or KycProvider.VERIFF or KycProvider.GENERIC_HOSTED
            && string.Equals(submission.ProviderKey, session.ProviderKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(submission.UserId, session.SubjectUserId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(submission.TenantId, session.TenantId, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(submission.ProviderSessionId)
            && string.Equals(
                submission.ProviderSessionId.Trim(),
                session.Id?.Trim(),
                StringComparison.OrdinalIgnoreCase);

    private async Task<AZOAResult<KycScreeningResult>> ScreenAsync(
        Guid userId,
        Guid? tenantId,
        HostedSessionSubjectAttributes subject,
        string lineageKey,
        CancellationToken ct)
    {
        var fullName = ComposeFullName(subject);
        if (string.IsNullOrWhiteSpace(fullName))
        {
            _logger.LogError(
                "An approved hosted session carried no usable person name to screen; refusing user {UserId}.",
                userId);
            return AZOAResult<KycScreeningResult>.Failure(
                "The approved verification carried no verifiable person attributes.");
        }

        return await _screening.ScreenAsync(userId, tenantId, fullName, subject.Birthday, ct, lineageKey: lineageKey);
    }

    private async Task<AZOAResult<KycScreeningMonitor>> RegisterMonitoringAsync(
        Guid userId,
        Guid? tenantId,
        HostedSessionSubjectAttributes subject,
        string lineageKey,
        CancellationToken ct)
    {
        var fullName = ComposeFullName(subject);
        if (string.IsNullOrWhiteSpace(fullName))
            return AZOAResult<KycScreeningMonitor>.Failure("A screening name is required.");

        var registered = await _screening.RegisterMonitorAsync(
            userId,
            tenantId,
            fullName,
            subject.Birthday,
            lineageKey,
            ct);
        return registered;
    }

    /// <summary>The vendor's screening query keys on a single <c>fullName</c> string.</summary>
    private static string ComposeFullName(HostedSessionSubjectAttributes subject)
        => string.Join(
                ' ',
                new[] { subject.FirstName, subject.LastName }
                    .Where(part => !string.IsNullOrWhiteSpace(part))
                    .Select(part => part!.Trim()))
            .Trim();
}
