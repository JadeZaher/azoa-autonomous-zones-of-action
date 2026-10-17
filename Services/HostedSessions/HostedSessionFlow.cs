// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace AZOA.WebAPI.Services.HostedSessions;

/// <summary>
/// Orchestrates one hosted-redirect lifecycle for any <see cref="IHostedSessionProvider"/>.
/// See <c>Services/HostedSessions/AGENTS.md</c> for why this is its own abstraction.
/// </summary>
public interface IHostedSessionFlow
{
    /// <summary>Claims admission before sending an external create request.</summary>
    Task<AZOAResult<HostedSessionStartResult>> BeginAsync(
        IHostedSessionProvider provider,
        HostedSessionRequest request,
        CancellationToken ct = default);

    /// <summary>Refreshes a caller-authorized hosted session using the provider authority and the shared resumption lease.</summary>
    Task<AZOAResult<HostedCallbackResult>> RefreshAsync(
        IHostedSessionProvider provider, Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Caller-initiated (user/tenant) refresh: at most one vendor re-fetch per session per
    /// <paramref name="cooldown"/>; inside it the stored state answers. See AGENTS.md §recovery (budget fairness).
    /// </summary>
    Task<AZOAResult<HostedCallbackResult>> RefreshOnDemandAsync(
        IHostedSessionProvider provider, Guid sessionId, TimeSpan cooldown, CancellationToken ct = default);

    /// <summary>Authenticates a callback and applies authoritative outcomes under a renewable lease.</summary>
    Task<AZOAResult<HostedCallbackResult>> HandleCallbackAsync(
        IHostedSessionProvider provider,
        string rawBody,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class HostedSessionFlow : IHostedSessionFlow
{
    /// <summary>Opaque unauthenticated callback failure.</summary>
    public const string OpaqueCallbackFailure = "The callback could not be processed.";

    /// <summary>Provider guarantees that no external create request was sent.</summary>
    public const string RequestNotSentCode = "PROVIDER_REQUEST_NOT_SENT";

    /// <summary>The provider definitively refused the request (4xx, rate-limit block): nothing was created.</summary>
    public const string ProviderRefusedCode = "PROVIDER_REQUEST_REFUSED";

    private const int MaxCallbackBodyBytes = 65536;

    private readonly IHostedSessionStore _store;
    private readonly IHostedSessionOutcomeHandler _outcomeHandler;
    private readonly ILogger<HostedSessionFlow> _logger;
    private readonly TimeProvider _clock;

    public HostedSessionFlow(
        IHostedSessionStore store,
        IHostedSessionOutcomeHandler outcomeHandler,
        ILogger<HostedSessionFlow>? logger = null,
        TimeProvider? clock = null)
    {
        _store = store;
        _outcomeHandler = outcomeHandler;
        _logger = logger ?? NullLogger<HostedSessionFlow>.Instance;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<HostedSessionStartResult>> BeginAsync(
        IHostedSessionProvider provider,
        HostedSessionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);

        var capabilities = provider.GetHostedCapabilities();
        if (!capabilities.Available)
        {
            return AZOAResult<HostedSessionStartResult>.FailureWithCode(
                capabilities.UnavailableReason ?? "The hosted provider is unavailable.",
                AzoaErrorCodes.DependencyUnavailable);
        }

        if (request.SubjectUserId == Guid.Empty)
            return AZOAResult<HostedSessionStartResult>.Failure("An authenticated subject is required.");
        if (string.IsNullOrWhiteSpace(request.AdmissionKey))
            return AZOAResult<HostedSessionStartResult>.Failure("A stable admission key is required.");

        var now = _clock.GetUtcNow();
        var candidate = new HostedSession
        {
            Id = (request.SessionId ?? Guid.NewGuid()).ToString("N"),
            FlowKind = request.FlowKind,
            ProviderKey = provider.ProviderKey,
            AdmissionKey = request.AdmissionKey,
            CreationToken = Guid.NewGuid().ToString("N"),
            CreationClaimedAt = now,
            SubjectUserId = request.SubjectUserId.ToString("N"),
            TenantId = request.TenantId?.ToString("N"),
            State = HostedSessionStates.Claimed,
            StateVersion = 0,
            CreatedDate = now,
            ModifiedDate = now,
        };

        request = new HostedSessionRequest
        {
            FlowKind = request.FlowKind, SubjectUserId = request.SubjectUserId, TenantId = request.TenantId,
            AdmissionKey = request.AdmissionKey, SessionId = Guid.ParseExact(candidate.Id, "N"),
            ProviderKey = provider.ProviderKey, PolicyRevision = request.PolicyRevision,
            CustodialAuthority = request.CustodialAuthority,
        };
        if (provider is IPreparedHostedSessionProvider prepared)
        {
            var preparation = await prepared.PrepareCreationAsync(request, ct);
            if (preparation.IsError)
                return AZOAResult<HostedSessionStartResult>.FailureWithCode(preparation.Message,
                    preparation.Code ?? AzoaErrorCodes.DependencyUnavailable);
        }

        // Only the candidate owner may issue the external create.
        var claimed = await _store.ClaimAdmissionAsync(candidate, ct);
        if (claimed.IsError || claimed.Result is null)
        {
            return AZOAResult<HostedSessionStartResult>.FailureWithCode(
                claimed.Message,
                claimed.Code ?? AzoaErrorCodes.DependencyUnavailable);
        }

        var session = claimed.Result;
        if (HostedSessionStates.IsTerminal(session.State))
        {
            return AZOAResult<HostedSessionStartResult>.Failure(
                "This hosted session already reached a terminal outcome.");
        }

        // An existing claim that already has a redirect is resumed verbatim.
        if (!string.IsNullOrWhiteSpace(session.ProviderSessionId)
            && !string.IsNullOrWhiteSpace(session.RedirectUrl)
            && (session.ExpiresAt is null || session.ExpiresAt > now))
        {
            return AZOAResult<HostedSessionStartResult>.Success(Project(session, resumed: true));
        }

        if (!string.Equals(session.Id, candidate.Id, StringComparison.Ordinal)
            || !string.Equals(session.CreationToken, candidate.CreationToken, StringComparison.Ordinal))
        {
            return AZOAResult<HostedSessionStartResult>.FailureWithCode(
                session.CreationQuarantinedAt is not null || (session.CreationClaimedAt ?? session.CreatedDate) <= now.AddMinutes(-5)
                    ? "NeedsOperator: this verification requires operator reconciliation before it can continue."
                    : "Verification creation is in progress; retry shortly.",
                AzoaErrorCodes.Conflict);
        }

        var send = request.CustodialAuthority is null
            ? await _store.TryBeginCreationAsync(ParseId(session.Id), candidate.CreationToken!, _clock.GetUtcNow(), ct)
            : _store is ICustodialHostedSessionStore custodialStore
                ? await custodialStore.TryBeginCustodialCreationAsync(ParseId(session.Id), candidate.CreationToken!,
                    _clock.GetUtcNow(), request.CustodialAuthority, ct)
                : AZOAResult<HostedSession>.Failure("Custodial provider admission is unavailable.");
        if (send.IsError || send.Result is null)
            return AZOAResult<HostedSessionStartResult>.FailureWithCode("This session creation no longer owns admission.", AzoaErrorCodes.Conflict);
        session = send.Result;
        var created = await provider.CreateHostedSessionAsync(request, ct);
        if (created.IsError || created.Result is null)
        {
            // Unsent or definitively refused: no provider session exists, so the admission is released.
            var nothingCreated = created.Code is RequestNotSentCode or ProviderRefusedCode;
            if (nothingCreated)
                await _store.ReleaseUnsentAdmissionAsync(ParseId(session.Id), candidate.CreationToken!, ct);
            else
                _logger.LogWarning(
                "Provider session creation failed for {FlowKind}/{ProviderKey}; the admission claim requires reconciliation.",
                request.FlowKind,
                provider.ProviderKey);
            return new AZOAResult<HostedSessionStartResult>
            {
                IsError = true,
                Message = nothingCreated
                    ? created.Message
                    : "NeedsOperator: the provider request outcome is uncertain and requires reconciliation.",
                Code = !nothingCreated ? AzoaErrorCodes.Conflict
                    : created.RetryAfterSeconds is > 0 ? AzoaErrorCodes.OperationNotAllowed
                    : AzoaErrorCodes.DependencyUnavailable,
                RetryAfterSeconds = created.RetryAfterSeconds,
            };
        }

        var attached = await _store.TryAttachProviderSessionAsync(
            ParseId(session.Id),
            session.StateVersion,
            created.Result.ProviderSessionId,
            created.Result.ProviderCorrelationId,
            created.Result.RedirectUrl,
            created.Result.ExpiresAt,
            candidate.CreationToken!,
            ct);
        if (attached.IsError || attached.Result is null)
        {
            var current = await _store.GetByIdAsync(ParseId(session.Id), ct);
            if (!current.IsError && current.Result is { } recovered
                && recovered.ProviderSessionId == created.Result.ProviderSessionId
                && !string.IsNullOrWhiteSpace(recovered.RedirectUrl)
                && !HostedSessionStates.IsTerminal(recovered.State))
                return AZOAResult<HostedSessionStartResult>.Success(Project(recovered, resumed: true));
            if (!current.IsError && current.Result is { } closed && HostedSessionStates.IsTerminal(closed.State)
                && closed.ProviderSessionId is null)
                await DeleteLateProviderSessionAsync(provider, closed, created.Result.ProviderSessionId, ct);
            return AZOAResult<HostedSessionStartResult>.FailureWithCode(
                attached.IsError
                    ? attached.Message
                    : "The hosted session changed while its provider session was being attached.",
                attached.Code ?? AzoaErrorCodes.Conflict);
        }

        return AZOAResult<HostedSessionStartResult>.Success(Project(attached.Result, resumed: false));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<HostedCallbackResult>> HandleCallbackAsync(
        IHostedSessionProvider provider,
        string rawBody,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (string.IsNullOrWhiteSpace(rawBody) || rawBody.Length > MaxCallbackBodyBytes)
            return OpaqueFailure();

        // Step 1: constant-time secret verification. The trigger deliberately carries
        // no outcome, so there is nothing here that could be trusted by accident.
        var verified = provider.VerifyCallback(rawBody);
        if (verified.IsError || verified.Result is null)
            return OpaqueFailure();

        var located = await _store.GetByProviderSessionAsync(
            provider.FlowKind,
            provider.ProviderKey,
            verified.Result.ProviderSessionId,
            ct);
        if (located.IsError)
            return OpaqueFailure();
        if (located.Result is null)
        {
            // An unknown session and a bad secret are indistinguishable to the caller.
            _logger.LogWarning(
                "Hosted callback referenced an unknown {FlowKind} session for provider {ProviderKey}.",
                provider.FlowKind,
                provider.ProviderKey);
            return OpaqueFailure();
        }

        await _store.RecordCallbackAsync(ParseId(located.Result.Id), ct);
        return await ProcessSessionAsync(provider, located.Result, ct);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<HostedCallbackResult>> RefreshAsync(
        IHostedSessionProvider provider, Guid sessionId, CancellationToken ct = default)
    {
        var located = await _store.GetByIdAsync(sessionId, ct);
        if (located.IsError || located.Result is null
            || located.Result.FlowKind != provider.FlowKind
            || located.Result.ProviderKey != provider.ProviderKey)
            return OpaqueFailure();
        return await ProcessSessionAsync(provider, located.Result, ct);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<HostedCallbackResult>> RefreshOnDemandAsync(
        IHostedSessionProvider provider, Guid sessionId, TimeSpan cooldown, CancellationToken ct = default)
    {
        var located = await _store.GetByIdAsync(sessionId, ct);
        if (located.IsError || located.Result is null
            || located.Result.FlowKind != provider.FlowKind
            || located.Result.ProviderKey != provider.ProviderKey)
            return OpaqueFailure();
        var session = located.Result;
        if (!HostedSessionStates.IsTerminal(session.State) && _store is IHostedSessionRecoveryStore recovery)
        {
            var now = _clock.GetUtcNow();
            if ((session.LastRefreshAt is { } last && last > now - cooldown)
                || !await recovery.TryClaimRefreshAsync(session, now, ct))
                return AZOAResult<HostedCallbackResult>.Success(new HostedCallbackResult
                {
                    SessionId = sessionId,
                    State = session.State,
                    Outcome = session.AuthoritativeOutcome,
                    RefreshThrottled = true,
                });
        }
        return await ProcessSessionAsync(provider, session, ct);
    }

    private async Task<AZOAResult<HostedCallbackResult>> ProcessSessionAsync(
        IHostedSessionProvider provider, HostedSession session, CancellationToken ct)
    {
        var sessionId = ParseId(session.Id);

        if (HostedSessionStates.IsTerminal(session.State))
        {
            // Redelivery after the single terminal write: converge, do not re-decide.
            return AZOAResult<HostedCallbackResult>.Success(new HostedCallbackResult
            {
                SessionId = sessionId,
                State = session.State,
                Outcome = session.AuthoritativeOutcome,
                TerminalWriteApplied = false,
                AlreadyTerminal = true,
            });
        }

        // Step 2: the authoritative re-fetch. The callback body's `result` is discarded.
        var outcome = await provider.FetchAuthoritativeOutcomeAsync(
            session.ProviderSessionId ?? string.Empty,
            ct);
        if (outcome.IsError || outcome.Result is null)
        {
            return AZOAResult<HostedCallbackResult>.FailureWithCode(
                "The authoritative provider outcome could not be retrieved.",
                outcome.Code ?? AzoaErrorCodes.DependencyUnavailable);
        }

        // Nonterminal outcomes never resume the domain handler.
        if (!outcome.Result.IsTerminal)
        {
            var parked = await _store.TryTransitionAsync(
                sessionId,
                session.StateVersion,
                HostedSessionStates.AwaitingOutcome,
                outcome.Result.Outcome,
                outcome.Result.Detail,
                resumeTargetId: null,
                ct);
            if (parked.IsError)
                return AZOAResult<HostedCallbackResult>.FailureWithCode(parked.Message, parked.Code ?? AzoaErrorCodes.DependencyUnavailable);
            if (parked.Result is null)
                return await ReadObservedAsync(sessionId, ct);
            // Only a genuine vendor manual review is progress; "pending"/"not yet submitted" changes nothing (RV-L2 L2-1).
            if (outcome.Result.IsManualReview && _outcomeHandler is IHostedSessionProgressHandler progress)
                await progress.ObserveProgressAsync(parked.Result, outcome.Result, ct);
            return AZOAResult<HostedCallbackResult>.Success(new HostedCallbackResult
            {
                SessionId = sessionId,
                State = parked.Result.State,
                Outcome = outcome.Result.Outcome,
                TerminalWriteApplied = false,
                AlreadyTerminal = false,
            });
        }

        var leaseToken = Guid.NewGuid().ToString("N");
        var leaseNow = _clock.GetUtcNow();
        var claimed = await _store.TryClaimResumeAsync(
            sessionId, session.StateVersion, leaseToken, leaseNow,
            leaseNow.AddMinutes(2), ct);
        if (claimed.IsError)
        {
            return AZOAResult<HostedCallbackResult>.FailureWithCode(
                claimed.Message,
                claimed.Code ?? AzoaErrorCodes.DependencyUnavailable);
        }

        if (claimed.Result is null)
            return await ReadObservedAsync(sessionId, ct);

        // Only the current lease owner resumes; crash recovery requires idempotent domain writes.
        var claim = claimed.Result;
        AZOAResult<string?> resume;
        using (var work = CancellationTokenSource.CreateLinkedTokenSource(ct))
        using (var stopRenewal = new CancellationTokenSource())
        {
            var renewal = RenewLeaseAsync(sessionId, leaseToken, work, stopRenewal.Token);
            try
            {
                resume = await _outcomeHandler.ResumeAsync(claim, outcome.Result, work.Token);
                work.Token.ThrowIfCancellationRequested();
            }
            finally
            {
                await stopRenewal.CancelAsync();
                await renewal;
            }
        }
        if (resume.IsError)
        {
            // Failed domain work remains retryable.
            var released = await _store.TryFinishResumeAsync(
                sessionId,
                claim.StateVersion,
                leaseToken,
                _clock.GetUtcNow(),
                HostedSessionStates.AwaitingOutcome,
                outcome.Result.Outcome,
                resume.Message,
                resumeTargetId: null,
                ct);
            if (released.Result is null)
            {
                _logger.LogError(
                    "A failed {FlowKind} resumption could not release its claim on session {SessionId}; the next callback will re-claim it.",
                    provider.FlowKind,
                    sessionId);
            }

            return AZOAResult<HostedCallbackResult>.FailureWithCode(
                resume.Message,
                resume.Code ?? AzoaErrorCodes.DependencyUnavailable);
        }

        var terminalState = outcome.Result.IsApproved
            ? HostedSessionStates.Completed
            : HostedSessionStates.Failed;
        var transitioned = await _store.TryFinishResumeAsync(
            sessionId,
            claim.StateVersion,
            leaseToken,
            _clock.GetUtcNow(),
            terminalState,
            outcome.Result.Outcome,
            outcome.Result.Detail,
            resume.Result,
            ct);
        if (transitioned.IsError)
        {
            return AZOAResult<HostedCallbackResult>.FailureWithCode(
                transitioned.Message,
                transitioned.Code ?? AzoaErrorCodes.DependencyUnavailable);
        }

        if (transitioned.Result is null)
            return await ReadObservedAsync(sessionId, ct);
        return AZOAResult<HostedCallbackResult>.Success(new HostedCallbackResult
        {
            SessionId = sessionId,
            State = transitioned.Result.State,
            Outcome = outcome.Result.Outcome,
            TerminalWriteApplied = transitioned.Result is not null,
            AlreadyTerminal = transitioned.Result is null,
        });
    }

    /// <summary>
    /// The create succeeded after this admission was superseded or released: the vendor session has no
    /// AZOA owner, so it is deleted and the deletion audited. See Services/HostedSessions/AGENTS.md §recovery.
    /// </summary>
    private async Task DeleteLateProviderSessionAsync(
        IHostedSessionProvider provider, HostedSession closed, string providerSessionId, CancellationToken ct)
    {
        if (provider is not IDeletableHostedSessionProvider deletable)
        {
            _logger.LogError("A late {ProviderKey} session for closed admission {SessionId} cannot be deleted by this provider.",
                provider.ProviderKey, closed.Id);
            return;
        }

        var deleted = await deletable.DeleteProviderSessionAsync(providerSessionId, ct);
        var action = deleted.IsError ? "LateProviderSessionDeleteFailed" : "LateProviderSessionDeleted";
        if (deleted.IsError)
            _logger.LogError("Deleting the late {ProviderKey} session for closed admission {SessionId} failed; it stays inert.",
                provider.ProviderKey, closed.Id);
        if (_store is IHostedSessionRecoveryStore recovery)
            await recovery.AppendAuditAsync(ParseId(closed.Id), "system", action,
                "The provider create response arrived after the admission closed.", _clock.GetUtcNow(), ct);
    }

    private async Task<AZOAResult<HostedCallbackResult>> ReadObservedAsync(Guid sessionId, CancellationToken ct)
    {
        var current = await _store.GetByIdAsync(sessionId, ct);
        if (current.IsError || current.Result is null)
            return AZOAResult<HostedCallbackResult>.FailureWithCode("Hosted session could not be read.",
                current.Code ?? AzoaErrorCodes.DependencyUnavailable);
        return AZOAResult<HostedCallbackResult>.Success(new HostedCallbackResult
        {
            SessionId = sessionId,
            State = current.Result.State,
            Outcome = current.Result.AuthoritativeOutcome,
            AlreadyTerminal = HostedSessionStates.IsTerminal(current.Result.State),
            ResumeClaimLost = true,
        });
    }

    private async Task RenewLeaseAsync(
        Guid sessionId, string token, CancellationTokenSource work, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _clock, stop);
                var now = _clock.GetUtcNow();
                if (!await _store.RenewResumeLeaseAsync(sessionId, token, now, now.AddMinutes(2), work.Token))
                {
                    await work.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch
        {
            await work.CancelAsync();
            throw;
        }
    }

    private static HostedSessionStartResult Project(HostedSession session, bool resumed)
        => new()
        {
            SessionId = ParseId(session.Id),
            ProviderKey = session.ProviderKey,
            RedirectUrl = session.RedirectUrl ?? string.Empty,
            ExpiresAt = session.ExpiresAt,
            Resumed = resumed,
        };

    private static Guid ParseId(string id)
        => Guid.TryParseExact(id, "N", out var parsed) ? parsed : Guid.Empty;

    private static AZOAResult<HostedCallbackResult> OpaqueFailure()
        => AZOAResult<HostedCallbackResult>.FailureWithCode(
            OpaqueCallbackFailure,
            AzoaErrorCodes.Forbidden);
}

/// <summary>
/// Domain resumption seam: what a flow does once an authoritative TERMINAL outcome
/// exists. The KYC handler writes the submission and identity binding; a fiat-in
/// handler would settle the payment. Returns the resumed record id, if any.
/// </summary>
public interface IHostedSessionOutcomeHandler
{
    Task<AZOAResult<string?>> ResumeAsync(
        HostedSession session,
        HostedSessionOutcome outcome,
        CancellationToken ct = default);
}
