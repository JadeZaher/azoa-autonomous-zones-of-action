// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using Moq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Kyc;
using AZOA.WebAPI.Services.HostedSessions;
using AZOA.WebAPI.Settings;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Kyc;

/// <summary>
/// Covers the <see cref="KycManager"/>-level hosted-provider admission gate that
/// replaced the blanket "every hosted provider is refused" guard once
/// <see cref="HostedSessionFlow"/> (D11) existed to claim durable admission before
/// any external provider call. The gate is exercised through a fake provider that
/// implements <see cref="IHostedSessionProvider"/> and routes its own
/// <c>BeginSessionAsync</c> through the flow exactly the way
/// <see cref="IdentomatKycProviderService"/> does, which keeps these tests
/// independent of Identomat's own wire format while still proving the seam
/// contract KycManager now trusts.
/// </summary>
public sealed class KycManagerHostedProviderGateTests
{
    // ── The real adapter satisfies the contract the gate checks for ─────────

    [Fact]
    public void IdentomatAdapter_ImplementsTheHostedSessionSeamTheGateChecksFor()
    {
        // KycManager gates on `resolution.Provider is IHostedSessionProvider`, not
        // merely on HostedVerification. Pinning this here documents that the real
        // adapter satisfies the exact contract the gate trusts.
        typeof(IdentomatKycProviderService).Should().BeAssignableTo<IHostedSessionProvider>();
    }

    // ── Task 1: a provider that implements the seam is no longer blocked ────

    [Fact]
    public async Task Begin_HostedProviderRoutedThroughHostedSessionFlow_Succeeds()
    {
        var store = new Mock<IKycStore>();
        SetupNoExistingSubmissions(store);
        SetupCreateEchoesSubmission(store);
        var hostedStore = new InMemoryHostedSessionStore();
        var flow = new HostedSessionFlow(hostedStore, new NoopOutcomeHandler());
        var provider = new FakeSeamHostedKycProvider(flow);
        var manager = NewManager(store, provider);
        var userId = Guid.NewGuid();

        var result = await manager.BeginAsync(userId);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.HostedVerification.Should().BeTrue();
        provider.VendorCreateCallCount.Should().Be(1,
            "a durable admission claim was made and the external session created exactly once");
        store.Verify(s => s.CreateSubmissionAsync(
            It.Is<KycSubmission>(submission =>
                submission.Provider == KycProvider.GENERIC_HOSTED
                && submission.HostedVerification
                && submission.UserId == userId.ToString("N")
                && !string.IsNullOrWhiteSpace(submission.ProviderSessionId)),
            It.IsAny<IReadOnlyList<KycDocument>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Task 1: fail-closed hosted stubs stay refused ────────────────────────

    [Fact]
    public async Task Begin_VeriffStub_StaysRefused_DespiteReportingHostedVerification()
    {
        var store = new Mock<IKycStore>();
        SetupNoExistingSubmissions(store);
        var provider = new VeriffKycProviderService();
        var settings = new KycSettings
        {
            Provider = "veriff",
            ApprovalPolicy = new KycApprovalPolicySettings
            {
                PolicyVersion = "veriff-v1",
                AssuranceLevel = "identity-verified",
                TrustedProviderKeys = ["veriff"],
            },
        };
        var manager = new KycManager(
            store.Object, provider, Options.Create(settings), DevelopmentEnvironment());

        var result = await manager.BeginAsync(Guid.NewGuid());

        result.IsError.Should().BeTrue();
        store.Verify(s => s.CreateSubmissionAsync(
            It.IsAny<KycSubmission>(), It.IsAny<IReadOnlyList<KycDocument>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Begin_GenericHostedScaffold_StaysRefused_DespiteReportingHostedVerification()
    {
        var store = new Mock<IKycStore>();
        SetupNoExistingSubmissions(store);
        var provider = new GenericHostedKycProviderService(Options.Create(new KycSettings()));
        var settings = new KycSettings
        {
            Provider = "hosted",
            ApprovalPolicy = new KycApprovalPolicySettings
            {
                PolicyVersion = "hosted-v1",
                AssuranceLevel = "identity-verified",
                TrustedProviderKeys = ["hosted"],
            },
        };
        var manager = new KycManager(
            store.Object, provider, Options.Create(settings), DevelopmentEnvironment());

        var result = await manager.BeginAsync(Guid.NewGuid());

        result.IsError.Should().BeTrue();
        store.Verify(s => s.CreateSubmissionAsync(
            It.IsAny<KycSubmission>(), It.IsAny<IReadOnlyList<KycDocument>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Begin_ProviderThatOnlyClaimsAvailability_IsRefusedBeforeItIsEverCalled()
    {
        // Defense-in-depth: a hosted provider that reports itself Available=true is
        // not, on its own, trusted. Only implementing IHostedSessionProvider proves
        // it claims durable admission before touching a vendor. Without this being
        // gated on the SEAM (not merely on HostedVerification), a future stub that
        // starts reporting Available=true - the way GenericHostedKycProviderService
        // and VeriffKycProviderService currently never do - would slip through.
        var store = new Mock<IKycStore>();
        SetupNoExistingSubmissions(store);
        var provider = new Mock<IKycProviderService>();
        provider.SetupGet(p => p.Provider).Returns(KycProvider.GENERIC_HOSTED);
        provider.SetupGet(p => p.ProviderKey).Returns("hosted");
        provider.Setup(p => p.GetCapabilities()).Returns(new KycProviderCapabilitiesModel
        {
            Provider = KycProvider.GENERIC_HOSTED,
            ProviderKey = "hosted",
            Available = true,
            HostedVerification = true,
        });
        var settings = new KycSettings
        {
            Provider = "hosted",
            ApprovalPolicy = new KycApprovalPolicySettings
            {
                PolicyVersion = "hosted-v1",
                AssuranceLevel = "identity-verified",
                TrustedProviderKeys = ["hosted"],
            },
        };
        var manager = new KycManager(
            store.Object, provider.Object, Options.Create(settings), DevelopmentEnvironment());

        var result = await manager.BeginAsync(Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Be(
            "KYC provider is unavailable until durable hosted-session admission is installed.");
        provider.Verify(p => p.BeginSessionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(s => s.CreateSubmissionAsync(
            It.IsAny<KycSubmission>(), It.IsAny<IReadOnlyList<KycDocument>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── The two resolution paths now admit the same providers ───────────────

    [Fact]
    public async Task Begin_SeamProviderWrappedInTheRegistrysProfileBoundDecorator_IsAdmitted()
    {
        // DELIBERATE CHANGE from the behaviour this test previously pinned.
        //
        // It used to assert that a delegate-only IKycProviderService wrapper around a
        // seam-implementing adapter stayed REFUSED, reproducing the shape
        // KycProviderRegistry.ResolveTenantAsync returned. That asymmetry was the bug,
        // not the contract: ResolveNodeDefault handed back the raw adapter and so
        // passed KycManager's `resolution.Provider is IHostedSessionProvider` gate,
        // while ResolveTenantAsync handed back a wrapper that hid the seam — so the
        // SAME provider was admitted or refused depending only on which route reached
        // it, and the tenant-scoped route is the acceptance path
        // (POST /api/tenant/custodial-accounts/{externalSubject}/kyc/session).
        //
        // The fix is in KycProviderRegistry: the wrapper now FORWARDS
        // IHostedSessionProvider when its inner adapter implements it. Unwrapping at
        // the call site was rejected because it would discard the profile-key binding
        // the wrapper exists to apply. This test now pins the forwarding behaviour, and
        // ProfileBoundWrapperForwardsTheSeam below pins it on the real registry type.
        var store = new Mock<IKycStore>();
        SetupNoExistingSubmissions(store);
        SetupCreateEchoesSubmission(store);
        var hostedStore = new InMemoryHostedSessionStore();
        var flow = new HostedSessionFlow(hostedStore, new NoopOutcomeHandler());
        var inner = new FakeSeamHostedKycProvider(flow);
        var wrapped = new SeamForwardingKycProviderServiceWrapper(inner.ProviderKey, inner);
        var manager = NewManager(store, wrapped);

        var result = await manager.BeginAsync(Guid.NewGuid());

        result.IsError.Should().BeFalse(result.Message);
        inner.VendorCreateCallCount.Should().Be(1,
            "a wrapper that forwards the seam must admit the adapter it wraps");
    }

    [Fact]
    public async Task Begin_WrapperThatHidesTheSeam_IsStillRefused()
    {
        // The gate itself is unchanged: hiding the seam still refuses. Only the
        // registry's wrapper stopped hiding it.
        var store = new Mock<IKycStore>();
        SetupNoExistingSubmissions(store);
        var hostedStore = new InMemoryHostedSessionStore();
        var flow = new HostedSessionFlow(hostedStore, new NoopOutcomeHandler());
        var inner = new FakeSeamHostedKycProvider(flow);
        var wrapped = new DelegateOnlyKycProviderServiceWrapper(inner.ProviderKey, inner);
        var manager = NewManager(store, wrapped);

        var result = await manager.BeginAsync(Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Be(
            "KYC provider is unavailable until durable hosted-session admission is installed.");
        inner.VendorCreateCallCount.Should().Be(0);
    }

    // ── Concurrency: the seam's admission claim, not KycManager's own ledger,
    //    is what stops a second provider session ──────────────────────────────

    [Fact]
    public async Task Begin_CalledTwiceForTheSameSubject_NeverCreatesASecondProviderSession()
    {
        // KycManager's own ledger is deliberately kept blind to the first attempt
        // here (GetActive/GetLatest keep returning null on both calls), isolating
        // what actually stops a second external session: HostedSessionFlow's
        // per-(user, tenant) admission claim inside the provider.
        var store = new Mock<IKycStore>();
        SetupNoExistingSubmissions(store);
        SetupCreateEchoesSubmission(store);
        var hostedStore = new InMemoryHostedSessionStore();
        var flow = new HostedSessionFlow(hostedStore, new NoopOutcomeHandler());
        var provider = new FakeSeamHostedKycProvider(flow);
        var manager = NewManager(store, provider);
        var userId = Guid.NewGuid();

        var first = await manager.BeginAsync(userId);
        var second = await manager.BeginAsync(userId);

        first.IsError.Should().BeFalse(first.Message);
        second.IsError.Should().BeFalse(second.Message);
        second.Result!.ProviderSessionId.Should().Be(first.Result!.ProviderSessionId,
            "a retry resumes the same durable claim instead of opening a second provider session");
        provider.VendorCreateCallCount.Should().Be(1,
            "two begins for one subject must converge on exactly one external provider session");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static KycManager NewManager(Mock<IKycStore> store, IKycProviderService provider)
        => new(
            store.Object,
            provider,
            Options.Create(new KycSettings
            {
                Provider = "hosted",
                ApprovalPolicy = new KycApprovalPolicySettings
                {
                    PolicyVersion = "hosted-v1",
                    AssuranceLevel = "identity-verified",
                    TrustedProviderKeys = ["hosted"],
                },
            }),
            DevelopmentEnvironment());

    private static void SetupNoExistingSubmissions(Mock<IKycStore> store)
    {
        store.Setup(s => s.GetActiveSubmissionByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new AZOAResult<KycSubmission> { Result = null });
        store.Setup(s => s.GetLatestSubmissionByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new AZOAResult<KycSubmission> { Result = null });
    }

    private static void SetupCreateEchoesSubmission(Mock<IKycStore> store)
    {
        store.Setup(s => s.CreateSubmissionAsync(
                It.IsAny<KycSubmission>(),
                It.IsAny<IReadOnlyList<KycDocument>>(),
                It.IsAny<CancellationToken>()))
             .ReturnsAsync((KycSubmission submission, IReadOnlyList<KycDocument> _, CancellationToken __) =>
                 new AZOAResult<KycSubmission> { Result = submission });
    }

    private static IHostEnvironment DevelopmentEnvironment()
        => Mock.Of<IHostEnvironment>(environment => environment.EnvironmentName == Environments.Development);

    /// <summary>
    /// A minimal hosted provider that mirrors exactly how
    /// <see cref="IdentomatKycProviderService.BeginSessionAsync(Guid, CancellationToken)"/>
    /// routes through <see cref="IHostedSessionFlow.BeginAsync"/> before ever
    /// touching a vendor, so a durable admission claim always precedes the
    /// external side effect. Reports <see cref="KycProvider.GENERIC_HOSTED"/> /
    /// <c>"hosted"</c> (rather than IDENTOMAT) purely to stay independent of the
    /// real adapter's wire format and configuration surface.
    /// </summary>
    private sealed class FakeSeamHostedKycProvider : IKycProviderService, IHostedSessionProvider
    {
        private readonly IHostedSessionFlow _flow;
        private int _vendorSessionSequence;

        public FakeSeamHostedKycProvider(IHostedSessionFlow flow) => _flow = flow;

        public KycProvider Provider => KycProvider.GENERIC_HOSTED;
        public string ProviderKey => "hosted";
        public string FlowKind => HostedSessionFlowKinds.Kyc;

        public int VendorCreateCallCount { get; private set; }

        public KycProviderCapabilitiesModel GetCapabilities() => new()
        {
            Provider = Provider,
            ProviderKey = ProviderKey,
            Available = true,
            HostedVerification = true,
            AcceptsDocumentReferences = false,
        };

        public HostedSessionCapabilities GetHostedCapabilities() => new()
        {
            ProviderKey = ProviderKey,
            FlowKind = FlowKind,
            Available = true,
        };

        public async Task<AZOAResult<KycSessionStartModel>> BeginSessionAsync(
            Guid userId, CancellationToken ct = default)
        {
            var started = await _flow.BeginAsync(
                this,
                new HostedSessionRequest
                {
                    FlowKind = FlowKind,
                    SubjectUserId = userId,
                    AdmissionKey = $"kyc:{ProviderKey}:{userId:N}:direct",
                },
                ct);
            if (started.IsError || started.Result is null)
            {
                return AZOAResult<KycSessionStartModel>.FailureWithCode(
                    started.Message, started.Code ?? AzoaErrorCodes.DependencyUnavailable);
            }

            return AZOAResult<KycSessionStartModel>.Success(new KycSessionStartModel
            {
                Provider = Provider,
                ProviderKey = ProviderKey,
                HostedVerification = true,
                AcceptsDocumentReferences = false,
                ProviderSessionId = started.Result.SessionId.ToString("N"),
                VerificationUrl = started.Result.RedirectUrl,
                ExpiresAt = started.Result.ExpiresAt?.UtcDateTime,
            });
        }

        public Task<AZOAResult<string>> CreateSessionAsync(
            Guid userId, IReadOnlyList<KycDocumentModel> documents, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<string>.Failure("Hosted; document references are not accepted."));

        public Task<AZOAResult<bool>> ValidateDocumentsAsync(
            IReadOnlyList<SubmitKycDocumentModel> documents, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<bool>.Failure("Hosted; document references are not accepted."));

        public Task<AZOAResult<KycStatus>> GetSessionStatusAsync(
            string providerSessionId, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<KycStatus>.Success(KycStatus.IN_REVIEW));

        public Task<AZOAResult<KycStatus>> HandleWebhookAsync(
            string payload, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<KycStatus>.Failure("Not exercised by these tests."));

        public Task<AZOAResult<HostedSessionCreation>> CreateHostedSessionAsync(
            HostedSessionRequest request, CancellationToken ct = default)
        {
            VendorCreateCallCount++;
            var vendorSessionId = $"vendor-session-{Interlocked.Increment(ref _vendorSessionSequence)}";
            return Task.FromResult(AZOAResult<HostedSessionCreation>.Success(new HostedSessionCreation
            {
                ProviderSessionId = vendorSessionId,
                RedirectUrl = $"https://vendor.example/redirect/{vendorSessionId}",
            }));
        }

        public AZOAResult<HostedCallbackTrigger> VerifyCallback(string rawBody)
            => AZOAResult<HostedCallbackTrigger>.Failure("Not exercised by these tests.");

        public Task<AZOAResult<HostedSessionOutcome>> FetchAuthoritativeOutcomeAsync(
            string providerSessionId, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<HostedSessionOutcome>.Failure("Not exercised by these tests."));
    }

    /// <summary>
    /// The shape the registry returns TODAY: a profile-bound wrapper that also
    /// forwards <see cref="IHostedSessionProvider"/>, so tenant-scoped resolution and
    /// node-default resolution admit the same adapter.
    /// </summary>
    private sealed class SeamForwardingKycProviderServiceWrapper(
        string providerKey,
        FakeSeamHostedKycProvider inner) : IKycProviderService, IHostedSessionProvider
    {
        public KycProvider Provider => inner.Provider;
        public string ProviderKey => providerKey;
        public string FlowKind => inner.FlowKind;

        public KycProviderCapabilitiesModel GetCapabilities()
        {
            var source = inner.GetCapabilities();
            return new KycProviderCapabilitiesModel
            {
                Provider = source.Provider,
                ProviderKey = providerKey,
                Available = source.Available,
                HostedVerification = source.HostedVerification,
                AcceptsDocumentReferences = source.AcceptsDocumentReferences,
                DevelopmentSimulation = source.DevelopmentSimulation,
                UnavailableReason = source.UnavailableReason,
            };
        }

        public HostedSessionCapabilities GetHostedCapabilities()
        {
            var source = inner.GetHostedCapabilities();
            return new HostedSessionCapabilities
            {
                ProviderKey = providerKey,
                FlowKind = source.FlowKind,
                Available = source.Available,
                MissingConfigurationKeys = source.MissingConfigurationKeys,
                UnavailableReason = source.UnavailableReason,
            };
        }

        public Task<AZOAResult<KycSessionStartModel>> BeginSessionAsync(
            Guid userId, CancellationToken ct = default)
            => inner.BeginSessionAsync(userId, ct);

        public Task<AZOAResult<string>> CreateSessionAsync(
            Guid userId, IReadOnlyList<KycDocumentModel> documents, CancellationToken ct = default)
            => inner.CreateSessionAsync(userId, documents, ct);

        public Task<AZOAResult<KycStatus>> GetSessionStatusAsync(
            string providerSessionId, CancellationToken ct = default)
            => inner.GetSessionStatusAsync(providerSessionId, ct);

        public Task<AZOAResult<KycStatus>> HandleWebhookAsync(
            string payload, CancellationToken ct = default)
            => inner.HandleWebhookAsync(payload, ct);

        public Task<AZOAResult<bool>> ValidateDocumentsAsync(
            IReadOnlyList<SubmitKycDocumentModel> documents, CancellationToken ct = default)
            => inner.ValidateDocumentsAsync(documents, ct);

        public Task<AZOAResult<HostedSessionCreation>> CreateHostedSessionAsync(
            HostedSessionRequest request, CancellationToken ct = default)
            => inner.CreateHostedSessionAsync(request, ct);

        public AZOAResult<HostedCallbackTrigger> VerifyCallback(string rawBody)
            => inner.VerifyCallback(rawBody);

        public Task<AZOAResult<HostedSessionOutcome>> FetchAuthoritativeOutcomeAsync(
            string providerSessionId, CancellationToken ct = default)
            => inner.FetchAuthoritativeOutcomeAsync(providerSessionId, ct);
    }

    /// <summary>
    /// A wrapper that HIDES the seam. Retained so the gate's own refusal stays pinned
    /// even though the registry no longer produces this shape.
    /// </summary>
    private sealed class DelegateOnlyKycProviderServiceWrapper(string providerKey, IKycProviderService inner)
        : IKycProviderService
    {
        public KycProvider Provider => inner.Provider;
        public string ProviderKey => providerKey;

        public KycProviderCapabilitiesModel GetCapabilities()
        {
            var source = inner.GetCapabilities();
            return new KycProviderCapabilitiesModel
            {
                Provider = source.Provider,
                ProviderKey = providerKey,
                Available = source.Available,
                HostedVerification = source.HostedVerification,
                AcceptsDocumentReferences = source.AcceptsDocumentReferences,
                DevelopmentSimulation = source.DevelopmentSimulation,
                UnavailableReason = source.UnavailableReason,
            };
        }

        public Task<AZOAResult<KycSessionStartModel>> BeginSessionAsync(
            Guid userId, CancellationToken ct = default)
            => inner.BeginSessionAsync(userId, ct);

        public Task<AZOAResult<string>> CreateSessionAsync(
            Guid userId, IReadOnlyList<KycDocumentModel> documents, CancellationToken ct = default)
            => inner.CreateSessionAsync(userId, documents, ct);

        public Task<AZOAResult<KycStatus>> GetSessionStatusAsync(
            string providerSessionId, CancellationToken ct = default)
            => inner.GetSessionStatusAsync(providerSessionId, ct);

        public Task<AZOAResult<KycStatus>> HandleWebhookAsync(
            string payload, CancellationToken ct = default)
            => inner.HandleWebhookAsync(payload, ct);

        public Task<AZOAResult<bool>> ValidateDocumentsAsync(
            IReadOnlyList<SubmitKycDocumentModel> documents, CancellationToken ct = default)
            => inner.ValidateDocumentsAsync(documents, ct);
    }

    private sealed class NoopOutcomeHandler : IHostedSessionOutcomeHandler
    {
        public Task<AZOAResult<string?>> ResumeAsync(
            HostedSession session, HostedSessionOutcome outcome, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<string?>.Success(null));
    }
}
