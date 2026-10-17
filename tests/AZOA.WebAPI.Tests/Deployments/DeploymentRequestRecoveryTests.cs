using System.Text.Json;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Deployments;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Moq;
using Run = AZOA.WebAPI.Models.Actions.ActionRun;
using ActionDefinition = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Tests.Deployments;

public sealed class DeploymentRequestRecoveryTests
{
    [Fact]
    public async Task AncestryInspectionUsesOwnedCurrentHeadAndAdvancesOnlyOnePage()
    {
        var store = new InMemoryDeploymentStore(); var actor = Guid.NewGuid();
        var parent = (await store.ClaimAsync(new DeploymentOperation { Id = "parent", ActorUserId = actor.ToString("N") }))!;
        var head = (await store.ClaimRecoveryChildAsync(parent, new DeploymentOperation { Id = "head", ParentOperationId = parent.Id,
            ActorUserId = parent.ActorUserId, RecoveryBindingHash = "binding", ReplacesOperationId = "prior", RecoveryMode = "AbortCleanup" }))!;
        var audits = new Mock<IDeploymentRecoveryAuditStore>(MockBehavior.Strict);
        audits.Setup(item => item.ClaimAsync(It.Is<DeploymentOperation>(row => row.Id == parent.Id && row.RecoveryChildId == head.Id),
            It.Is<DeploymentOperation>(row => row.Id == head.Id), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentRecoveryAudit { Id = "audit", HeadOperationId = head.Id, State = "Running", Version = 4 });
        audits.Setup(item => item.AdvanceAsync("audit", 4, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentRecoveryAudit { Id = "audit", HeadOperationId = head.Id, State = "Running", InspectedCount = 25, Digest = "first-page" });
        var service = Service(store, Mock.Of<IFrozenActionRuntime>(), audits.Object);
        (await service.InspectRecoveryAncestryAsync(parent.Id, Guid.NewGuid(), null)).IsError.Should().BeTrue();
        audits.Verify(item => item.ClaimAsync(It.IsAny<DeploymentOperation>(), It.IsAny<DeploymentOperation>(), It.IsAny<CancellationToken>()), Times.Never);
        var inspected = await service.InspectRecoveryAncestryAsync(parent.Id, actor, null);
        inspected.IsError.Should().BeFalse(inspected.Message);
        inspected.Result!.HeadOperationId.Should().Be(head.Id);
        inspected.Result.InspectedCount.Should().Be(25);
        audits.Verify(item => item.AdvanceAsync("audit", 4, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExpiredRecoveryQuoteCanBeInvalidatedWithoutSigningAndExactRetryIsStable()
    {
        var store = new InMemoryDeploymentStore();
        var actor = Guid.NewGuid();
        var service = Service(store, Mock.Of<IFrozenActionRuntime>());
        var target = new DeploymentTarget("NativeContract", "Solana", "Devnet");
        var originalIntent = new NativeOperationIntent(1, "parent", "Deploy", "Solana", "Devnet", "genesis", "hash",
            JsonSerializer.SerializeToElement(new { }), [], new("10", "0", "0", ""));
        var parent = new DeploymentOperation { Id = "parent", ActorUserId = actor.ToString("N"),
            ArtifactId = "artifact", ArtifactHash = "hash", State = "Failed", RequestJson = JsonSerializer.Serialize(new DeploymentStartRequest("artifact", "hash", target, originalIntent)),
            HistoryJson = JsonSerializer.Serialize(new[] { new NativeStageHistory(new("CreateBuffer", "tx", "wire", "genesis", "{}", "1", "0"),
                new("Confirmed", "tx", "genesis")) }) };
        parent = (await store.ClaimAsync(parent))!;
        var childId = AZOA.WebAPI.Helpers.Deployments.DeploymentIdentity.Operation(actor, null, "expired-recovery");
        var intent = new NativeOperationIntent(1, childId, "Recovery", "Solana", "Devnet", "genesis", "hash",
            JsonSerializer.SerializeToElement(new NativeRecoveryInput(originalIntent, JsonSerializer.Deserialize<List<NativeStageHistory>>(parent.HistoryJson)!, "AbortCleanup", "payer", parent.Version)), [], new("1", "0", "0", "expired"),
            new("expired", "hash", "genesis", "1", "0", "0", "{}", DateTimeOffset.UtcNow.AddDays(-1)));
        intent = intent with { Quote = intent.Quote! with { IntentHash = AZOA.WebAPI.Helpers.Deployments.NativeOperationAuthorization.IntentHash(intent) } };
        await store.AddQuoteAsync(new NativeQuote { Id = "expired", ActorUserId = actor.ToString("N"), IntentHash = intent.Quote!.IntentHash,
            QuoteJson = JsonSerializer.Serialize(intent.Quote), ExpiresAt = intent.Quote.ExpiresAt });
        var binding = AZOA.WebAPI.Helpers.CanonicalJson.Hash(new { parent.Id, parent.Version, parent.Generation, parent.RequestJson, parent.HistoryJson,
            IntentHash = AZOA.WebAPI.Helpers.Deployments.NativeOperationAuthorization.IntentHash(intent) });
        var request = new DeploymentRecoveryStartRequest("expired-recovery", new(parent.Version, binding, intent), true);
        var forgedVersion = request with { Quote = request.Quote with { ParentVersion = parent.Version - 1 } };
        (await service.InvalidateRecoveryAsync("parent", actor, null, forgedVersion)).IsError.Should().BeTrue();
        (await store.GetAsync(parent.Id))!.Version.Should().Be(parent.Version);
        await store.ClaimAsync(new DeploymentOperation { Id = "other", ActorUserId = actor.ToString("N"),
            ArtifactId = "artifact", ArtifactHash = "hash", State = "Failed", Version = parent.Version + 2,
            RequestJson = JsonSerializer.Serialize(new DeploymentStartRequest("artifact", "hash", target, originalIntent with { OperationKey = "other" })) });
        (await service.InvalidateRecoveryAsync("other", actor, null, request)).IsError.Should().BeTrue();
        (await store.GetAsync(parent.Id))!.Version.Should().Be(parent.Version);
        (await service.InvalidateRecoveryAsync("parent", Guid.NewGuid(), null, request)).IsError.Should().BeTrue();
        var invalidated = await service.InvalidateRecoveryAsync("parent", actor, null, request);
        invalidated.IsError.Should().BeFalse(invalidated.Message);
        invalidated.Result!.Version.Should().Be(parent.Version + 1);
        invalidated.Result.Stage.Should().Be("RecoveryQuoteInvalidated");
        var replay = await service.InvalidateRecoveryAsync("parent", actor, null, request);
        replay.Result!.Version.Should().Be(invalidated.Result.Version);
        (await service.StartRecoveryAsync("parent", actor, null, request)).IsError.Should().BeTrue();
        store.Operations.Should().HaveCount(2);
    }

    [Fact]
    public async Task LinkedRecoveryReplayUsesOriginalReceiptAfterQuoteExpiryAndParentProjection()
    {
        var store = new InMemoryDeploymentStore();
        var service = Service(store, Mock.Of<IFrozenActionRuntime>());
        var actor = Guid.NewGuid();
        var target = new DeploymentTarget("NativeContract", "Solana", "Devnet");
        var original = new DeploymentStartRequest("artifact", "hash", target);
        await store.ClaimAsync(new DeploymentOperation { Id = "parent", ActorUserId = actor.ToString("N"),
            ArtifactId = "artifact", ArtifactHash = "hash", RequestJson = JsonSerializer.Serialize(original), State = "AbortedClean" });
        var quote = new NativeOperationQuote("expired", "intent", "solana:genesis", "1", "0", "0", "{}", DateTimeOffset.UtcNow.AddDays(-1));
        var childId = AZOA.WebAPI.Helpers.Deployments.DeploymentIdentity.Operation(actor, null, "recovery-key");
        var intent = new NativeOperationIntent(1, childId, "Recovery", "Solana", "Devnet", "solana:genesis", "hash",
            JsonSerializer.SerializeToElement(new { }), [], new("1", "0", "0", quote.QuoteId), quote);
        var childRequest = new DeploymentStartRequest("artifact", "hash", target, intent);
        await store.ClaimAsync(new DeploymentOperation { Id = childId, ActorUserId = actor.ToString("N"), ParentOperationId = "parent",
            ArtifactId = "artifact", ArtifactHash = "hash", RecoveryBindingHash = "binding", RecoveryMode = "AbortCleanup",
            RequestJson = AZOA.WebAPI.Helpers.CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(childRequest)), State = "Confirmed" });
        var replay = await service.StartRecoveryAsync("parent", actor, null, new("recovery-key", new(0, "binding", intent), true));
        replay.IsError.Should().BeFalse(replay.Message);
        replay.Result!.OperationId.Should().Be(childId);
        replay.Result.State.Should().Be("Confirmed");
        var changed = intent with { Budget = intent.Budget with { MaximumNetworkFee = "2" } };
        (await service.StartRecoveryAsync("parent", actor, null, new("recovery-key", new(0, "binding", changed), true))).IsError.Should().BeTrue();
        (await service.StartRecoveryAsync("parent", Guid.NewGuid(), null, new("recovery-key", new(0, "binding", intent), true))).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task GenericAdmissionCannotBypassLinkedRecoveryFence()
    {
        var service = Service(new InMemoryDeploymentStore(), Mock.Of<IFrozenActionRuntime>());
        var intent = new NativeOperationIntent(1, "operation", "Recovery", "Solana", "Devnet", "genesis", "hash",
            JsonSerializer.SerializeToElement(new { }), [], new("1", "0", "0", ""));
        var request = new DeploymentStartRequest("artifact", "hash", new("NativeContract", "Solana", "Devnet"), intent);
        (await service.AdmitAsync(Guid.NewGuid(), null, "key", request)).IsError.Should().BeTrue();
        (await service.QuoteAsync(Guid.NewGuid(), null, new("artifact", "key", intent))).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task CancellationBeforeAdmissionFencesDelayedCreatorEvenIfItsQuoteExpired()
    {
        var store = new InMemoryDeploymentStore();
        var service = Service(store, Mock.Of<IFrozenActionRuntime>());
        var actor = Guid.NewGuid();
        var request = new DeploymentStartRequest("missing-or-expired-artifact", "hash", new("NativeContract", "Solana", "Devnet"));
        var cancelled = await service.CancelRequestAsync(new("original-key", request), actor, null);
        cancelled.Result!.State.Should().Be("Cancelled");
        var delayed = await service.StartAsync(actor, null, "original-key", request);
        delayed.IsError.Should().BeFalse(delayed.Message);
        delayed.Result!.OperationId.Should().Be(cancelled.Result.OperationId);
        delayed.Result.State.Should().Be("Cancelled");
        (await service.GetRequestAsync("original-key", actor, null)).Result!.State.Should().Be("Cancelled");
        (await service.GetRequestAsync("original-key", Guid.NewGuid(), null)).IsError.Should().BeTrue();
        store.Operations.Should().ContainSingle();
        (await service.CancelRequestAsync(new("original-key", request with { ArtifactId = "different" }), actor, null)).IsError.Should().BeTrue();
        (await service.StartAsync(actor, null, "original-key", request with { ArtifactId = "different" })).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task KeyCancellationCannotOverwriteSubmissionAuthorization()
    {
        var store = new InMemoryDeploymentStore(); var actor = Guid.NewGuid();
        var request = new DeploymentStartRequest("artifact", "hash", new("NativeContract", "Solana", "Devnet"));
        await store.ClaimAsync(new DeploymentOperation { Id = AZOA.WebAPI.Helpers.Deployments.DeploymentIdentity.Operation(actor, null, "key"),
            ActorUserId = actor.ToString("N"), ArtifactId = "artifact", ArtifactHash = "hash", RequestJson = AZOA.WebAPI.Helpers.CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(request)),
            IntentHash = AZOA.WebAPI.Helpers.CanonicalJson.Hash(request),
            State = "Submitted", SubmissionAuthorized = true });
        var result = await Service(store, Mock.Of<IFrozenActionRuntime>()).CancelRequestAsync(new("key", request), actor, null);
        result.IsError.Should().BeTrue();
        store.Operations.Single().State.Should().Be("Submitted");
        store.Operations.Single().SubmissionAuthorized.Should().BeTrue();
    }

    [Fact]
    public async Task ExactInvocationReplayPrecedesRevokedBundleOrResourceChecks()
    {
        var store = new InMemoryDeploymentStore(); var actor = Guid.NewGuid(); var graph = new ActionDefinition { Id = Guid.NewGuid() };
        var artifact = new RuntimeArtifact([], graph, []);
        var target = new DeploymentTarget("PlatformRuntime", "Algorand", "Devnet");
        var payload = new DeploymentArtifactPayload(2, Guid.NewGuid(), actor, null, target, artifact, null);
        await store.AddArtifactAsync(new DeploymentArtifact { Id = "artifact", ArtifactHash = "hash", PayloadHash = "payload", PayloadJson = JsonSerializer.Serialize(payload) });
        await store.ClaimAsync(new DeploymentOperation { Id = "operation", ActorUserId = actor.ToString("N"), ArtifactId = "artifact", ArtifactHash = "hash",
            State = "Confirmed", RequestJson = JsonSerializer.Serialize(new DeploymentStartRequest("artifact", "hash", target)) });
        var runtime = new Mock<IFrozenActionRuntime>(); var admitted = new Run { Id = Guid.NewGuid(), UserId = actor };
        runtime.Setup(item => item.TryReplayAsync("operation", "key", It.IsAny<RuntimeArtifact>(), actor, null, It.IsAny<JsonElement>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<Run>.Success(admitted));
        var service = Service(store, runtime.Object);
        var replay = await service.InvokeAsync("operation", actor, null, "key", new("payload", JsonSerializer.SerializeToElement(new { amount = "9" }), true));
        replay.Result!.Id.Should().Be(admitted.Id);
        runtime.Verify(item => item.StartAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<RuntimeArtifact>(), It.IsAny<Guid>(), It.IsAny<Guid?>(),
            It.IsAny<JsonElement>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<DeploymentAuthorityWitness?>(),
            It.IsAny<AZOA.WebAPI.Models.Signing.SigningCredentialWitness?>()), Times.Never);
        (await service.InvokeAsync("operation", actor, null, "key", new("wrong-payload", JsonSerializer.SerializeToElement(new { }), true))).IsError.Should().BeTrue();
    }

    private static DeploymentService Service(InMemoryDeploymentStore store, IFrozenActionRuntime runtime, IDeploymentRecoveryAuditStore? audits = null)
        => new(store, Mock.Of<IActionGraphBundleStore>(MockBehavior.Strict), Mock.Of<IActionStore>(MockBehavior.Strict), Mock.Of<IHolonManager>(MockBehavior.Strict),
            new DeploymentArtifactCompiler(Mock.Of<IActionDagValidator>()), Mock.Of<IBlockchainProviderFactory>(MockBehavior.Strict),
            Mock.Of<IValueAccessService>(MockBehavior.Strict), Mock.Of<INodeGovernanceGuard>(MockBehavior.Strict), runtime,
            Mock.Of<IWalletStore>(MockBehavior.Strict), Mock.Of<ITenantConsentGate>(MockBehavior.Strict), Mock.Of<IEcosystemStore>(MockBehavior.Strict), Mock.Of<IDappSeriesStore>(MockBehavior.Strict), audits ?? Mock.Of<IDeploymentRecoveryAuditStore>(MockBehavior.Strict));
}
