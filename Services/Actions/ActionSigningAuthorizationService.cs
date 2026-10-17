using System.Security.Claims;
using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Actions.Workflow;
using AZOA.WebAPI.Services.Signing;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>Captures fresh participant consent for an exact frozen node that lacks signing evidence.</summary>
public sealed class ActionSigningAuthorizationService(SigningAdmissionAuthorityFactory authority,
    SurrealActionSigningAuthorizationStore store, IActionNodeExecutionStore executions, IActionRunStore runs)
{
    public async Task<AZOAResult<ActionSigningAuthorizationReceipt>> AuthorizeAsync(Guid executionId,
        ActionSigningAuthorizationRequest request, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (request.RequestId == Guid.Empty || request.ExpectedVersion < 0 || request.IntentHash?.Length != 64
            || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length is < 20 or > 1000)
            return AZOAResult<ActionSigningAuthorizationReceipt>.Failure("Exact version, intent hash, request ID and a 20–1000 character authorization reason are required.");
        var captured = await authority.CaptureCredentialAsync(principal, ct);
        if (captured.IsError || captured.Result is not { } credential)
            return AZOAResult<ActionSigningAuthorizationReceipt>.Failure(captured.Message);
        var id = CanonicalJson.Hash(new { Domain = "action-participant-authorization-v1", credential.ActorUserId, credential.TenantId, request.RequestId })[..32];
        var requestHash = CanonicalJson.Hash(new { executionId, request.ExpectedVersion, request.IntentHash, Reason = request.Reason.Trim() });
        var prior = await store.GetAsync(id, ct);
        if (prior is not null)
            return prior.ActorId == credential.ActorUserId.ToString("N") && prior.TenantId == credential.TenantId?.ToString("N") && prior.RequestHash == requestHash
                ? Receipt(prior) : AZOAResult<ActionSigningAuthorizationReceipt>.Failure("Participant request key conflicts with its original authorization.");
        var loaded = await executions.GetByIdAsync(executionId, ct);
        if (loaded.IsError || loaded.Result is not { } execution || execution.Version != request.ExpectedVersion
            || execution.State is not (ActionNodeState.Pending or ActionNodeState.Running)
            || execution.RecoveryState is not ("NeedsParticipantAuthorization" or "NeedsOperator")
            || execution.SigningAuthorityJson is not null || execution.AdmissionRejection is not null || execution.FrozenInput is null)
            return AZOAResult<ActionSigningAuthorizationReceipt>.Failure("The unchanged node is not awaiting participant signing authorization.");
        var runResult = await runs.GetByIdAsync(execution.RunId, ct);
        if (runResult.IsError || runResult.Result is not { } run || run.UserId != credential.ActorUserId || run.ActingTenantId != credential.TenantId
            || run.Status is not (ActionRunStatus.Running or ActionRunStatus.AwaitingReconciliation))
            return AZOAResult<ActionSigningAuthorizationReceipt>.Failure("Only the original actor in the exact tenant context may authorize this operation.");
        ActionFrozenNodeInput? input;
        try { input = JsonSerializer.Deserialize<ActionFrozenNodeInput>(execution.FrozenInput); }
        catch (JsonException) { return AZOAResult<ActionSigningAuthorizationReceipt>.Failure("The frozen operation is unreadable."); }
        if (input is null || input.Version != 1 || input.RunId != run.Id || input.NodeId != execution.NodeId
            || input.ActorUserId != run.UserId || input.ActingTenantId != run.ActingTenantId
            || CanonicalJson.HashCanonical(execution.FrozenInput) != request.IntentHash
            || run.DefinitionSnapshotJson is null || ActionRunSnapshot.Read(run).Nodes.SingleOrDefault(node => node.Id == input.NodeId)?.NodeType != input.NodeType)
            return AZOAResult<ActionSigningAuthorizationReceipt>.Failure("Frozen operation and original workflow authority differ.");
        var scopes = ActionSigningScopes.Required(input.NodeType, input.Config.GetRawText());
        if (scopes is null || scopes.Count == 0)
            return AZOAResult<ActionSigningAuthorizationReceipt>.Failure("This node has no supported signing authorization contract.");
        var selected = await authority.SelectWorkflowAsync(credential, new(run.Id, execution.NodeId, run.UserId, run.ActingTenantId, request.IntentHash), scopes, ct);
        if (selected.IsError || selected.Result is null) return AZOAResult<ActionSigningAuthorizationReceipt>.Failure(selected.Message);
        var witness = selected.Result with { AuthorizationId = Guid.ParseExact(id, "N") };
        var step = await store.GetStepAsync(run.Id, execution.NodeId, ct);
        var payload = new ActionStepPayload(run.Id, run.ActionId, run.UserId, execution.NodeId);
        if (step is null || step.Id != ActionWorkflowIdentity.StepId(run.Id, execution.NodeId)
            || step.AdmissionPayloadHash != CanonicalJson.HashCanonical(AZOA.WebAPI.Sagas.SagaStep<ActionStepPayload>.Serialize(payload)))
            return AZOAResult<ActionSigningAuthorizationReceipt>.Failure("The original workflow step is unavailable.");
        try
        {
            var savedPayload = JsonSerializer.Deserialize<ActionStepPayload>(step.Payload);
            if (savedPayload is null || (savedPayload with { SignalPayload = null }) != payload)
                return AZOAResult<ActionSigningAuthorizationReceipt>.Failure("The parked workflow step changed identity.");
        }
        catch (JsonException) { return AZOAResult<ActionSigningAuthorizationReceipt>.Failure("The parked workflow step is unreadable."); }
        var audit = new ActionSigningAuthorization
        {
            Id = id, ActorId = run.UserId.ToString("N"), TenantId = run.ActingTenantId?.ToString("N"), ExecutionId = executionId.ToString("N"),
            RunId = run.Id.ToString("N"), NodeId = execution.NodeId.ToString("N"), ExpectedVersion = request.ExpectedVersion,
            IntentHash = request.IntentHash, WitnessJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(witness)),
            RequestHash = requestHash, Reason = request.Reason.Trim(), CreatedAt = DateTimeOffset.UtcNow,
        };
        var saved = await store.AuthorizeAsync(audit, witness, execution, step, ct);
        return saved.IsError || saved.Result is null ? AZOAResult<ActionSigningAuthorizationReceipt>.Failure(saved.Message) : Receipt(saved.Result);
    }

    private static AZOAResult<ActionSigningAuthorizationReceipt> Receipt(ActionSigningAuthorization row)
        => AZOAResult<ActionSigningAuthorizationReceipt>.Success(new(Guid.ParseExact(row.Id, "N"), Guid.ParseExact(row.RunId, "N"),
            Guid.ParseExact(row.NodeId, "N"), row.IntentHash));
}
