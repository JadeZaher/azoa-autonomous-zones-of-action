using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Actions.Workflow;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using RunRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionRun;
using ExecutionRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionNodeExecution;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealActionRecoveryTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private readonly Guid _operator = Guid.NewGuid();
    private ISurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;
    private SurrealActionRunStore _runs = null!;
    private SurrealActionNodeExecutionStore _executions = null!;
    private SurrealActionRecoveryStore _store = null!;
    private SurrealSagaStore _sagas = null!;
    private ActionOperationRecoveryService _service = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "action_run", "action_node_execution", "saga_steps", "action_recovery_audit");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions { Endpoint = SurrealTestDefaults.Endpoint,
            Namespace = _namespace, Database = "test", User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password });
        _executor = new DefaultSurrealExecutor(_connection);
        _runs = new(_executor); _executions = new(_executor); _store = new(_executor); _sagas = new(_executor);
        _service = new(_store, _executions, _runs, [new NoEgressResolver()]);
    }

    [Fact]
    public async Task RealSagaPark_ConcurrentRetryCreatesOneAudit_AndOldRequestCannotWakeLaterGeneration()
    {
        var execution = await ParkedAsync();
        var request = Request(execution);
        var results = await Task.WhenAll(_service.RetryAsync(execution.Id, _operator, request, default),
            _service.RetryAsync(execution.Id, _operator, request, default));
        results.Should().OnlyContain(result => !result.IsError);
        results.Select(result => result.Result!.AuditId).Distinct().Should().ContainSingle();
        (await _store.HistoryAsync(execution.Id.ToString("N"), default)).Should().ContainSingle();
        var stepId = Guid.ParseExact(ActionWorkflowIdentity.StepId(execution.RunId, execution.NodeId), "N");
        (await _sagas.GetAsync(stepId, default))!.Status.Should().Be(StepStatus.Pending);
        var claimed = await _sagas.TryClaimDueStepAsync(stepId, DateTime.UtcNow.AddSeconds(1), default);
        claimed.Should().NotBeNull();
        await _sagas.ParkStepAsync(stepId, claimed!.ClaimedAt!.Value, $"operator:{execution.NodeId:N}", null, default);
        var current = (await _executions.GetByIdAsync(execution.Id)).Result!;
        current.RecoveryState = "NeedsOperator";
        (await _executions.UpdateAsync(current, ActionNodeState.Running)).IsError.Should().BeFalse();
        (await _service.RetryAsync(execution.Id, _operator, request, default)).IsError.Should().BeFalse();
        (await _sagas.GetAsync(stepId, default))!.Status.Should().Be(StepStatus.Parked);
        (await _service.RetryAsync(execution.Id, _operator, request with { RequestId = Guid.NewGuid() }, default)).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task SignalStampedPayload_IsPreserved_AndInspectNeverInvokesDomainRecovery()
    {
        var execution = await ParkedAsync("{\"approval\":true}");
        var before = await _store.GetStepAsync(execution.RunId, execution.NodeId, default);
        var view = await _service.GetAsync(execution.Id, default);
        view.Result!.CanRetry.Should().BeTrue();
        var json = JsonSerializer.Serialize(view.Result);
        json.Should().NotContain("approval").And.NotContain("FrozenInput");
        var result = await _service.RetryAsync(execution.Id, _operator, Request(execution), default);
        result.IsError.Should().BeFalse(result.Message);
        (await _store.GetStepAsync(execution.RunId, execution.NodeId, default))!.Payload.Should().Be(before!.Payload);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("version")]
    [InlineData("payload")]
    [InlineData("gate")]
    [InlineData("intent")]
    public async Task ChangedAuthorityOrCorrelation_RollsBackAuditAndWake(string change)
    {
        var execution = await ParkedAsync();
        var request = Request(execution);
        var stepId = ActionWorkflowIdentity.StepId(execution.RunId, execution.NodeId);
        var query = change switch
        {
            "cancel" => SurrealWriter.UpdateOnly<RunRow>(execution.RunId.ToString("N")).Where(seed => true).Set(row => row.Status, RunRow.ActionRunStatusKind.Cancelled).Build(),
            "version" => SurrealWriter.UpdateOnly<ExecutionRow>(execution.Id.ToString("N")).Where(seed => true).Set(row => row.Version, 1L).Build(),
            "payload" => SurrealWriter.UpdateOnly<AZOA.WebAPI.Persistence.SurrealDb.Models.SagaSteps>(stepId).Where(seed => true)
                .Set(row => row.Payload, SagaStep<ActionStepPayload>.Serialize(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()))).Build(),
            "gate" => SurrealWriter.UpdateOnly<AZOA.WebAPI.Persistence.SurrealDb.Models.SagaSteps>(stepId).Where(seed => true).Set(row => row.GateId, SagaOpaqueKey.Encode("another-gate")).Build(),
            _ => SurrealWriter.UpdateOnly<ExecutionRow>(execution.Id.ToString("N")).Where(seed => true).Set(row => row.FrozenInput, "{}").Build()
        };
        (await _executor.ExecuteAsync(query)).EnsureAllOk();
        (await _service.RetryAsync(execution.Id, _operator, request, default)).IsError.Should().BeTrue();
        (await _store.HistoryAsync(execution.Id.ToString("N"), default)).Should().BeEmpty();
        (await _sagas.GetAsync(Guid.ParseExact(stepId, "N"), default))!.Status.Should().Be(StepStatus.Parked);
    }

    [Fact]
    public async Task LegacyUncorrelatedExecution_IsInspectableButCannotBeReset()
    {
        var execution = await ParkedAsync();
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<ExecutionRow>(execution.Id.ToString("N")).Where(seed => true)
            .Unset(row => row.FrozenInput).Unset(row => row.ExternalOperationJson).Build())).EnsureAllOk();
        (await _service.GetAsync(execution.Id, default)).Result!.CanRetry.Should().BeFalse();
        (await _service.RetryAsync(execution.Id, _operator, Request(execution), default)).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task RegisteredReadRecovery_CanWakeOriginalStepWithoutExecutingTheRead()
    {
        var execution = await ParkedAsync(domain: "read");
        var service = new ActionOperationRecoveryService(_store, _executions, _runs, [new NoEgressResolver("read")]);
        (await service.GetAsync(execution.Id, default)).Result!.CanRetry.Should().BeTrue();
        (await service.RetryAsync(execution.Id, _operator, Request(execution), default)).IsError.Should().BeFalse();
        (await _sagas.GetAsync(Guid.ParseExact(ActionWorkflowIdentity.StepId(execution.RunId, execution.NodeId), "N"), default))!
            .Status.Should().Be(StepStatus.Pending);
    }

    private async Task<ActionNodeExecution> ParkedAsync(string? signal = null, string domain = "local")
    {
        var run = new ActionRun { Id = Guid.NewGuid(), ActionId = Guid.NewGuid(), UserId = Guid.NewGuid(), Status = ActionRunStatus.AwaitingReconciliation };
        var node = new ActionNode { Id = Guid.NewGuid(), NodeType = domain == "read" ? ActionNodeType.HolonGet : ActionNodeType.HolonCreate, Config = "{}" };
        ActionRunSnapshot.Capture(run, new ActionDefinition { Id = run.ActionId, UserId = run.UserId, Nodes = [node] });
        (await _runs.CreateAsync(run)).IsError.Should().BeFalse();
        var input = new ActionFrozenNodeInput(1, run.Id, node.Id, node.NodeType, run.UserId, null,
            JsonSerializer.SerializeToElement(new { name = "private fixture input" }), new Dictionary<Guid, ActionNodeExecution>(), new Dictionary<Guid, ActionNodeExecution>());
        var frozen = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(input));
        var execution = new ActionNodeExecution { Id = Guid.NewGuid(), RunId = run.Id, NodeId = node.Id, State = ActionNodeState.Running,
            RecoveryState = "NeedsOperator", FrozenInput = frozen,
            ExternalOperation = new(1, domain, $"action:{run.Id:N}:{node.Id:N}:{domain}", Guid.NewGuid().ToString("N"), CanonicalJson.HashCanonical(frozen), run.UserId, null) };
        (await _executions.CreateAsync(execution)).IsError.Should().BeFalse();
        var payload = new ActionStepPayload(run.Id, run.ActionId, run.UserId, node.Id);
        await new SurrealActionWorkflowAdmissionStore(_executor).EnqueueNodeAsync(run.Id, node.Id, SagaStep<ActionStepPayload>.Serialize(payload));
        var stepId = Guid.ParseExact(ActionWorkflowIdentity.StepId(run.Id, node.Id), "N");
        var claimed = await _sagas.TryClaimDueStepAsync(stepId, DateTime.UtcNow.AddSeconds(1), default);
        claimed.Should().NotBeNull();
        if (signal is not null)
        {
            (await _sagas.ParkStepAsync(stepId, claimed!.ClaimedAt!.Value, "approval", null, default)).Should().BeTrue();
            (await _sagas.TrySignalAsync(run.Id.ToString(), "approval", SagaStep<ActionStepPayload>.Serialize(payload with { SignalPayload = signal }), default)).Should().NotBeNull();
            claimed = await _sagas.TryClaimDueStepAsync(stepId, DateTime.UtcNow.AddSeconds(1), default);
        }
        (await _sagas.ParkStepAsync(stepId, claimed!.ClaimedAt!.Value, $"operator:{node.Id:N}", null, default)).Should().BeTrue();
        return execution;
    }

    private static ActionRecoveryRequest Request(ActionNodeExecution execution)
        => new(Guid.NewGuid(), execution.Version, execution.ExternalOperation!.IntentHash, "Reviewed original operation and restored its dependency.");

    private sealed class NoEgressResolver(string domain = "local") : IActionExternalOperationResolver
    {
        public string Domain => domain;
        public Task<ActionExternalOperationObservation> ObserveAsync(ActionExternalOperationReference reference, string canonicalInput, CancellationToken ct = default)
            => throw new InvalidOperationException("Operator inspection and scheduling must not invoke an effect resolver.");
    }

    public async Task DisposeAsync()
    {
        _connection?.Dispose();
        await SurrealTestSchema.DropAsync(_namespace);
    }
}
