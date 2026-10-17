using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Moq;
using AZOA.WebAPI.Models;

namespace AZOA.WebAPI.Tests.Actions;

public sealed class ActionCredentialRevokeRecoveryTests
{
    [Fact]
    public async Task IssuerRecoveryAdmitsWithoutSigningThenProjectsOnlyConfirmedRevocation()
    {
        var fixture = await Fixture.CreateAsync();
        var (reference, frozen) = fixture.Intent(fixture.Issuer);
        var preparedBefore = fixture.Rail.Prepared.Count;
        var admitted = await fixture.Operations.ObserveAsync(reference, frozen);
        admitted.Status.Should().Be(ActionExternalOperationStatus.Pending);
        fixture.Rail.Prepared.Count.Should().Be(preparedBefore);
        var row = (await fixture.Rail.Store.GetAsync(reference.OperationId!))!;
        row.State.Should().Be("Preparing");
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson)!;
        command.Actor.Should().Be(fixture.Issuer);
        command.SourceUserId.Should().Be(fixture.Holder);
        command.SourceAddress.Should().Be(fixture.Holder.ToString("N"));
        command.ActionAuthority!.IntentHash.Should().Be(reference.IntentHash);

        fixture.Rail.Confirm = false;
        (await fixture.Rail.Service.RefreshAsync(fixture.Issuer, row.Id, false)).Result!.State.Should().Be("Submitted");
        var pending = await fixture.Operations.ObserveAsync(reference, frozen);
        pending.Status.Should().Be(ActionExternalOperationStatus.Pending);
        pending.Output.Should().BeNull();
        var prepared = fixture.Rail.Prepared.Count;
        fixture.Rail.Confirm = true;
        var completed = await fixture.Operations.ObserveAsync(reference, frozen);
        completed.Status.Should().Be(ActionExternalOperationStatus.Succeeded);
        completed.TransactionId.Should().NotBeNullOrWhiteSpace();
        var output = JsonSerializer.Deserialize<CredentialRevokeResult>(completed.Output!, ActionNodeJson.Options)!;
        output.IssuerUserId.Should().Be(fixture.Issuer);
        output.HolderUserId.Should().Be(fixture.Holder);
        output.HolderAddress.Should().Be(fixture.Holder.ToString("N"));
        output.AssetId.Should().Be(fixture.AssetId);
        output.Status.Should().Be(OperationStatus.Completed);
        output.LifecycleId.Should().Be(reference.OperationId);
        output.TxHash.Should().Be(completed.TransactionId);
        (await fixture.Rail.Store.GetAuthorityAsync(fixture.NftId.ToString("N")))!.Revoked.Should().BeTrue();

        fixture.Rail.Access.Setup(access => access.RequireValueAccessAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<bool>.Failure("Authority revoked after settlement."));
        (await fixture.Operations.ObserveAsync(reference, frozen)).Should().BeEquivalentTo(completed);
        fixture.Rail.Prepared.Count.Should().Be(prepared);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HolderAndUnrelatedActorCannotAdmitIssuerRevocation(bool holder)
    {
        var fixture = await Fixture.CreateAsync();
        var actor = holder ? fixture.Holder : Guid.NewGuid();
        fixture.Rail.AddWallet(actor);
        var (reference, frozen) = fixture.Intent(actor);
        var prepared = fixture.Rail.Prepared.Count;

        (await fixture.Operations.ObserveAsync(reference, frozen)).Status.Should().Be(ActionExternalOperationStatus.Ambiguous);
        (await fixture.Rail.Store.GetAsync(reference.OperationId!)).Should().BeNull();
        fixture.Rail.Prepared.Count.Should().Be(prepared);
        (await fixture.Rail.Store.GetAuthorityAsync(fixture.NftId.ToString("N")))!.Revoked.Should().BeFalse();
    }

    [Fact]
    public async Task SameIssuerRequestCannotBeReboundToChangedReasonOrTenant()
    {
        var fixture = await Fixture.CreateAsync();
        var (reference, frozen) = fixture.Intent(fixture.Issuer);
        await fixture.Operations.ObserveAsync(reference, frozen);
        var original = (await fixture.Rail.Store.GetAsync(reference.OperationId!))!;
        var input = JsonSerializer.Deserialize<ActionFrozenNodeInput>(frozen)!;
        var config = input.Config.Deserialize<CredentialRevokeNodeConfig>()!;
        config.Reason = "Changed after original admission.";
        var changed = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(input with { Config = JsonSerializer.SerializeToElement(config) }));

        (await fixture.Operations.ObserveAsync(reference with { IntentHash = CanonicalJson.HashCanonical(changed) }, changed))
            .Status.Should().Be(ActionExternalOperationStatus.Ambiguous);
        (await fixture.Operations.ObserveAsync(reference with { ActingTenantId = Guid.NewGuid() }, frozen))
            .Status.Should().Be(ActionExternalOperationStatus.Ambiguous);
        (await fixture.Rail.Store.GetAsync(reference.OperationId!)).Should().BeEquivalentTo(original);
    }

    private sealed class Fixture
    {
        internal NftLifecycleHarness Rail { get; } = new();
        internal Guid Issuer { get; } = Guid.NewGuid();
        internal Guid Holder { get; } = Guid.NewGuid();
        internal Guid NftId { get; private set; }
        internal string AssetId { get; private set; } = "";
        internal ActionNftOperationService Operations { get; }

        private Fixture()
        {
            Rail.AddWallet(Issuer);
            Rail.AddWallet(Holder);
            Operations = new(Mock.Of<INftManager>(), new CredentialManager(Rail.Store, Rail.Service, Rail.Factory.Object),
                Rail.Store, Rail.Service, Mock.Of<IActionNodeExecutionStore>(), Rail.Service);
        }

        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            var request = NftLifecycleHarness.MintRequest(transferable: false);
            request.IsSoulbound = true;
            var issued = await fixture.Rail.Service.MintAsync(fixture.Issuer, fixture.Holder, request, "confirmed-issuer-proof", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(fixture.Issuer));
            issued.IsError.Should().BeFalse();
            issued.Result!.State.Should().Be("AwaitingRecipientAcceptance");
            (await fixture.Rail.Service.RefreshAsync(fixture.Holder, issued.Result.OperationId, true, credential: NftLifecycleHarness.Credential(fixture.Holder), acceptance: await fixture.Rail.Acceptance(issued.Result.OperationId))).Result!.State.Should().Be("Preparing");
            var delivered = await fixture.Rail.Service.RefreshAsync(fixture.Issuer, issued.Result.OperationId, false);
            delivered.Result!.State.Should().Be("Completed");
            fixture.NftId = delivered.Result.NftId;
            fixture.AssetId = delivered.Result.AssetId!;
            return fixture;
        }

        internal (ActionExternalOperationReference Reference, string Frozen) Intent(Guid actor)
        {
            var config = new CredentialRevokeNodeConfig { AssetId = AssetId, ChainType = "Algorand", Network = "Devnet", Reason = "Issuer withdrew credential." };
            var node = new ActionNode { Id = Guid.NewGuid(), NodeType = ActionNodeType.CredentialRevoke, Config = JsonSerializer.Serialize(config) };
            var context = new ActionNodeExecutionContext(Guid.NewGuid(), node.Id, new ActionDefinition { Id = Guid.NewGuid(), Nodes = [node] }, actor);
            var input = new ActionFrozenNodeInput(1, context.RunId, node.Id, node.NodeType, actor, null,
                JsonSerializer.SerializeToElement(config), new Dictionary<Guid, ActionNodeExecution>(), new Dictionary<Guid, ActionNodeExecution>());
            var frozen = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(input));
            var reference = ActionNftOperationService.Reference(context, CanonicalJson.HashCanonical(frozen));
            Rail.AuthorizeWorkflow(frozen, reference);
            return (reference, frozen);
        }
    }
}
