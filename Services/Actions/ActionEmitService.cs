using System.Text.Json;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Services.Signing;
using Receipt = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionEmitReceipt;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>Recovers one exact Emit acceptance without dispatching HTTP or changing its destination.</summary>
public sealed class ActionEmitService(IActionEmitStore receipts, IActionRunStore runs,
    IActionNodeExecutionStore executions, SigningAdmissionAuthorityFactory authorityFactory) : IActionExternalOperationResolver
{
    /// <inheritdoc/>
    public string Domain => "emit";

    public static ActionExternalOperationReference Reference(ActionNodeExecutionContext context, string intentHash)
        => Reference(context.RunId, context.NodeId, context.ActingUserId, context.ActingTenantId, intentHash);

    private static ActionExternalOperationReference Reference(Guid run, Guid node, Guid actor, Guid? tenant, string hash)
        => new(1, "emit", $"action:{run:N}:{node:N}:emit", ActionEmitIdentity.Create(run, node, actor, tenant), hash, actor, tenant);

    public async Task<ActionNodeHandlerResult> ExecuteAsync(ActionNodeExecutionContext context, CancellationToken ct)
    {
        var admitted = await executions.GetByRunAndNodeAsync(context.RunId, context.NodeId, ct);
        if (admitted.IsError || admitted.Result?.ExternalOperation is not { } reference
            || admitted.Result.FrozenInput is not { } frozen || Reference(context, reference.IntentHash) != reference)
            return ActionNodeHandlerResult.Invalid("Emit requires durable workflow admission.");
        return ActionNodeHandlerResult.Pending(await ObserveAsync(reference, frozen, ct));
    }

    /// <inheritdoc/>
    public async Task<ActionExternalOperationObservation> ObserveAsync(ActionExternalOperationReference reference,
        string canonicalInput, CancellationToken ct = default)
    {
        if (reference.Version != 1 || reference.Domain != Domain || CanonicalJson.HashCanonical(canonicalInput) != reference.IntentHash)
            return Refuse(reference, "Emit recovery envelope does not match its frozen input.");
        ActionFrozenNodeInput? input;
        try
        {
            using var document = JsonDocument.Parse(canonicalInput);
            if (CanonicalJson.Canonicalize(document.RootElement) != canonicalInput)
                return Refuse(reference, "Emit recovery requires canonical input.");
            input = document.RootElement.Deserialize<ActionFrozenNodeInput>();
        }
        catch (JsonException) { return Refuse(reference, "Emit recovery input is invalid."); }
        if (input is null || input.Version != 1 || input.NodeType != ActionNodeType.Emit
            || input.RunId == Guid.Empty || input.NodeId == Guid.Empty || input.ActorUserId == Guid.Empty
            || input.ActingTenantId == Guid.Empty
            || Reference(input.RunId, input.NodeId, input.ActorUserId, input.ActingTenantId, reference.IntentHash) != reference)
            return Refuse(reference, "Emit recovery identity conflicts with its original command.");
        var previous = await receipts.GetAsync(reference.OperationId!, ct);
        if (previous is not null) return await ObserveReceiptAsync(reference, input, previous, ct);
        var loaded = await runs.GetByIdAsync(input.RunId, ct);
        if (loaded.IsError || loaded.Result is not { } run || run.UserId != input.ActorUserId
            || run.ActingTenantId != input.ActingTenantId || run.AuthenticationWitnessJson is null)
            return Refuse(reference, "Original Emit workflow authority is unavailable.");
        var credential = JsonSerializer.Deserialize<SigningCredentialWitness>(run.AuthenticationWitnessJson);
        if (credential is null || credential.ActorUserId != input.ActorUserId || credential.TenantId != input.ActingTenantId)
            return Refuse(reference, "Original Emit credential does not match the frozen participant.");
        var selected = await authorityFactory.SelectWorkflowAsync(credential,
            new(input.RunId, input.NodeId, input.ActorUserId, input.ActingTenantId, reference.IntentHash), [], ct);
        if (selected.IsError || selected.Result is null)
            return new(ActionExternalOperationStatus.Pending, reference, Detail: "Emit awaits current original workflow consent.");
        var accepted = await receipts.AcceptAsync(canonicalInput, selected.Result, ct);
        return accepted is null
            ? new(ActionExternalOperationStatus.Pending, reference,
                Detail: "Emit is not accepted yet; current workflow authority and an active tenant destination are required.")
            : await ObserveReceiptAsync(reference, input, accepted, ct);
    }

    private async Task<ActionExternalOperationObservation> ObserveReceiptAsync(ActionExternalOperationReference reference,
        ActionFrozenNodeInput input, Receipt receipt, CancellationToken ct)
    {
        if (receipt.Id != reference.OperationId || receipt.IntentHash != reference.IntentHash
            || receipt.RunId != input.RunId.ToString("N") || receipt.NodeId != input.NodeId.ToString("N")
            || receipt.ActorId != input.ActorUserId.ToString("N") || receipt.TenantId != input.ActingTenantId?.ToString("N"))
            return Refuse(reference, "Saved Emit receipt conflicts with the admitted command.");
        if (!receipt.IsError && input.ActingTenantId is not null
            && (receipt.EventId != receipt.Id || string.IsNullOrWhiteSpace(receipt.RegistrationId)
                || receipt.DestinationRevision is null or <= 0 || string.IsNullOrWhiteSpace(receipt.DestinationUrl)
                || !await receipts.HasAcceptedEventAsync(receipt, ct)))
            return Refuse(reference, "Accepted tenant Emit receipt lacks its pinned outbox identity.");
        return new(receipt.IsError ? ActionExternalOperationStatus.Failed : ActionExternalOperationStatus.Succeeded,
            reference, receipt.OutputJson, receipt.IsError ? receipt.Message : "Durably accepted; receiver delivery is tracked separately.");
    }

    private static ActionExternalOperationObservation Refuse(ActionExternalOperationReference reference, string detail)
        => new(ActionExternalOperationStatus.Ambiguous, reference, Detail: detail);
}
