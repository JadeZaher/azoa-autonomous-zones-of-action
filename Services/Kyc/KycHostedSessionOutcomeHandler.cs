// SPDX-License-Identifier: UNLICENSED

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.HostedSessions;
using AZOA.WebAPI.Settings;

namespace AZOA.WebAPI.Services.Kyc;

/// <summary>
/// KYC-side resumption for <see cref="IHostedSessionFlow"/>: once an authoritative
/// TERMINAL verdict exists, run AML screening, register ongoing monitoring, write
/// the person-to-avatar binding, and terminalize the submission ledger row. Only
/// ever called with a terminal outcome — the flow parks a non-terminal one before
/// reaching here, and the flow's resume CLAIM guarantees exactly one caller.
/// </summary>
public sealed class KycHostedSessionOutcomeHandler : IHostedSessionOutcomeHandler
{
    private const string PersistenceMessage = "The KYC outcome could not be recorded.";

    /// <summary>Same clamp KycManager applies, so one approval never outlives another.</summary>
    private const int MinimumExpiryDays = 1;
    private const int MaximumExpiryDays = 365;

    private readonly IKycStore _kycStore;
    private readonly IKycIdentityBindingService _binding;
    private readonly IIdentomatScreeningService _screening;
    private readonly KycSettings _settings;
    private readonly ILogger<KycHostedSessionOutcomeHandler> _logger;
    private readonly TimeProvider _clock;

    public KycHostedSessionOutcomeHandler(
        IKycStore kycStore,
        IKycIdentityBindingService binding,
        IIdentomatScreeningService screening,
        IOptions<KycSettings> settings,
        ILogger<KycHostedSessionOutcomeHandler>? logger = null,
        TimeProvider? clock = null)
    {
        _kycStore = kycStore;
        _binding = binding;
        _screening = screening;
        _settings = settings.Value;
        _logger = logger ?? NullLogger<KycHostedSessionOutcomeHandler>.Instance;
        _clock = clock ?? TimeProvider.System;
    }

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

        if (!Guid.TryParseExact(session.SubjectAvatarId ?? string.Empty, "N", out var avatarId))
            return AZOAResult<string?>.Failure("The hosted session has no resolvable subject avatar.");
        Guid? tenantId = Guid.TryParseExact(session.TenantId ?? string.Empty, "N", out var parsedTenant)
            ? parsedTenant
            : null;

        var now = _clock.GetUtcNow();

        if (outcome.IsApproved)
        {
            if (outcome.Subject is null)
            {
                _logger.LogError(
                    "An approved hosted session carried no subject attributes; refusing to approve avatar {AvatarId}.",
                    avatarId);
                return AZOAResult<string?>.Failure(
                    "The approved verification carried no verifiable person attributes.");
            }

            // AML FIRST, before any durable identity write. The screening row is what
            // the value gate reads, and the gate denies when it is missing, so an
            // approval that fails to screen must not become a binding either.
            var screened = await ScreenAsync(avatarId, tenantId, outcome.Subject, ct);
            if (screened.IsError || screened.Result is null)
            {
                return AZOAResult<string?>.FailureWithCode(
                    screened.Message,
                    screened.Code ?? AzoaErrorCodes.DependencyUnavailable);
            }

            if (screened.Result.Decision != KycScreeningDecision.NO_HIT)
            {
                // The submission still records that IDENTITY was verified — that is
                // true and is a distinct fact. The value gate reads the screening row
                // and stays shut. Nothing here is projected to the subject or the
                // tenant: an adverse AML finding is not something to tip off about.
                _logger.LogWarning(
                    "AML screening returned {Decision} for a verified identity; the value gate stays closed for avatar {AvatarId}.",
                    screened.Result.Decision,
                    avatarId);
            }

            var bound = await _binding.BindAsync(
                avatarId,
                tenantId,
                KycProvider.IDENTOMAT,
                session.ProviderKey,
                outcome.Subject,
                ct);
            if (bound.IsError)
            {
                return AZOAResult<string?>.FailureWithCode(
                    bound.Message,
                    bound.Code ?? AzoaErrorCodes.DependencyUnavailable);
            }

            // Ongoing monitoring AFTER the binding. A failure here degrades future
            // change detection but cannot change today's decision, which the persisted
            // screening row already governs — so it is logged loudly rather than
            // failing the resume and stranding an approval that did screen cleanly.
            await RegisterMonitoringAsync(avatarId, tenantId, outcome.Subject, ct);
        }

        var existing = await _kycStore.GetActiveSubmissionAsync(avatarId, tenantId, ct);
        if (existing.IsError)
        {
            return AZOAResult<string?>.FailureWithCode(
                PersistenceMessage,
                AzoaErrorCodes.DependencyUnavailable);
        }

        if (existing.Result is null)
        {
            // No active attempt to terminalize. The hosted session itself remains the
            // durable record of the outcome, so this is not a failure.
            _logger.LogWarning(
                "No active KYC attempt matched hosted session {SessionId}; the outcome was recorded on the session only.",
                session.Id);
            return AZOAResult<string?>.Success(null);
        }

        var submission = existing.Result;
        if (!MatchesSession(submission, session))
        {
            // The active attempt is a DIFFERENT one from the attempt this session
            // belongs to. Correlating by avatar alone would let a stale session's
            // verdict terminalize whichever attempt happens to be open, writing an
            // approval the authority never issued for it.
            _logger.LogError(
                "Hosted session {SessionId} resolved an active KYC attempt that belongs to another session; refusing to terminalize it.",
                session.Id);
            return AZOAResult<string?>.Success(null);
        }

        submission.Status = outcome.IsApproved ? KycStatus.APPROVED : KycStatus.REJECTED;
        submission.ReviewedAt = now;
        submission.ModifiedDate = now;
        if (outcome.IsApproved)
        {
            // Begin set this to the ~30 minute SESSION window. Leaving it there makes
            // every approval expire minutes after it is granted — invisible in a fast
            // test, total denial in production — because both value gates require an
            // explicit FUTURE expiry.
            submission.ExpiresAt = now.AddDays(
                Math.Clamp(_settings.SubmissionExpiryDays, MinimumExpiryDays, MaximumExpiryDays));
        }
        else
        {
            submission.RejectionReason = outcome.Detail;
        }

        var saved = await _kycStore.UpsertSubmissionAsync(submission, ct);
        if (saved.IsError)
        {
            return AZOAResult<string?>.FailureWithCode(
                PersistenceMessage,
                AzoaErrorCodes.DependencyUnavailable);
        }

        return AZOAResult<string?>.Success(submission.Id);
    }

    /// <summary>
    /// The submission's <c>ProviderSessionId</c> is the AZOA hosted-session id — the
    /// adapter puts it there deliberately so the vendor's own id never leaves the
    /// node — which makes it the exact correlation between an attempt and a session.
    /// </summary>
    private static bool MatchesSession(KycSubmission submission, HostedSession session)
        => !string.IsNullOrWhiteSpace(submission.ProviderSessionId)
            && string.Equals(
                submission.ProviderSessionId.Trim(),
                session.Id?.Trim(),
                StringComparison.OrdinalIgnoreCase);

    private async Task<AZOAResult<KycScreeningResult>> ScreenAsync(
        Guid avatarId,
        Guid? tenantId,
        HostedSessionSubjectAttributes subject,
        CancellationToken ct)
    {
        var fullName = ComposeFullName(subject);
        if (string.IsNullOrWhiteSpace(fullName))
        {
            _logger.LogError(
                "An approved hosted session carried no usable person name to screen; refusing avatar {AvatarId}.",
                avatarId);
            return AZOAResult<KycScreeningResult>.Failure(
                "The approved verification carried no verifiable person attributes.");
        }

        return await _screening.ScreenAsync(avatarId, tenantId, fullName, subject.Birthday, ct);
    }

    private async Task RegisterMonitoringAsync(
        Guid avatarId,
        Guid? tenantId,
        HostedSessionSubjectAttributes subject,
        CancellationToken ct)
    {
        var fullName = ComposeFullName(subject);
        if (string.IsNullOrWhiteSpace(fullName))
            return;

        var registered = await _screening.RegisterMonitorAsync(
            avatarId,
            tenantId,
            fullName,
            subject.Birthday,
            ct);
        if (registered.IsError)
        {
            _logger.LogError(
                "AML ongoing monitoring could not be registered for avatar {AvatarId}; this subject will not be re-screened by the poll until it is: {Reason}",
                avatarId,
                registered.Message);
        }
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
