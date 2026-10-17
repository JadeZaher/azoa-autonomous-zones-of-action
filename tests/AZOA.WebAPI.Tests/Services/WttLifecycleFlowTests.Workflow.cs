using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Wormhole;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

/// <summary>Bridge/Back nodes on the <c>wtt</c> domain: the frozen node witness is the only signing authority.</summary>
public sealed partial class WttLifecycleFlowTests
{
    [Fact]
    public async Task AWorkflowBridgeNodeSettlesThroughTheJournalUnderItsFrozenNodeWitnessAndNoOtherCredential()
    {
        var rig = new Rig();
        var node = WorkflowNode.Bridge(rig);
        var resolver = node.Resolver(rig);

        var pending = await resolver.ObserveAsync(node.Reference, node.FrozenInput);
        Assert.Equal(ActionExternalOperationStatus.Pending, pending.Status);
        var operationId = node.Reference.OperationId!;
        var admitted = (await rig.Store.GetAsync(operationId))!;
        Assert.Equal((node.RunId.ToString("N"), node.NodeId.ToString("N")), (admitted.ActionRunId, admitted.ActionNodeId));
        Assert.Single(rig.Source.Sent.Distinct());

        // The owner's own direct credential cannot sign a workflow-owned operation, nor claim its request key.
        Assert.Equal(AzoaErrorCodes.Forbidden, (await rig.Service.ContinueAsync(operationId, new(admitted.Version, true), rig.Credential)).Code);
        Assert.Equal(AzoaErrorCodes.InvalidRequest, (await rig.Service.QuoteAsync(rig.Credential, rig.Request(node.Reference.RequestKey))).Code);

        rig.Source.Finalize("Algorand-Source-Transfer-0");
        Assert.Equal(ActionExternalOperationStatus.Pending, (await resolver.ObserveAsync(node.Reference, node.FrozenInput)).Status);
        Assert.Single(rig.Target.Sent.Distinct());

        rig.Target.Finalize("Solana-Target-Redeem-0");
        var settled = await resolver.ObserveAsync(node.Reference, node.FrozenInput);
        Assert.Equal(ActionExternalOperationStatus.Succeeded, settled.Status);
        Assert.Equal(("Solana-Target-Redeem-0", "Solana"), (settled.TransactionId, settled.ChainType));
        var output = JsonSerializer.Deserialize<WttActionOutput>(settled.Output!, ActionNodeJson.Options)!;
        Assert.Equal((operationId, rig.Recipient), (output.OperationId, output.RecipientAddress));
    }

    [Fact]
    public async Task AWorkflowNodeWhoseWitnessLacksATargetScopeOrWhoseRouteNeedsAParticipantIsRefusedBeforeAnyQuote()
    {
        var narrow = new Rig();
        var node = WorkflowNode.Bridge(narrow, scopes: [AzoaScopes.ActionExecute, AzoaScopes.WttSourceSign]);
        var refused = await node.Resolver(narrow).ObserveAsync(node.Reference, node.FrozenInput);
        Assert.Equal(ActionExternalOperationStatus.Failed, refused.Status);
        Assert.Equal(0, narrow.Source.Quotes);

        var participant = new Rig(participantRecipient: true);
        var optIn = WorkflowNode.Bridge(participant);
        Assert.Equal(ActionExternalOperationStatus.Failed, (await optIn.Resolver(participant).ObserveAsync(optIn.Reference, optIn.FrozenInput)).Status);
        Assert.Null(await participant.Store.GetAsync(optIn.Reference.OperationId!));
    }

    /// <summary>One frozen Bridge node: canonical input, its saved execution and the selected workflow witness.</summary>
    private sealed record WorkflowNode(Guid RunId, Guid NodeId, string FrozenInput, ActionExternalOperationReference Reference,
        ActionNodeExecution Execution)
    {
        internal static WorkflowNode Bridge(Rig rig, IReadOnlyList<string>? scopes = null)
        {
            var (run, node) = (Guid.NewGuid(), Guid.NewGuid());
            var config = new
            {
                wtt = new WttActionConfig(new(rig.SourceChain, "Devnet", rig.SourceWallet), "31566704", false, "1000000",
                    new("Solana", "Devnet", rig.TargetWallet), rig.Recipient, new("1000", "100000", "1000"), new("1000", "5000000", "1000")),
            };
            var input = new ActionFrozenNodeInput(1, run, node, ActionNodeType.Bridge, rig.Actor, null, JsonSerializer.SerializeToElement(config),
                new Dictionary<Guid, ActionNodeExecution>(), new Dictionary<Guid, ActionNodeExecution>());
            var frozen = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(input));
            var intentHash = CanonicalJson.HashCanonical(frozen);
            var key = WttLifecycleService.WorkflowRequestKey(run, node);
            var reference = new ActionExternalOperationReference(1, WttActionResolver.DomainName, key,
                WttOperationIdentity.Operation(rig.Actor, null, WttOperationIdentity.RequestKeyHash(key)), intentHash, rig.Actor, null);
            var witness = new WorkflowSigningWitness(1, rig.Credential, new(run, node, rig.Actor, null, intentHash),
                scopes ?? [AzoaScopes.ActionExecute, AzoaScopes.WttSourceSign, AzoaScopes.WttTargetSign], []);
            var execution = new ActionNodeExecution
            {
                RunId = run, NodeId = node, State = ActionNodeState.Running, FrozenInput = frozen, ExternalOperation = reference,
                SigningAuthorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(witness)),
            };
            return new(run, node, frozen, reference, execution);
        }

        internal WttActionResolver Resolver(Rig rig)
        {
            var executions = new Mock<IActionNodeExecutionStore>();
            executions.Setup(store => store.GetByRunAndNodeAsync(RunId, NodeId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<ActionNodeExecution>.Success(Execution));
            return new(rig.Service, rig.Recovery, rig.Store, executions.Object, rig.Modules);
        }
    }
}
