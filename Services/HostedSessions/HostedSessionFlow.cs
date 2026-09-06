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
    /// <summary>
    /// Claims durable admission, then creates the provider session, then returns the
    /// redirect. The claim happens FIRST so a crash cannot orphan a provider session.
    /// </summary>
    Task<AZOAResult<HostedSessionStartResult>> BeginAsync(
        IHostedSessionProvider provider,
        HostedSessionRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Processes an untrusted inbound callback: constant-time secret verification,
    /// an AUTHORITATIVE server-side re-fetch, a CAS resume CLAIM, the domain
    /// resumption on the winning branch only, then exactly one CAS terminal write.
    /// The callback body's own claimed outcome is discarded.
    /// </summary>
    Task<AZOAResult<HostedCallbackResult>> HandleCallbackAsync(
        IHostedSessionProvider provider,
        string rawBody,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class HostedSessionFlow : IHostedSessionFlow
{
    /// <summary>
    /// Single opaque failure surfaced to an unauthenticated caller. A callback
    /// endpoint must not distinguish "bad secret" from "unknown session": either
    /// discrimination turns the endpoint into an oracle.
    /// </summary>
    public const string OpaqueCallbackFailure = "The callback could not be processed.";

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
            Id = Guid.NewGuid().ToString("N"),
            FlowKind = request.FlowKind,
            ProviderKey = provider.ProviderKey,
            AdmissionKey = request.AdmissionKey,
            SubjectUserId = request.SubjectUserId.ToString("N"),
            TenantId = request.TenantId?.ToString("N"),
            State = HostedSessionStates.Claimed,
            StateVersion = 0,
            CreatedDate = now,
            ModifiedDate = now,
        };

        // Durable admission BEFORE any external side effect. Losing the race returns
        // the winner's row, so two concurrent begins never create two provider sessions.
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

        var created = await provider.CreateHostedSessionAsync(request, ct);
        if (created.IsError || created.Result is null)
        {
            // The claim stays NON-TERMINAL on purpose. Marking it failed here would
            // brick this subject permanently: the same admission key would resume a
            // terminal row forever, so one transient provider outage would deny that
            // subject KYC for good. Leaving the claim open lets the next attempt
            // resume it and retry the create, and it still cannot produce two
            // provider sessions because there is only ever one claim.
            _logger.LogWarning(
                "Provider session creation failed for {FlowKind}/{ProviderKey}; the admission claim stays open for retry.",
                request.FlowKind,
                provider.ProviderKey);
            return AZOAResult<HostedSessionStartResult>.FailureWithCode(
                created.Message,
                created.Code ?? AzoaErrorCodes.DependencyUnavailable);
        }

        var attached = await _store.TryAttachProviderSessionAsync(
            ParseId(session.Id),
            session.StateVersion,
            created.Result.ProviderSessionId,
            created.Result.ProviderCorrelationId,
            created.Result.RedirectUrl,
            created.Result.ExpiresAt,
            ct);
        if (attached.IsError || attached.Result is null)
        {
            return AZOAResult<HostedSessionStartResult>.FailureWithCode(
                attached.IsError
                    ? attached.Message
                    : "The hosted session changed while its provider session was being attached.",
                attached.Code ?? AzoaErrorCodes.Conflict);
        }

        return AZOAResult<HostedSessionStartResult>.Success(Project(attached.Result, resumed: false));
    }

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

        var session = located.Result;
        var sessionId = ParseId(session.Id);
        await _store.RecordCallbackAsync(sessionId, ct);

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
            session.ProviderSessionId ?? verified.Result.ProviderSessionId,
            ct);
        if (outcome.IsError || outcome.Result is null)
        {
            return AZOAResult<HostedCallbackResult>.FailureWithCode(
                "The authoritative provider outcome could not be retrieved.",
                outcome.Code ?? AzoaErrorCodes.DependencyUnavailable);
        }

        // Step 3: a non-terminal verdict parks the session. It must never be written
        // as a terminal state, because a terminal state is what opens a value gate.
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
            return AZOAResult<HostedCallbackResult>.Success(new HostedCallbackResult
            {
                SessionId = sessionId,
                State = parked.Result?.State ?? HostedSessionStates.AwaitingOutcome,
                Outcome = outcome.Result.Outcome,
                TerminalWriteApplied = false,
                AlreadyTerminal = false,
            });
        }

        // Step 4: CLAIM the resume BEFORE running it. The terminal check at the top
        // of this method read a snapshot, so two concurrent deliveries can both pass
        // it; if resumption ran before the CAS they would BOTH resume and the loser's
        // divergent outcome would be discarded silently AFTER its ledger write had
        // landed — a session recorded FAILED while the submission says APPROVED, with
        // the value gate reading only the submission.
        var claimed = await _store.TryTransitionAsync(
            sessionId,
            session.StateVersion,
            HostedSessionStates.Resuming,
            outcome.Result.Outcome,
            outcome.Result.Detail,
            resumeTargetId: null,
            ct);
        if (claimed.IsError)
        {
            return AZOAResult<HostedCallbackResult>.FailureWithCode(
                claimed.Message,
                claimed.Code ?? AzoaErrorCodes.DependencyUnavailable);
        }

        if (claimed.Result is null)
        {
            // Another delivery holds the claim. Doing nothing is the correct answer:
            // the holder owns both the domain write and the terminal transition.
            var current = await _store.GetByIdAsync(sessionId, ct);
            var observed = current.Result;
            return AZOAResult<HostedCallbackResult>.Success(new HostedCallbackResult
            {
                SessionId = sessionId,
                State = observed?.State ?? HostedSessionStates.Resuming,
                Outcome = observed?.AuthoritativeOutcome ?? outcome.Result.Outcome,
                TerminalWriteApplied = false,
                AlreadyTerminal = HostedSessionStates.IsTerminal(observed?.State),
                ResumeClaimLost = true,
            });
        }

        // Step 5: exactly one delivery reaches here, so the domain side effect runs once.
        var claim = claimed.Result;
        var resume = await _outcomeHandler.ResumeAsync(session, outcome.Result, ct);
        if (resume.IsError)
        {
            // Compensate: release the claim back to a NON-TERMINAL state so the next
            // delivery retries. Terminalising a session whose domain write failed
            // would strand an approval that no ledger row backs.
            var released = await _store.TryTransitionAsync(
                sessionId,
                claim.StateVersion,
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
        var transitioned = await _store.TryTransitionAsync(
            sessionId,
            claim.StateVersion,
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

        return AZOAResult<HostedCallbackResult>.Success(new HostedCallbackResult
        {
            SessionId = sessionId,
            State = transitioned.Result?.State ?? terminalState,
            Outcome = outcome.Result.Outcome,
            TerminalWriteApplied = transitioned.Result is not null,
            AlreadyTerminal = transitioned.Result is null,
        });
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
