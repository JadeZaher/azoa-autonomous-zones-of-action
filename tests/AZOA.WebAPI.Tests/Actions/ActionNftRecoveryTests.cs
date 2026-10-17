using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Moq;

namespace AZOA.WebAPI.Tests.Actions;

public sealed class ActionNftRecoveryTests
{
    [Fact]
    public async Task SolanaMintRecoveryWaitsForExplicitCostsWithoutCreatingOrSigningLifecycle()
    {
        var h = new Fixture(chain: "Solana");
        var first = await h.Operations.ObserveAsync(h.Reference, h.Frozen);
        first.Status.Should().Be(ActionExternalOperationStatus.Pending);
        (await h.Operations.ObserveAsync(h.Reference, h.Frozen)).Should().BeEquivalentTo(first);
        (await h.Rail.Store.GetAsync(h.Reference.OperationId!)).Should().BeNull();
        h.Rail.Prepared.Should().BeEmpty();
        h.Rail.Submitted.Should().BeEmpty();
    }

    [Fact]
    public async Task RefundUsesFrozenConfirmedTransferAndWaitsForActualHoldersSignature()
    {
        var rail = new NftLifecycleHarness();
        var actor = Guid.NewGuid(); var holder = Guid.NewGuid(); var run = Guid.NewGuid();
        var wallet = rail.AddWallet(actor); var recipient = rail.AddWallet(holder);
        var mint = await rail.Service.MintAsync(actor, actor, NftLifecycleHarness.MintRequest(), "original-mint", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(actor));
        var sourceId = Guid.NewGuid();
        var transfer = new NftTransferNodeConfig { NftId = mint.Result!.NftId,
            Request = new() { WalletId = wallet.Id, TargetUserId = holder } };
        var source = Freeze(run, sourceId, ActionNodeType.Transfer, actor, transfer, new Dictionary<Guid, ActionNodeExecution>());
        rail.AuthorizeWorkflow(source.Frozen, source.Reference);
        var moved = await rail.Service.MoveAsync(actor, mint.Result.NftId, recipient.Address, false, source.Reference.RequestKey,
            actionAuthority: new(run, sourceId, actor, null, source.Reference.IntentHash), credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(actor));
        moved.Result!.State.Should().Be("Completed");
        var prior = new Dictionary<Guid, ActionNodeExecution> { [sourceId] = new()
            { Id = Guid.NewGuid(), RunId = run, NodeId = sourceId, State = ActionNodeState.Succeeded,
                FrozenInput = source.Frozen, ExternalOperation = source.Reference } };
        var refund = Freeze(run, Guid.NewGuid(), ActionNodeType.Refund, actor,
            new RefundNodeConfig { NftId = mint.Result.NftId, TransferNodeId = sourceId }, prior);
        rail.AuthorizeWorkflow(refund.Frozen, refund.Reference);
        var service = new ActionNftOperationService(new NftManager(rail.Nfts.Object, rail.Wallets.Object, rail.Service),
            Mock.Of<ICredentialManager>(), rail.Store, rail.Service, Mock.Of<IActionNodeExecutionStore>(), rail.Service);
        var count = rail.Prepared.Count;
        var pending = await service.ObserveAsync(refund.Reference, refund.Frozen);
        pending.Status.Should().Be(ActionExternalOperationStatus.Pending);
        pending.Output.Should().BeNull();
        rail.Prepared.Should().HaveCount(count);
        (await rail.Service.RefreshAsync(actor, refund.Reference.OperationId!, true, credential: NftLifecycleHarness.Credential(actor), acceptance: await rail.Acceptance(refund.Reference.OperationId!))).IsError.Should().BeTrue();
        rail.Prepared.Should().HaveCount(count);
        (await rail.Service.RefreshAsync(holder, refund.Reference.OperationId!, true, credential: NftLifecycleHarness.Credential(holder), acceptance: await rail.Acceptance(refund.Reference.OperationId!))).Result!.State.Should().Be("Completed");
        (await service.ObserveAsync(refund.Reference, refund.Frozen)).Status.Should().Be(ActionExternalOperationStatus.Succeeded);
        rail.Prepared.Last().SignerAddress.Should().Be(recipient.Address);
    }

    [Fact]
    public async Task CrashBeforeDomainAdmissionCreatesDiscoverableIntentWithoutAnySignature()
    {
        var h = new Fixture();
        var first = await h.Operations.ObserveAsync(h.Reference, h.Frozen);
        first.Status.Should().Be(ActionExternalOperationStatus.Pending);
        var row = await h.Rail.Store.GetAsync(h.Reference.OperationId!);
        row.Should().NotBeNull();
        row!.State.Should().Be("Preparing");
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson)!;
        command.ActionAuthority!.IntentHash.Should().Be(h.Reference.IntentHash);
        h.Rail.Prepared.Should().BeEmpty();
        h.Rail.Submitted.Should().BeEmpty();

        await h.Rail.Service.RefreshAsync(h.Actor, row.Id, false);
        var completed = await h.Operations.ObserveAsync(h.Reference, h.Frozen);
        completed.Status.Should().Be(ActionExternalOperationStatus.Succeeded);
        completed.TransactionId.Should().NotBeNullOrWhiteSpace();
        completed.Output.Should().Contain("Confirmed");
    }

    [Fact]
    public async Task SamePublicRequestKeyCannotImpersonateFrozenWorkflowAuthority()
    {
        var h = new Fixture();
        var direct = await h.Rail.Service.MintAsync(h.Actor, h.Actor,
            NftLifecycleOperationProjection.MintModel(h.Request), h.Reference.RequestKey,
            credential: NftLifecycleHarness.Credential(h.Actor));
        direct.IsError.Should().BeFalse();
        var prepared = h.Rail.Prepared.Count;
        var submitted = h.Rail.Submitted.Count;
        var observed = await h.Operations.ObserveAsync(h.Reference, h.Frozen);
        observed.Status.Should().Be(ActionExternalOperationStatus.Ambiguous);
        h.Rail.Prepared.Should().HaveCount(prepared);
        h.Rail.Submitted.Should().HaveCount(submitted);
    }

    [Fact]
    public async Task InvalidFrozenConfigurationHasDurableFailureAndCannotBecomeNewMint()
    {
        var h = new Fixture(invalid: true);
        var first = await h.Operations.ObserveAsync(h.Reference, h.Frozen);
        first.Status.Should().Be(ActionExternalOperationStatus.Failed);
        var row = await h.Rail.Store.GetAsync(h.Reference.OperationId!);
        row!.Stage.Should().Be("AdmissionFailure");
        (await h.Operations.ObserveAsync(h.Reference, h.Frozen)).Should().BeEquivalentTo(first);
        var changed = NftLifecycleHarness.MintRequest();
        changed.WalletId = h.Request.WalletId;
        (await h.Rail.Service.MintAsync(h.Actor, h.Actor, changed, h.Reference.RequestKey, credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(h.Actor))).IsError.Should().BeTrue();
        h.Rail.Prepared.Should().BeEmpty();
        h.Rail.Submitted.Should().BeEmpty();
    }

    [Fact]
    public async Task ChangedEnvelopeCannotAdmitOrObserveAnotherActorsRequest()
    {
        var h = new Fixture();
        var observed = await h.Operations.ObserveAsync(h.Reference with { ActorUserId = Guid.NewGuid() }, h.Frozen);
        observed.Status.Should().Be(ActionExternalOperationStatus.Ambiguous);
        (await h.Rail.Store.GetAsync(h.Reference.OperationId!)).Should().BeNull();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("completed")]
    [InlineData("changed-input")]
    [InlineData("changed-witness")]
    public async Task WorkflowAdmissionCannotReplaceMissingOrChangedOriginalAuthority(string change)
    {
        var h = new Fixture();
        var execution = h.Rail.AuthorizeWorkflow(h.Frozen, h.Reference);
        if (change == "missing") execution.SigningAuthorityJson = null;
        if (change == "completed") execution.State = ActionNodeState.Succeeded;
        if (change == "changed-input") execution.FrozenInput = "{}";
        if (change == "changed-witness")
        {
            var witness = JsonSerializer.Deserialize<AZOA.WebAPI.Models.Signing.WorkflowSigningWitness>(execution.SigningAuthorityJson!)!;
            execution.SigningAuthorityJson = JsonSerializer.Serialize(witness with
            {
                Action = witness.Action with { IntentHash = new string('f', 64) },
            });
        }

        await h.Operations.ObserveAsync(h.Reference, h.Frozen);

        (await h.Rail.Store.GetAsync(h.Reference.OperationId!)).Should().BeNull();
        h.Rail.Prepared.Should().BeEmpty();
        h.Rail.Submitted.Should().BeEmpty();
    }

    private static (string Frozen, ActionExternalOperationReference Reference) Freeze(Guid run, Guid nodeId,
        ActionNodeType type, Guid actor, object config, IReadOnlyDictionary<Guid, ActionNodeExecution> previous)
    {
        var json = JsonSerializer.SerializeToElement(config);
        var context = new ActionNodeExecutionContext(run, nodeId, new ActionDefinition
            { Id = Guid.NewGuid(), Nodes = [new ActionNode { Id = nodeId, NodeType = type, Config = json.GetRawText() }] }, actor,
            allRunExecutions: previous);
        var input = new ActionFrozenNodeInput(1, run, nodeId, type, actor, null, json,
            new Dictionary<Guid, ActionNodeExecution>(), previous);
        var frozen = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(input));
        return (frozen, ActionNftOperationService.Reference(context, CanonicalJson.HashCanonical(frozen)));
    }

    private sealed class Fixture
    {
        internal NftLifecycleHarness Rail { get; } = new();
        internal Guid Actor { get; } = Guid.NewGuid();
        internal NftMintRequest Request { get; }
        internal ActionNftOperationService Operations { get; }
        internal ActionExternalOperationReference Reference { get; }
        internal string Frozen { get; }

        internal Fixture(bool invalid = false, string chain = "Algorand")
        {
            var wallet = Rail.AddWallet(Actor);
            Request = new() { WalletId = wallet.Id, ChainId = chain, MetadataUri = invalid ? "" : "ipfs://metadata", Name = "Proof" };
            var node = new ActionNode { Id = Guid.NewGuid(), NodeType = ActionNodeType.NftMint,
                Config = JsonSerializer.Serialize(Request) };
            var context = new ActionNodeExecutionContext(Guid.NewGuid(), node.Id,
                new ActionDefinition { Id = Guid.NewGuid(), Nodes = [node] }, Actor);
            var input = new ActionFrozenNodeInput(1, context.RunId, node.Id, node.NodeType, Actor, null,
                JsonSerializer.SerializeToElement(Request), new Dictionary<Guid, ActionNodeExecution>(), new Dictionary<Guid, ActionNodeExecution>());
            Frozen = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(input));
            Reference = ActionNftOperationService.Reference(context, CanonicalJson.HashCanonical(Frozen));
            Rail.AuthorizeWorkflow(Frozen, Reference);
            Operations = new(new NftManager(Rail.Nfts.Object, Rail.Wallets.Object, Rail.Service),
                Mock.Of<ICredentialManager>(), Rail.Store, Rail.Service, Mock.Of<IActionNodeExecutionStore>(), Rail.Service);
        }
    }
}
