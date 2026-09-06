// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.HostedSessions;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Kyc;

public sealed class HostedSessionFlowTests
{
    private const string ProviderKey = "identomat";
    private const string ProviderSessionId = "parent-session-1";

    // ── The callback is a trigger, not an authority ─────────────────────────

    [Fact]
    public async Task Callback_DiscardsItsOwnResult_AndRefetchesTheAuthority()
    {
        // The callback claims APPROVED. The authoritative fetch says REJECTED.
        // The authority must win, or a forged callback becomes an approval.
        var store = new InMemoryHostedSessionStore();
        store.SeedAwaitingSubject(
            HostedSessionFlowKinds.Kyc, ProviderKey, ProviderSessionId, Guid.NewGuid());
        var provider = new FakeHostedProvider
        {
            CallbackClaimedOutcome = "APPROVED",
            AuthoritativeOutcome = Terminal("REJECTED", approved: false),
        };
        var flow = NewFlow(store);

        var handled = await flow.HandleCallbackAsync(provider, "body");

        handled.IsError.Should().BeFalse();
        provider.AuthoritativeFetchCount.Should().Be(1,
            "the /result/ re-fetch is the only source of truth");
        handled.Result!.Outcome.Should().Be("REJECTED");
        handled.Result.State.Should().Be(HostedSessionStates.Failed);
    }

    [Fact]
    public async Task Callback_WithRejectedSecret_NeverReachesTheAuthoritativeFetch()
    {
        var store = new InMemoryHostedSessionStore();
        store.SeedAwaitingSubject(
            HostedSessionFlowKinds.Kyc, ProviderKey, ProviderSessionId, Guid.NewGuid());
        var provider = new FakeHostedProvider { RejectCallback = true };
        var flow = NewFlow(store);

        var handled = await flow.HandleCallbackAsync(provider, "body");

        handled.IsError.Should().BeTrue();
        provider.AuthoritativeFetchCount.Should().Be(0);
        store.CallbackRecordCount.Should().Be(0);
    }

    [Fact]
    public async Task Callback_ForUnknownSession_IsIndistinguishableFromABadSecret()
    {
        var store = new InMemoryHostedSessionStore();
        var badSecret = new FakeHostedProvider { RejectCallback = true };
        var unknownSession = new FakeHostedProvider();
        var flow = NewFlow(store);

        var rejected = await flow.HandleCallbackAsync(badSecret, "body");
        var unknown = await flow.HandleCallbackAsync(unknownSession, "body");

        rejected.Message.Should().Be(unknown.Message,
            "distinguishing the two turns the endpoint into an oracle for an "
            + "unauthenticated caller");
        rejected.Code.Should().Be(unknown.Code);
    }

    // ── MANUAL_CHECK never terminalises ─────────────────────────────────────

    [Fact]
    public async Task Callback_WithNonTerminalOutcome_DoesNotTakeATerminalWrite()
    {
        var store = new InMemoryHostedSessionStore();
        var seeded = store.SeedAwaitingSubject(
            HostedSessionFlowKinds.Kyc, ProviderKey, ProviderSessionId, Guid.NewGuid());
        var handler = new RecordingOutcomeHandler();
        var provider = new FakeHostedProvider
        {
            AuthoritativeOutcome = new HostedSessionOutcome
            {
                Outcome = "MANUAL_CHECK",
                IsTerminal = false,
                IsApproved = false,
            },
        };
        var flow = NewFlow(store, handler);

        var handled = await flow.HandleCallbackAsync(provider, "body");

        handled.Result!.State.Should().Be(HostedSessionStates.AwaitingOutcome);
        handled.Result.TerminalWriteApplied.Should().BeFalse();
        store.TerminalWriteCount.Should().Be(0);
        handler.ResumeCount.Should().Be(0,
            "the domain must not resume on an undecided verdict");
        HostedSessionStates.IsTerminal(store.Peek(ParseId(seeded.Id))!.State).Should().BeFalse();
    }

    [Fact]
    public async Task NonTerminalOutcome_CanStillTerminaliseOnALaterCallback()
    {
        var store = new InMemoryHostedSessionStore();
        store.SeedAwaitingSubject(
            HostedSessionFlowKinds.Kyc, ProviderKey, ProviderSessionId, Guid.NewGuid());
        var provider = new FakeHostedProvider
        {
            AuthoritativeOutcome = new HostedSessionOutcome
            {
                Outcome = "MANUAL_CHECK",
                IsTerminal = false,
            },
        };
        var flow = NewFlow(store);

        await flow.HandleCallbackAsync(provider, "body");
        provider.AuthoritativeOutcome = Terminal("APPROVED", approved: true);
        var second = await flow.HandleCallbackAsync(provider, "body");

        second.Result!.State.Should().Be(HostedSessionStates.Completed);
        second.Result.TerminalWriteApplied.Should().BeTrue();
        store.TerminalWriteCount.Should().Be(1);
    }

    // ── Redelivery converges on exactly one terminal write ──────────────────

    /// <summary>
    /// SEQUENTIAL redeliveries. This proves convergence and nothing about
    /// concurrency: deliveries 2-5 short-circuit on the terminal check before they
    /// reach any race, so <c>ResumeCount == 1</c> here is true by construction.
    /// <see cref="ConcurrentCallbacks_RunDomainResumptionExactlyOnce"/> is the test
    /// that actually exercises two deliveries in flight at the same time.
    /// </summary>
    [Fact]
    public async Task RedeliveredCallbacksInSequence_ConvergeOnExactlyOneTerminalWrite()
    {
        var store = new InMemoryHostedSessionStore();
        store.SeedAwaitingSubject(
            HostedSessionFlowKinds.Kyc, ProviderKey, ProviderSessionId, Guid.NewGuid());
        var handler = new RecordingOutcomeHandler();
        var provider = new FakeHostedProvider
        {
            AuthoritativeOutcome = Terminal("APPROVED", approved: true),
        };
        var flow = NewFlow(store, handler);

        var deliveries = new List<HostedCallbackResult>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var handled = await flow.HandleCallbackAsync(provider, "body");
            handled.IsError.Should().BeFalse();
            deliveries.Add(handled.Result!);
        }

        store.TerminalWriteCount.Should().Be(1,
            "the vendor documents no dedup or retry semantics, so the CAS write is "
            + "the only thing keeping five redeliveries from applying five outcomes");
        deliveries.Count(delivery => delivery.TerminalWriteApplied).Should().Be(1);
        deliveries.Skip(1).Should().OnlyContain(delivery => delivery.AlreadyTerminal);
        deliveries.Should().OnlyContain(delivery => delivery.State == HostedSessionStates.Completed);
        handler.ResumeCount.Should().Be(1, "the domain side effect must run once");
    }

    // ── Genuinely concurrent deliveries ─────────────────────────────────────

    [Fact]
    public async Task ConcurrentCallbacks_RunDomainResumptionExactlyOnce()
    {
        // TWO deliveries in flight at once, rendezvoused so both read the SAME
        // non-terminal snapshot before either writes — the exact interleaving a
        // sequential loop can never produce, because deliveries 2..n short-circuit on
        // the terminal check.
        //
        // The two authoritative fetches deliberately DISAGREE. If resumption ran
        // before the CAS, both would resume: one would write submission=REJECTED and
        // the other submission=APPROVED, the losing CAS would be swallowed as the
        // expected redelivery outcome, and the session would record one verdict while
        // the ledger recorded the other. The value gate reads the ledger.
        var store = new InMemoryHostedSessionStore();
        var seeded = store.SeedAwaitingSubject(
            HostedSessionFlowKinds.Kyc, ProviderKey, ProviderSessionId, Guid.NewGuid());
        var handler = new RecordingOutcomeHandler();
        var provider = new RendezvousHostedProvider(
            arrivals: 2,
            outcomes:
            [
                Terminal("REJECTED", approved: false),
                Terminal("APPROVED", approved: true),
            ]);
        var flow = NewFlow(store, handler);

        var deliveries = await Task.WhenAll(
            flow.HandleCallbackAsync(provider, "body"),
            flow.HandleCallbackAsync(provider, "body"));

        provider.AuthoritativeFetchCount.Should().Be(2,
            "both deliveries must genuinely be in flight for this to test anything");
        handler.ResumeCount.Should().Be(1,
            "the resume claim is taken BEFORE resumption, so only the winner may run "
            + "the domain side effect; two resumptions would write two different "
            + "submission verdicts and silently discard one");
        store.TerminalWriteCount.Should().Be(1);

        var final = store.Peek(ParseId(seeded.Id))!;
        HostedSessionStates.IsTerminal(final.State).Should().BeTrue();
        final.AuthoritativeOutcome.Should().Be(handler.LastOutcome,
            "the session's recorded verdict and the verdict the ledger was written "
            + "with must be the same one; divergence is the whole defect");
        deliveries.Should().Contain(delivery => delivery.Result!.ResumeClaimLost,
            "the delivery that lost the claim must report that it did no domain work");
    }

    [Fact]
    public async Task ConcurrentCallbacks_WhenTheWinnerFailsToResume_ReleaseTheClaimForRetry()
    {
        // A failed resumption must leave the session NON-TERMINAL. With the claim
        // taken first, that requires an explicit compensating transition — without it
        // the session would sit in RESUMING forever and no later callback could act.
        var store = new InMemoryHostedSessionStore();
        var seeded = store.SeedAwaitingSubject(
            HostedSessionFlowKinds.Kyc, ProviderKey, ProviderSessionId, Guid.NewGuid());
        var handler = new RecordingOutcomeHandler { FailResume = true };
        var provider = new FakeHostedProvider
        {
            AuthoritativeOutcome = Terminal("APPROVED", approved: true),
        };
        var flow = NewFlow(store, handler);

        var failed = await flow.HandleCallbackAsync(provider, "body");

        failed.IsError.Should().BeTrue();
        store.TerminalWriteCount.Should().Be(0);
        var parked = store.Peek(ParseId(seeded.Id))!;
        parked.State.Should().Be(HostedSessionStates.AwaitingOutcome,
            "the claim must be released, not held, or the session is stranded");
        HostedSessionStates.IsTerminal(parked.State).Should().BeFalse();

        // And the retry succeeds against the released claim.
        var retryHandler = new RecordingOutcomeHandler();
        var retried = await NewFlow(store, retryHandler).HandleCallbackAsync(provider, "body");

        retried.IsError.Should().BeFalse(retried.Message);
        retryHandler.ResumeCount.Should().Be(1);
        store.TerminalWriteCount.Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentCallbacksAtTheSameVersion_ProduceOneWinner()
    {
        // Both deliveries read the SAME state version, as two racing HTTP requests
        // would. Only the first conditional update may apply.
        var store = new InMemoryHostedSessionStore();
        var seeded = store.SeedAwaitingSubject(
            HostedSessionFlowKinds.Kyc, ProviderKey, ProviderSessionId, Guid.NewGuid());
        var sessionId = ParseId(seeded.Id);

        var first = await store.TryTransitionAsync(
            sessionId, seeded.StateVersion, HostedSessionStates.Completed, "APPROVED", null, "sub-1");
        var second = await store.TryTransitionAsync(
            sessionId, seeded.StateVersion, HostedSessionStates.Completed, "APPROVED", null, "sub-1");

        first.Result.Should().NotBeNull();
        second.Result.Should().BeNull("the stale version must lose");
        second.IsError.Should().BeFalse("losing a CAS race is expected, not an error");
        store.TerminalWriteCount.Should().Be(1);
    }

    [Fact]
    public async Task ATerminalSessionRejectsAFurtherTransitionEvenAtTheCurrentVersion()
    {
        var store = new InMemoryHostedSessionStore();
        var seeded = store.SeedAwaitingSubject(
            HostedSessionFlowKinds.Kyc, ProviderKey, ProviderSessionId, Guid.NewGuid());
        var sessionId = ParseId(seeded.Id);
        await store.TryTransitionAsync(
            sessionId, seeded.StateVersion, HostedSessionStates.Failed, "REJECTED", null, null);
        var current = store.Peek(sessionId)!;

        var flip = await store.TryTransitionAsync(
            sessionId, current.StateVersion, HostedSessionStates.Completed, "APPROVED", null, null);

        flip.Result.Should().BeNull(
            "a terminal outcome must not be rewritten into its opposite by a late callback");
        store.Peek(sessionId)!.AuthoritativeOutcome.Should().Be("REJECTED");
    }

    // ── Begin claims admission before any external call ─────────────────────

    [Fact]
    public async Task Begin_ClaimsAdmissionBeforeCreatingTheProviderSession()
    {
        var store = new ClaimOrderRecordingStore();
        var provider = new FakeHostedProvider();
        var flow = new HostedSessionFlow(store, new RecordingOutcomeHandler());

        var started = await flow.BeginAsync(provider, NewRequest());

        started.IsError.Should().BeFalse();
        store.ClaimSequence.Should().BeLessThan(provider.CreateSequence,
            "a provider session created before a durable claim can be orphaned by a "
            + "crash, leaving a session no callback can ever be matched to");
    }

    [Fact]
    public async Task Begin_WithTheSameAdmissionKeyTwice_ResumesInsteadOfCreatingASecondSession()
    {
        var store = new InMemoryHostedSessionStore();
        var provider = new FakeHostedProvider();
        var flow = NewFlow(store);
        var request = NewRequest();

        var first = await flow.BeginAsync(provider, request);
        var second = await flow.BeginAsync(provider, request);

        first.Result!.SessionId.Should().Be(second.Result!.SessionId);
        second.Result.Resumed.Should().BeTrue();
        provider.CreateCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Begin_AfterATransientProviderFailure_CanStillSucceedOnRetry()
    {
        // Marking the claim terminal on a create failure would brick this subject
        // forever: the same admission key would resume a terminal row on every
        // future attempt, so one provider outage would deny them KYC permanently.
        var store = new InMemoryHostedSessionStore();
        var provider = new FakeHostedProvider { FailCreateOnce = true };
        var flow = NewFlow(store);
        var request = NewRequest();

        var failed = await flow.BeginAsync(provider, request);
        var retried = await flow.BeginAsync(provider, request);

        failed.IsError.Should().BeTrue();
        retried.IsError.Should().BeFalse("the open claim must be resumable after a transient outage");
        retried.Result!.RedirectUrl.Should().NotBeNullOrEmpty();
        provider.CreateCallCount.Should().Be(2);
        store.TerminalWriteCount.Should().Be(0, "a transient failure is not a terminal outcome");
    }

    [Fact]
    public async Task Begin_WithAnUnavailableProvider_NeverTouchesTheStore()
    {
        var store = new ClaimOrderRecordingStore();
        var provider = new FakeHostedProvider { Available = false };
        var flow = new HostedSessionFlow(store, new RecordingOutcomeHandler());

        var started = await flow.BeginAsync(provider, NewRequest());

        started.IsError.Should().BeTrue();
        store.ClaimSequence.Should().Be(0);
        provider.CreateCallCount.Should().Be(0);
    }

    // ── Resumption failure must not terminalise ─────────────────────────────

    [Fact]
    public async Task WhenDomainResumptionFails_TheSessionIsNotTerminalised()
    {
        var store = new InMemoryHostedSessionStore();
        store.SeedAwaitingSubject(
            HostedSessionFlowKinds.Kyc, ProviderKey, ProviderSessionId, Guid.NewGuid());
        var handler = new RecordingOutcomeHandler { FailResume = true };
        var provider = new FakeHostedProvider
        {
            AuthoritativeOutcome = Terminal("APPROVED", approved: true),
        };
        var flow = NewFlow(store, handler);

        var handled = await flow.HandleCallbackAsync(provider, "body");

        handled.IsError.Should().BeTrue();
        store.TerminalWriteCount.Should().Be(0,
            "terminalising a session whose domain write failed would strand an "
            + "approval that no ledger row backs");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static HostedSessionFlow NewFlow(
        IHostedSessionStore store,
        IHostedSessionOutcomeHandler? handler = null)
        => new(store, handler ?? new RecordingOutcomeHandler());

    private static HostedSessionRequest NewRequest() => new()
    {
        FlowKind = HostedSessionFlowKinds.Kyc,
        SubjectUserId = Guid.NewGuid(),
        AdmissionKey = "kyc:identomat:subject:direct",
    };

    private static HostedSessionOutcome Terminal(string outcome, bool approved) => new()
    {
        Outcome = outcome,
        IsTerminal = true,
        IsApproved = approved,
        Subject = approved
            ? new HostedSessionSubjectAttributes
            {
                FirstName = "Ada",
                DocumentNumber = "AB123456",
                IssuingState = "GB",
            }
            : null,
    };

    private static Guid ParseId(string id) => Guid.ParseExact(id, "N");

    private sealed class FakeHostedProvider : IHostedSessionProvider
    {
        private static int _sequence;

        public string FlowKind => HostedSessionFlowKinds.Kyc;
        public string ProviderKey => HostedSessionFlowTests.ProviderKey;
        public bool Available { get; init; } = true;
        public bool RejectCallback { get; init; }
        public bool FailCreateOnce { get; init; }
        public string? CallbackClaimedOutcome { get; init; }
        public HostedSessionOutcome AuthoritativeOutcome { get; set; } = new()
        {
            Outcome = "APPROVED",
            IsTerminal = true,
            IsApproved = true,
        };

        public int CreateCallCount { get; private set; }
        public int CreateSequence { get; private set; }
        public int AuthoritativeFetchCount { get; private set; }

        public HostedSessionCapabilities GetHostedCapabilities() => new()
        {
            ProviderKey = ProviderKey,
            FlowKind = FlowKind,
            Available = Available,
            UnavailableReason = Available ? null : "unconfigured",
        };

        public Task<AZOAResult<HostedSessionCreation>> CreateHostedSessionAsync(
            HostedSessionRequest request,
            CancellationToken ct = default)
        {
            CreateCallCount++;
            CreateSequence = Interlocked.Increment(ref _sequence);
            if (FailCreateOnce && CreateCallCount == 1)
            {
                return Task.FromResult(AZOAResult<HostedSessionCreation>.FailureWithCode(
                    "transient provider outage",
                    AzoaErrorCodes.DependencyUnavailable));
            }

            return Task.FromResult(AZOAResult<HostedSessionCreation>.Success(new HostedSessionCreation
            {
                ProviderSessionId = ProviderSessionId,
                ProviderCorrelationId = "child-session-1",
                RedirectUrl = "https://widget.identomat.com/?session_token=child-session-1",
            }));
        }

        public AZOAResult<HostedCallbackTrigger> VerifyCallback(string rawBody)
        {
            if (RejectCallback)
            {
                return AZOAResult<HostedCallbackTrigger>.FailureWithCode(
                    HostedSessionFlow.OpaqueCallbackFailure,
                    AzoaErrorCodes.Forbidden);
            }

            // The claimed outcome is deliberately NOT propagated: the trigger type
            // has no field for it.
            _ = CallbackClaimedOutcome;
            return AZOAResult<HostedCallbackTrigger>.Success(new HostedCallbackTrigger
            {
                ProviderSessionId = ProviderSessionId,
                EventType = "session-change",
            });
        }

        public Task<AZOAResult<HostedSessionOutcome>> FetchAuthoritativeOutcomeAsync(
            string providerSessionId,
            CancellationToken ct = default)
        {
            AuthoritativeFetchCount++;
            return Task.FromResult(AZOAResult<HostedSessionOutcome>.Success(AuthoritativeOutcome));
        }
    }

    private sealed class RecordingOutcomeHandler : IHostedSessionOutcomeHandler
    {
        private int _resumeCount;

        public int ResumeCount => Volatile.Read(ref _resumeCount);
        public bool FailResume { get; init; }

        /// <summary>The verdict the ledger write was actually made with.</summary>
        public string? LastOutcome { get; private set; }

        public Task<AZOAResult<string?>> ResumeAsync(
            HostedSession session,
            HostedSessionOutcome outcome,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _resumeCount);
            LastOutcome = outcome.Outcome;
            return Task.FromResult(FailResume
                ? AZOAResult<string?>.Failure("domain resumption failed")
                : AZOAResult<string?>.Success("submission-1"));
        }
    }

    /// <summary>
    /// Holds every caller inside <see cref="FetchAuthoritativeOutcomeAsync"/> until
    /// the expected number of deliveries has arrived, then releases them all. That
    /// rendezvous is what forces both to have read the same non-terminal snapshot,
    /// which no amount of sequential looping can produce. Each caller gets its OWN
    /// outcome so a second resumption would be visibly divergent rather than
    /// coincidentally identical.
    /// </summary>
    private sealed class RendezvousHostedProvider(
        int arrivals,
        IReadOnlyList<HostedSessionOutcome> outcomes) : IHostedSessionProvider
    {
        private readonly SemaphoreSlim _released = new(0);
        private int _arrived;

        public string FlowKind => HostedSessionFlowKinds.Kyc;
        public string ProviderKey => HostedSessionFlowTests.ProviderKey;

        public int AuthoritativeFetchCount => Volatile.Read(ref _arrived);

        public HostedSessionCapabilities GetHostedCapabilities() => new()
        {
            ProviderKey = ProviderKey,
            FlowKind = FlowKind,
            Available = true,
        };

        public Task<AZOAResult<HostedSessionCreation>> CreateHostedSessionAsync(
            HostedSessionRequest request, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<HostedSessionCreation>.Failure("not exercised"));

        public AZOAResult<HostedCallbackTrigger> VerifyCallback(string rawBody)
            => AZOAResult<HostedCallbackTrigger>.Success(new HostedCallbackTrigger
            {
                ProviderSessionId = ProviderSessionId,
                EventType = "session-change",
            });

        public async Task<AZOAResult<HostedSessionOutcome>> FetchAuthoritativeOutcomeAsync(
            string providerSessionId,
            CancellationToken ct = default)
        {
            var index = Interlocked.Increment(ref _arrived) - 1;
            if (index == arrivals - 1)
                _released.Release(arrivals);

            await _released.WaitAsync(TimeSpan.FromSeconds(10), ct);
            _released.Release();

            return AZOAResult<HostedSessionOutcome>.Success(outcomes[index % outcomes.Count]);
        }
    }

    /// <summary>Records the order of claim versus external create.</summary>
    private sealed class ClaimOrderRecordingStore : IHostedSessionStore
    {
        private static int _sequence;
        private readonly InMemoryHostedSessionStore _inner = new();

        public int ClaimSequence { get; private set; }

        public Task<AZOAResult<HostedSession>> ClaimAdmissionAsync(
            HostedSession candidate,
            CancellationToken ct = default)
        {
            ClaimSequence = Interlocked.Increment(ref _sequence);
            return _inner.ClaimAdmissionAsync(candidate, ct);
        }

        public Task<AZOAResult<HostedSession>> GetByProviderSessionAsync(
            string flowKind, string providerKey, string providerSessionId, CancellationToken ct = default)
            => _inner.GetByProviderSessionAsync(flowKind, providerKey, providerSessionId, ct);

        public Task<AZOAResult<HostedSession>> GetByIdAsync(Guid id, CancellationToken ct = default)
            => _inner.GetByIdAsync(id, ct);

        public Task<AZOAResult<HostedSession>> TryAttachProviderSessionAsync(
            Guid id, long expectedStateVersion, string providerSessionId, string? providerCorrelationId,
            string redirectUrl, DateTimeOffset? expiresAt, CancellationToken ct = default)
            => _inner.TryAttachProviderSessionAsync(
                id, expectedStateVersion, providerSessionId, providerCorrelationId, redirectUrl, expiresAt, ct);

        public Task<AZOAResult<HostedSession>> TryTransitionAsync(
            Guid id, long expectedStateVersion, string newState, string? authoritativeOutcome,
            string? outcomeDetail, string? resumeTargetId, CancellationToken ct = default)
            => _inner.TryTransitionAsync(
                id, expectedStateVersion, newState, authoritativeOutcome, outcomeDetail, resumeTargetId, ct);

        public Task<AZOAResult<bool>> RecordCallbackAsync(Guid id, CancellationToken ct = default)
            => _inner.RecordCallbackAsync(id, ct);
    }
}
