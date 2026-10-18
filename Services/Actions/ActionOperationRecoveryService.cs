using System.Text.Json;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services.Actions.Workflow;
using Execution = AZOA.WebAPI.Models.Actions.ActionNodeExecution;
using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>Inspects parked actions and requests audited recovery of their unchanged admitted intent.</summary>
public sealed class ActionOperationRecoveryService(SurrealActionRecoveryStore store, IActionNodeExecutionStore executions,
    IActionRunStore runs, IEnumerable<IActionExternalOperationResolver> resolvers)
{
    private static readonly HashSet<string> RecoveryCapabilities = ["local", "governance", "deployment", "nft", "read"];
    private readonly HashSet<string> _registered = resolvers.Select(resolver => resolver.Domain).ToHashSet(StringComparer.Ordinal);

    public async Task<IReadOnlyList<ActionRecoveryView>> ListAsync(int limit, CancellationToken ct)
    {
        var rows = await store.ListAsync(limit, ct);
        return rows.Select(row => View(SurrealActionNodeExecutionStore.ToDomain(row))).ToArray();
    }

    public async Task<AZOAResult<ActionRecoveryView>> GetAsync(Guid id, CancellationToken ct)
    {
        var found = await executions.GetByIdAsync(id, ct);
        return found.IsError || found.Result is null ? AZOAResult<ActionRecoveryView>.Failure("Action execution was not found.")
            : AZOAResult<ActionRecoveryView>.Success(View(found.Result));
    }

    public Task<IReadOnlyList<ActionRecoveryAudit>> HistoryAsync(Guid id, CancellationToken ct)
        => store.HistoryAsync(id.ToString("N"), ct);

    public async Task<AZOAResult<ActionRecoveryReceipt>> RetryAsync(Guid id, Guid operatorId, ActionRecoveryRequest request, CancellationToken ct,
        OperatorActor? actor = null)
    {
        if (id == Guid.Empty || operatorId == Guid.Empty || request.RequestId == Guid.Empty || request.ExpectedVersion < 0
            || request.IntentHash is not { Length: 64 } || request.IntentHash.Any(character => !char.IsAsciiHexDigit(character))
            || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length is < 20 or > 1000)
            return AZOAResult<ActionRecoveryReceipt>.Failure("An exact execution version, intent hash, request ID and review reason of 20–1000 characters are required.");
        var audit = new ActionRecoveryAudit
        {
            Id = CanonicalJson.Hash(new { Domain = "action-recovery-v1", OperatorId = operatorId, request.RequestId })[..32],
            OperatorId = operatorId.ToString("N"), Actor = actor?.AuditActor, ExecutionId = id.ToString("N"), ExpectedVersion = request.ExpectedVersion,
            IntentHash = request.IntentHash, Reason = request.Reason.Trim(),
            RequestHash = CanonicalJson.Hash(new { ExecutionId = id, request.ExpectedVersion, request.IntentHash, Reason = request.Reason.Trim() }),
            CreatedAt = DateTimeOffset.UtcNow
        };
        var prior = await store.GetAuditAsync(audit.Id, ct);
        if (prior is not null)
            return prior.OperatorId == audit.OperatorId && prior.RequestHash == audit.RequestHash && prior.ExecutionId == audit.ExecutionId
                ? Receipt(prior) : AZOAResult<ActionRecoveryReceipt>.Failure("Recovery request key was already used for another request.");
        var loaded = await executions.GetByIdAsync(id, ct);
        if (loaded.IsError || loaded.Result is not { } execution || !View(execution).CanRetry
            || execution.Version != request.ExpectedVersion || execution.ExternalOperation!.IntentHash != request.IntentHash)
            return AZOAResult<ActionRecoveryReceipt>.Failure("The execution is not an unchanged recoverable parked operation.");
        var foundRun = await runs.GetByIdAsync(execution.RunId, ct);
        if (foundRun.IsError || foundRun.Result is not { } run || run.Status != ActionRunStatus.AwaitingReconciliation)
            return AZOAResult<ActionRecoveryReceipt>.Failure("The run is no longer awaiting reconciliation.");
        var reference = execution.ExternalOperation!;
        ActionFrozenNodeInput? input;
        try
        {
            using var document = JsonDocument.Parse(execution.FrozenInput!);
            if (CanonicalJson.Canonicalize(document.RootElement) != execution.FrozenInput)
                return AZOAResult<ActionRecoveryReceipt>.Failure("The saved operation input is not canonical.");
            input = document.RootElement.Deserialize<ActionFrozenNodeInput>();
        }
        catch (JsonException) { return AZOAResult<ActionRecoveryReceipt>.Failure("The saved operation input is unreadable."); }
        if (input is null || input.Version != 1 || input.RunId != run.Id || input.NodeId != execution.NodeId
            || input.ActorUserId != run.UserId || input.ActingTenantId != run.ActingTenantId
            || reference.ActorUserId != run.UserId || reference.ActingTenantId != run.ActingTenantId
            || CanonicalJson.HashCanonical(execution.FrozenInput!) != reference.IntentHash
            || run.DefinitionSnapshotJson is null
            || ActionRunSnapshot.Read(run).Nodes.SingleOrDefault(node => node.Id == input.NodeId)?.NodeType != input.NodeType)
            return AZOAResult<ActionRecoveryReceipt>.Failure("The saved operation no longer matches its frozen run authority.");
        audit.RunId = run.Id.ToString("N");
        audit.NodeId = execution.NodeId.ToString("N");
        var admittedPayload = new ActionStepPayload(run.Id, run.ActionId, run.UserId, execution.NodeId);
        var admissionHash = CanonicalJson.HashCanonical(SagaStep<ActionStepPayload>.Serialize(admittedPayload));
        var step = await store.GetStepAsync(run.Id, execution.NodeId, ct);
        if (step is null || step.AdmissionPayloadHash != admissionHash)
            return AZOAResult<ActionRecoveryReceipt>.Failure("The original workflow step admission is unavailable.");
        try
        {
            var payload = JsonSerializer.Deserialize<ActionStepPayload>(step.Payload);
            if (payload is null || (payload with { SignalPayload = null }) != admittedPayload)
                return AZOAResult<ActionRecoveryReceipt>.Failure("The workflow step identifies another action operation.");
        }
        catch (JsonException) { return AZOAResult<ActionRecoveryReceipt>.Failure("The workflow step payload is unreadable."); }
        var saved = await store.RetryAsync(audit, run.UserId.ToString("N"), run.ActingTenantId?.ToString("N"),
            execution.FrozenInput!, JsonSerializer.Serialize(reference), step.Payload, admissionHash, ct);
        return saved.IsError || saved.Result is null ? AZOAResult<ActionRecoveryReceipt>.Failure(saved.Message) : Receipt(saved.Result);
    }

    private ActionRecoveryView View(Execution execution)
    {
        var reference = execution.ExternalOperation;
        var supported = reference is { Version: 1 } && RecoveryCapabilities.Contains(reference.Domain) && _registered.Contains(reference.Domain);
        return new(execution.Id, execution.RunId, execution.NodeId, execution.Version, execution.State.ToString(), execution.RecoveryState,
            reference?.Domain, reference?.OperationId, reference?.IntentHash,
            supported && execution.State == ActionNodeState.Running && execution.RecoveryState == "NeedsOperator" && execution.FrozenInput is not null);
    }

    private static AZOAResult<ActionRecoveryReceipt> Receipt(ActionRecoveryAudit audit)
        => AZOAResult<ActionRecoveryReceipt>.Success(new(audit.Id, Guid.ParseExact(audit.ExecutionId, "N"), audit.ExpectedVersion, audit.CreatedAt));
}
