// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Bridge;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Kyc;

/// <summary>
/// The AML decision has to REACH a value gate. Identity verification says who a
/// person is; it says nothing about whether that person is sanctioned, so a
/// verified-and-sanctioned subject who could still mint, allocate, or bridge would
/// make the whole screening surface decorative.
/// </summary>
public sealed class KycAmlValueGateTests
{
    private const string ProviderKey = "veriff";

    // ── KycGateService: the consumer value gate ─────────────────────────────

    [Fact]
    public async Task ValueGate_WithACurrentApprovalAndAClearScreening_Opens()
    {
        var userId = Guid.NewGuid();
        var screening = new InMemoryKycScreeningStore();
        screening.SeedResult(userId, null, KycScreeningDecision.NO_HIT);

        var gate = NewGate(userId, screening);

        (await gate.RequireVerifiedAsync(userId)).IsError.Should().BeFalse();
    }

    [Theory]
    [InlineData(KycScreeningDecision.HIT)]
    [InlineData(KycScreeningDecision.MANUAL_REVIEW)]
    public async Task ValueGate_WithAnAdverseScreening_StaysShutDespiteAnApprovedIdentity(
        KycScreeningDecision decision)
    {
        // The exact scenario the screening surface exists for: a sanctioned person
        // passes identity verification. The submission is APPROVED and true; the value
        // gate must still refuse.
        var userId = Guid.NewGuid();
        var screening = new InMemoryKycScreeningStore();
        screening.SeedResult(userId, null, decision);

        var gate = NewGate(userId, screening);
        var result = await gate.RequireVerifiedAsync(userId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Be(
            KycAuthorizationError.Forbidden + KycAuthorizationError.VerificationRequiredMessage,
            "an adverse AML finding must be indistinguishable from any other refusal; "
            + "naming it would be tipping off");
    }

    [Fact]
    public async Task ValueGate_WithNoScreeningAtAll_StaysShut()
    {
        // Fail-closed on MISSING is the point. An approval with no AML answer is an
        // unanswered question, not a clean subject.
        var userId = Guid.NewGuid();

        var result = await NewGate(userId, new InMemoryKycScreeningStore())
            .RequireVerifiedAsync(userId);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task ValueGate_WhenTheScreeningStoreFails_StaysShut()
    {
        var userId = Guid.NewGuid();
        var screening = new InMemoryKycScreeningStore();
        screening.SeedResult(userId, null, KycScreeningDecision.NO_HIT);
        screening.FailReads = true;

        var result = await NewGate(userId, screening).RequireVerifiedAsync(userId);

        result.IsError.Should().BeTrue("an unreadable AML answer is not a clear one");
    }

    [Fact]
    public async Task ValueGate_WithATruncatedClearScreening_StaysShut()
    {
        var userId = Guid.NewGuid();
        var screening = new InMemoryKycScreeningStore();
        screening.SeedResult(userId, null, KycScreeningDecision.NO_HIT, resultsTruncated: true);

        var result = await NewGate(userId, screening).RequireVerifiedAsync(userId);

        result.IsError.Should().BeTrue("an unread sanctions list is not a clean one");
    }

    [Fact]
    public async Task ValueGate_ReadsTheMostRecentScreening_SoAMonitoringHitClosesAnOpenGate()
    {
        // Ongoing monitoring only matters if a later HIT actually shuts the gate that
        // the original NO_HIT opened.
        var userId = Guid.NewGuid();
        var screening = new InMemoryKycScreeningStore();
        screening.SeedResult(userId, null, KycScreeningDecision.NO_HIT,
            screenedAt: DateTimeOffset.UtcNow.AddDays(-10));
        screening.SeedResult(userId, null, KycScreeningDecision.HIT,
            screenedAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var result = await NewGate(userId, screening).RequireVerifiedAsync(userId);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task ValueGate_DoesNotAcceptAnotherTenantsClearScreening()
    {
        // The screening read is scoped by (user, tenant). A tenant-blind read would
        // let a clear result recorded under one tenant open a gate under another.
        var userId = Guid.NewGuid();
        var tenantA = Guid.NewGuid();
        var screening = new InMemoryKycScreeningStore();
        screening.SeedResult(userId, tenantA, KycScreeningDecision.NO_HIT);

        var result = await NewGate(userId, screening).RequireVerifiedAsync(userId);

        result.IsError.Should().BeTrue(
            "a node-direct gate must not read a tenant-scoped screening row");
    }

    // ── RealValueKycGate: the bridge / real-value gate ──────────────────────

    [Fact]
    public async Task RealValueGate_WithAnAdverseScreening_StaysShut()
    {
        var userId = Guid.NewGuid();
        var screening = new InMemoryKycScreeningStore();
        screening.SeedResult(userId, null, KycScreeningDecision.HIT);
        var store = StoreWithCurrentApproval(userId);

        var gate = new RealValueKycGate(
            store,
            screening,
            ReadyProvider(),
            Options.Create(TrustedSettings()),
            Environment(Environments.Production));

        (await gate.RequireCurrentApprovalAsync(userId)).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task RealValueGate_WithNoScreening_StaysShut()
    {
        var userId = Guid.NewGuid();
        var gate = new RealValueKycGate(
            StoreWithCurrentApproval(userId),
            new InMemoryKycScreeningStore(),
            ReadyProvider(),
            Options.Create(TrustedSettings()),
            Environment(Environments.Production));

        (await gate.RequireCurrentApprovalAsync(userId)).IsError.Should().BeTrue();
    }

    // ── The approval the outcome handler writes actually survives ───────────

    [Fact]
    public async Task HostedApproval_ExpiresOnTheSubmissionWindow_NotTheSessionWindow()
    {
        // Begin sets ExpiresAt to the ~30 minute SESSION window. Leaving it there makes
        // every approval expire minutes after it is granted, because both value gates
        // require an explicit FUTURE expiry — invisible in a fast test, total denial in
        // production.
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid().ToString("N");
        var submission = new KycSubmission
        {
            Id = "submission-1",
            UserId = userId.ToString("N"),
            ProviderSessionId = sessionId,
            Status = KycStatus.PENDING,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
        };
        var kycStore = new Mock<IKycStore>();
        kycStore.Setup(item => item.GetActiveSubmissionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<KycSubmission> { Result = submission });
        kycStore.Setup(item => item.UpsertSubmissionAsync(
                It.IsAny<KycSubmission>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((KycSubmission item, CancellationToken _) =>
                new AZOAResult<KycSubmission> { Result = item });

        var handler = NewOutcomeHandler(kycStore, KycScreeningDecision.NO_HIT, submissionExpiryDays: 30);
        var resumed = await handler.ResumeAsync(Session(sessionId, userId), ApprovedOutcome());

        resumed.IsError.Should().BeFalse(resumed.Message);
        submission.Status.Should().Be(KycStatus.APPROVED);
        submission.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(30), TimeSpan.FromMinutes(1),
            "an approval that expires with its begin-session window denies everyone");
    }

    [Fact]
    public async Task HostedApproval_ScreensBeforeBindingAndRegistersMonitoring()
    {
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid().ToString("N");
        var kycStore = new Mock<IKycStore>();
        kycStore.Setup(item => item.GetActiveSubmissionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<KycSubmission> { Result = null });
        var order = new CallOrder();
        var screening = new RecordingScreeningService(KycScreeningDecision.NO_HIT, order);
        var binding = new RecordingBindingService(order);
        var handler = new KycHostedSessionOutcomeHandler(
            kycStore.Object,
            binding,
            screening,
            Options.Create(new KycSettings()),
            NullLogger<KycHostedSessionOutcomeHandler>.Instance);

        var resumed = await handler.ResumeAsync(Session(sessionId, userId), ApprovedOutcome());

        resumed.IsError.Should().BeFalse(resumed.Message);
        screening.ScreenCount.Should().Be(1, "the AML row is what the value gate reads");
        screening.MonitorCount.Should().Be(1, "no monitor means the poll finds nothing to poll");
        // The array overload is required: ContainInOrder(params string[]) would swallow
        // a `because` argument as a fourth expected element.
        order.Steps.Should().ContainInOrder(
            new[] { "screen", "bind", "monitor" },
            "an approval that cannot be screened must not become a durable binding, and "
            + "monitoring is registered only once a binding exists to monitor");
    }

    [Fact]
    public async Task HostedApproval_WhenScreeningFails_WritesNoBindingAndNoApproval()
    {
        var userId = Guid.NewGuid();
        var kycStore = new Mock<IKycStore>();
        var screening = new RecordingScreeningService(KycScreeningDecision.NO_HIT) { FailScreen = true };
        var binding = new RecordingBindingService();
        var handler = new KycHostedSessionOutcomeHandler(
            kycStore.Object,
            binding,
            screening,
            Options.Create(new KycSettings()),
            NullLogger<KycHostedSessionOutcomeHandler>.Instance);

        var resumed = await handler.ResumeAsync(
            Session(Guid.NewGuid().ToString("N"), userId), ApprovedOutcome());

        resumed.IsError.Should().BeTrue();
        binding.BindCount.Should().Be(0);
        kycStore.Verify(item => item.UpsertSubmissionAsync(
            It.IsAny<KycSubmission>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HostedOutcome_ForAnAttemptBelongingToAnotherSession_TerminalizesNothing()
    {
        // Correlating by user alone lets a stale session's verdict terminalize
        // whichever attempt happens to be open, writing an approval the authority never
        // issued for it.
        var userId = Guid.NewGuid();
        var submission = new KycSubmission
        {
            Id = "submission-1",
            UserId = userId.ToString("N"),
            ProviderSessionId = Guid.NewGuid().ToString("N"),
            Status = KycStatus.PENDING,
        };
        var kycStore = new Mock<IKycStore>();
        kycStore.Setup(item => item.GetActiveSubmissionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<KycSubmission> { Result = submission });

        var handler = NewOutcomeHandler(kycStore, KycScreeningDecision.NO_HIT);
        var resumed = await handler.ResumeAsync(
            Session(Guid.NewGuid().ToString("N"), userId), ApprovedOutcome());

        resumed.IsError.Should().BeFalse("the session itself still records the outcome");
        submission.Status.Should().Be(KycStatus.PENDING);
        kycStore.Verify(item => item.UpsertSubmissionAsync(
            It.IsAny<KycSubmission>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static KycGateService NewGate(Guid userId, InMemoryKycScreeningStore screening)
        => new(
            StoreWithCurrentApproval(userId),
            screening,
            ReadyProvider(),
            Options.Create(TrustedSettings()),
            Environment(Environments.Production));

    private static KycHostedSessionOutcomeHandler NewOutcomeHandler(
        Mock<IKycStore> kycStore,
        KycScreeningDecision decision,
        int submissionExpiryDays = 30)
        => new(
            kycStore.Object,
            new RecordingBindingService(),
            new RecordingScreeningService(decision),
            Options.Create(new KycSettings { SubmissionExpiryDays = submissionExpiryDays }),
            NullLogger<KycHostedSessionOutcomeHandler>.Instance);

    private static HostedSession Session(string sessionId, Guid userId) => new()
    {
        Id = sessionId,
        FlowKind = HostedSessionFlowKinds.Kyc,
        ProviderKey = "identomat",
        AdmissionKey = "admission",
        SubjectUserId = userId.ToString("N"),
        State = HostedSessionStates.AwaitingSubject,
    };

    private static HostedSessionOutcome ApprovedOutcome() => new()
    {
        Outcome = "APPROVED",
        IsTerminal = true,
        IsApproved = true,
        Subject = new HostedSessionSubjectAttributes
        {
            FirstName = "Ada",
            LastName = "Lovelace",
            Birthday = "1815-12-10",
            DocumentNumber = "AB123456",
            IssuingState = "GB",
        },
    };

    private static IKycStore StoreWithCurrentApproval(Guid userId)
    {
        var store = new Mock<IKycStore>();
        var submission = new KycSubmission
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId.ToString("N"),
            Provider = KycProvider.VERIFF,
            ProviderKey = ProviderKey,
            ProviderResult = KycApprovalTrust.CreateEnvelope(Profile()),
            Status = KycStatus.APPROVED,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        store.Setup(item => item.GetLatestSubmissionByUserAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<KycSubmission> { Result = submission });
        store.Setup(item => item.GetLatestSubmissionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<KycSubmission> { Result = submission });
        return store.Object;
    }

    private static KycApprovalProfile Profile()
        => new(KycProvider.VERIFF, ProviderKey, "production-v1", "substantial");

    private static KycSettings TrustedSettings() => new()
    {
        Provider = ProviderKey,
        ApprovalPolicy = new KycApprovalPolicySettings
        {
            PolicyVersion = "production-v1",
            AssuranceLevel = "substantial",
            TrustedProviderKeys = [ProviderKey],
        },
    };

    private static IKycProviderService ReadyProvider()
    {
        var provider = new Mock<IKycProviderService>();
        provider.SetupGet(item => item.Provider).Returns(KycProvider.VERIFF);
        provider.SetupGet(item => item.ProviderKey).Returns(ProviderKey);
        provider.Setup(item => item.GetCapabilities()).Returns(new KycProviderCapabilitiesModel
        {
            Provider = KycProvider.VERIFF,
            ProviderKey = ProviderKey,
            Available = true,
        });
        return provider.Object;
    }

    private static IHostEnvironment Environment(string name)
        => Mock.Of<IHostEnvironment>(environment => environment.EnvironmentName == name);

    /// <summary>
    /// One shared, per-TEST ordering log. A static counter would be shared across
    /// every test in the class and every parallel collection, which makes an
    /// "A happened before B" assertion pass or fail on unrelated test scheduling.
    /// </summary>
    private sealed class CallOrder
    {
        private readonly List<string> _steps = [];

        public IReadOnlyList<string> Steps
        {
            get { lock (_steps) return _steps.ToList(); }
        }

        public void Record(string step)
        {
            lock (_steps) _steps.Add(step);
        }
    }

    /// <summary>Records call ORDER as well as counts; the ordering is the invariant.</summary>
    private sealed class RecordingScreeningService(
        KycScreeningDecision decision,
        CallOrder? order = null) : IIdentomatScreeningService
    {
        public bool FailScreen { get; init; }
        public int ScreenCount { get; private set; }
        public int MonitorCount { get; private set; }

        public Task<AZOAResult<KycScreeningResult>> ScreenAsync(
            Guid subjectUserId,
            Guid? tenantId,
            string fullName,
            string? birthdayTimeFrom,
            CancellationToken ct = default)
        {
            ScreenCount++;
            order?.Record("screen");
            return Task.FromResult(FailScreen
                ? AZOAResult<KycScreeningResult>.Failure("screening unavailable")
                : AZOAResult<KycScreeningResult>.Success(new KycScreeningResult
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Decision = decision,
                }));
        }

        public Task<AZOAResult<KycScreeningMonitor>> RegisterMonitorAsync(
            Guid subjectUserId,
            Guid? tenantId,
            string fullName,
            string? birthdayTimeFrom,
            CancellationToken ct = default)
        {
            MonitorCount++;
            order?.Record("monitor");
            return Task.FromResult(AZOAResult<KycScreeningMonitor>.Success(new KycScreeningMonitor
            {
                Id = Guid.NewGuid().ToString("N"),
            }));
        }

        public Task<AZOAResult<bool>> RemoveMonitorAsync(
            Guid? tenantId, string providerMonitorId, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<bool>.Success(true));

        public Task<AZOAResult<KycMonitorPollReport>> PollMonitoringAsync(CancellationToken ct = default)
            => Task.FromResult(AZOAResult<KycMonitorPollReport>.Success(new KycMonitorPollReport()));
    }

    private sealed class RecordingBindingService(CallOrder? order = null) : IKycIdentityBindingService
    {
        public int BindCount { get; private set; }

        public Task<AZOAResult<KycIdentity>> BindAsync(
            Guid userId,
            Guid? tenantId,
            KycProvider provider,
            string providerKey,
            HostedSessionSubjectAttributes attributes,
            CancellationToken ct = default)
        {
            BindCount++;
            order?.Record("bind");
            return Task.FromResult(AZOAResult<KycIdentity>.Success(new KycIdentity
            {
                Id = "binding-1",
                UserIds = [userId.ToString("N")],
            }));
        }

        public Task<AZOAResult<KycEligibilityDecision>> EvaluateEligibilityAsync(
            Guid userId, Guid? tenantId, string personKey, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<KycEligibilityDecision>.Success(
                KycEligibilityDecision.Eligible()));
    }
}
