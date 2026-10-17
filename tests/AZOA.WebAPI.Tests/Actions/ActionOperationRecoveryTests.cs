using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Actions;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Actions;

public sealed class ActionOperationRecoveryTests
{
    [Theory]
    [InlineData("run")]
    [InlineData("node")]
    [InlineData("actor")]
    [InlineData("tenant")]
    [InlineData("version")]
    public async Task SelfConsistentHashCannotChangeTheAdmittedIdentity(string changed)
    {
        var (run, execution, input) = Create();
        input = changed switch
        {
            "run" => input with { RunId = Guid.NewGuid() },
            "node" => input with { NodeId = Guid.NewGuid() },
            "actor" => input with { ActorUserId = Guid.NewGuid() },
            "tenant" => input with { ActingTenantId = Guid.NewGuid() },
            _ => input with { Version = 2 }
        };
        Save(execution, input);
        var resolver = new Observer();
        var result = await new ActionOperationRecovery([resolver]).ObserveAsync(execution, run, default);
        result.Status.Should().Be(ActionExternalOperationStatus.Ambiguous);
        resolver.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"Version\":1,\"Version\":1}")]
    public async Task CorruptPersistedInputNeedsOperatorWithoutInvokingTheDomain(string json)
    {
        var (run, execution, _) = Create();
        execution.FrozenInput = json;
        execution.ExternalOperation = execution.ExternalOperation! with { IntentHash = CanonicalJson.HashCanonical(json) };
        var resolver = new Observer();
        var result = await new ActionOperationRecovery([resolver]).ObserveAsync(execution, run, default);
        result.Status.Should().Be(ActionExternalOperationStatus.Ambiguous);
        resolver.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ObserverMayAttachIdentityOnceButCannotReplaceAnExistingIdentity()
    {
        var (run, execution, _) = Create();
        var resolver = new Observer();
        var recovery = new ActionOperationRecovery([resolver]);
        var attached = await recovery.ObserveAsync(execution, run, default);
        attached.Status.Should().Be(ActionExternalOperationStatus.Pending);
        attached.Reference.OperationId.Should().Be("domain-operation");
        execution.ExternalOperation = attached.Reference;
        resolver.OperationId = "different-operation";
        (await recovery.ObserveAsync(execution, run, default)).Status.Should().Be(ActionExternalOperationStatus.Ambiguous);
    }

    [Theory]
    [InlineData(2, "test")]
    [InlineData(1, "unknown")]
    public async Task UnsupportedEnvelopeDoesNotDispatch(int version, string domain)
    {
        var (run, execution, _) = Create();
        execution.ExternalOperation = execution.ExternalOperation! with { Version = version, Domain = domain };
        var resolver = new Observer();
        (await new ActionOperationRecovery([resolver]).ObserveAsync(execution, run, default))
            .Status.Should().Be(ActionExternalOperationStatus.Ambiguous);
        resolver.Calls.Should().Be(0);
    }

    private static (ActionRun Run, ActionNodeExecution Execution, ActionFrozenNodeInput Input) Create()
    {
        var run = new ActionRun { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), ActingTenantId = Guid.NewGuid() };
        var execution = new ActionNodeExecution { Id = Guid.NewGuid(), RunId = run.Id, NodeId = Guid.NewGuid() };
        ActionRunSnapshot.Capture(run, new ActionDefinition
        {
            Id = Guid.NewGuid(), Nodes = [new ActionNode { Id = execution.NodeId, NodeType = default }]
        });
        var input = new ActionFrozenNodeInput(1, run.Id, execution.NodeId, default, run.UserId,
            run.ActingTenantId, JsonSerializer.SerializeToElement(new { amount = 10 }),
            new Dictionary<Guid, ActionNodeExecution>(), new Dictionary<Guid, ActionNodeExecution>());
        execution.ExternalOperation = new(1, "test", "immutable-request", null, "", run.UserId, run.ActingTenantId);
        Save(execution, input);
        return (run, execution, input);
    }

    private static void Save(ActionNodeExecution execution, ActionFrozenNodeInput input)
    {
        execution.FrozenInput = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(input));
        execution.ExternalOperation = execution.ExternalOperation! with { IntentHash = CanonicalJson.HashCanonical(execution.FrozenInput) };
    }

    private sealed class Observer : IActionExternalOperationResolver
    {
        public string Domain => "test";
        public int Calls { get; private set; }
        public string OperationId { get; set; } = "domain-operation";
        public Task<ActionExternalOperationObservation> ObserveAsync(ActionExternalOperationReference reference,
            string canonicalInput, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new ActionExternalOperationObservation(ActionExternalOperationStatus.Pending,
                reference with { OperationId = OperationId }));
        }
    }
}
