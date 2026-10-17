using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Wormhole;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>Admits and advances workflow Bridge/Back transfers on the <c>wtt</c> domain; see Services/Wormhole/AGENTS.md §Workflow authority.</summary>
public sealed class WttActionResolver(WttLifecycleService lifecycle, WttRecoveryService recovery, IWttOperationStore operations,
    IActionNodeExecutionStore executions, WttModuleResolver modules) : IActionExternalOperationResolver, IActionWttRoute
{
    public const string DomainName = "wtt";

    /// <summary>Config property that moves a Bridge/Back node onto the <c>wtt</c> domain.</summary>
    public const string ConfigProperty = "wtt";

    private static readonly JsonSerializerOptions ConfigOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    /// <inheritdoc/>
    public string Domain => DomainName;

    /// <summary>Signing scopes a WTT node selects at admission (plus <c>action:execute</c>, which the engine appends).</summary>
    public static IReadOnlyList<string> SigningScopes { get; } = [AzoaScopes.WttSourceSign, AzoaScopes.WttTargetSign];

    /// <inheritdoc/>
    public bool Applies(AZOA.WebAPI.Models.Actions.ActionNode node) => ReadConfig(node.Config) is not null;

    /// <inheritdoc/>
    IReadOnlyList<string> IActionWttRoute.SigningScopes => SigningScopes;

    /// <inheritdoc/>
    public string? SettlementRailId(AZOA.WebAPI.Models.Actions.ActionNode node) => ReadConfig(node.Config)?.Source.ChainType;

    /// <inheritdoc/>
    public ActionExternalOperationReference CreateReference(ActionNodeExecutionContext context, string intentHash) => Reference(context, intentHash);

    public static ActionExternalOperationReference Reference(ActionNodeExecutionContext context, string intentHash)
    {
        var key = WttLifecycleService.WorkflowRequestKey(context.RunId, context.NodeId);
        return new(1, DomainName, key, WttOperationIdentity.Operation(context.ActingUserId, context.ActingTenantId, WttOperationIdentity.RequestKeyHash(key)),
            intentHash, context.ActingUserId, context.ActingTenantId);
    }

    /// <summary>The node's <c>wtt</c> request, or null when the Bridge/Back node is not on this domain.</summary>
    public static WttActionConfig? ReadConfig(string? nodeConfigJson)
    {
        if (string.IsNullOrWhiteSpace(nodeConfigJson)) return null;
        using var document = JsonDocument.Parse(nodeConfigJson);
        return ReadConfig(document.RootElement);
    }

    /// <inheritdoc cref="ReadConfig(string?)"/>
    public static WttActionConfig? ReadConfig(JsonElement nodeConfig)
    {
        if (nodeConfig.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in nodeConfig.EnumerateObject())
            if (string.Equals(property.Name, ConfigProperty, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Object)
            {
                try { return property.Value.Deserialize<WttActionConfig>(ConfigOptions); }
                catch (JsonException) { return null; }
            }
        return null;
    }

    public async Task<ActionNodeHandlerResult> ExecuteAsync(ActionNodeExecutionContext context, CancellationToken ct)
    {
        var loaded = await executions.GetByRunAndNodeAsync(context.RunId, context.NodeId, ct);
        if (loaded.IsError || loaded.Result?.ExternalOperation is not { } reference || loaded.Result.FrozenInput is not { } frozen
            || Reference(context, reference.IntentHash) != reference)
            return ActionNodeHandlerResult.Invalid("WTT effects require original durable workflow admission.");
        var observed = await ObserveAsync(reference, frozen, ct);
        return observed.Status == ActionExternalOperationStatus.Succeeded
            ? ActionNodeHandlerResult.Ok(observed.Output, txHash: observed.TransactionId, chainType: observed.ChainType)
            : ActionNodeHandlerResult.Pending(observed);
    }

    /// <inheritdoc/>
    public async Task<ActionExternalOperationObservation> ObserveAsync(ActionExternalOperationReference reference, string canonicalInput,
        CancellationToken ct = default)
    {
        ActionFrozenNodeInput? input;
        try { input = JsonSerializer.Deserialize<ActionFrozenNodeInput>(canonicalInput); }
        catch (JsonException) { input = null; }
        if (input is null || input.Version != 1 || input.NodeType is not (ActionNodeType.Bridge or ActionNodeType.Back) || reference.Version != 1
            || reference.Domain != Domain || CanonicalJson.HashCanonical(canonicalInput) != reference.IntentHash
            || input.ActorUserId != reference.ActorUserId || input.ActingTenantId != reference.ActingTenantId
            || reference.RequestKey != WttLifecycleService.WorkflowRequestKey(input.RunId, input.NodeId)
            || reference.OperationId != WttOperationIdentity.Operation(input.ActorUserId, input.ActingTenantId,
                WttOperationIdentity.RequestKeyHash(reference.RequestKey)))
            return Observation(ActionExternalOperationStatus.Ambiguous, reference, "The saved WTT envelope differs from its original frozen identity.");
        var config = ReadConfig(input.Config);
        if (config is null || (input.NodeType == ActionNodeType.Back) != (config.ReturnOfOperationId is not null))
            return Observation(ActionExternalOperationStatus.Failed, reference,
                "A typed wtt request is required; a Back node names the settled operation it returns, a Bridge node names none.");
        var loaded = await executions.GetByRunAndNodeAsync(input.RunId, input.NodeId, ct);
        if (loaded.IsError || loaded.Result is not { } execution || execution.ExternalOperation != reference
            || execution.FrozenInput != canonicalInput || execution.SigningAuthorityJson is null)
            return Observation(ActionExternalOperationStatus.Ambiguous, reference, "The exact admitted WTT signing execution is unavailable.");
        var witness = JsonSerializer.Deserialize<WorkflowSigningWitness>(execution.SigningAuthorityJson)!;
        if (witness.Action.RunId != input.RunId || witness.Action.NodeId != input.NodeId || witness.Action.IntentHash != reference.IntentHash
            || witness.Credential.ActorUserId != input.ActorUserId || witness.Credential.TenantId != input.ActingTenantId)
            return Observation(ActionExternalOperationStatus.Ambiguous, reference, "The frozen workflow signing witness belongs to another node.");
        var operationId = reference.OperationId!;
        var row = await operations.GetAsync(operationId, ct);
        if (row is null)
        {
            var admitted = await AdmitAsync(witness, reference, config, ct);
            if (admitted is not null) return admitted;
            row = await operations.GetAsync(operationId, ct);
            if (row is null) return Observation(ActionExternalOperationStatus.Pending, reference, "Workflow WTT admission is pending.", config.Source.ChainType);
        }
        if (row.ActorUserId != input.ActorUserId.ToString("N") || row.TenantId != (input.ActingTenantId?.ToString("N") ?? "")
            || row.ActionRunId != input.RunId.ToString("N") || row.ActionNodeId != input.NodeId.ToString("N") || row.ActionIntentHash != reference.IntentHash
            || row.ReturnOfOperationId != config.ReturnOfOperationId)
            return Observation(ActionExternalOperationStatus.Ambiguous, reference, "The saved WTT operation belongs to another frozen request.");
        for (var step = 0; step < 6; step++)
        {
            switch (row.State)
            {
                case "Settled" when row.SettlementJson is not null:
                    return Settled(reference, row);
                case "Cancelled":
                    return Observation(ActionExternalOperationStatus.Cancelled, reference, "The transfer was cancelled before any source publication.",
                        config.Source.ChainType);
                case "NeedsOperator":
                    return Observation(ActionExternalOperationStatus.Ambiguous, reference, "The transfer needs operator reconciliation: " + row.Detail,
                        config.Source.ChainType);
            }
            if (row.Phase == "NativeOutput")
                return Observation(ActionExternalOperationStatus.Ambiguous, reference, "A workflow transfer cannot reach a participant step.");
            var current = row.CurrentStageId is { } stageId ? await operations.GetStageAsync(stageId, ct) : null;
            if (row.State == "AwaitingProof" || row.State == "Active" && current?.State is "SubmissionAuthorized" or "NeedsOperator")
            {
                // Finality and proof progress need no new signature: observe, then re-read.
                await recovery.ReconcileAsync(operationId, ct);
                var observed = await operations.GetAsync(operationId, ct) ?? row;
                if (observed.Version == row.Version)
                    return Observation(ActionExternalOperationStatus.Pending, reference, observed.Detail ?? "Awaiting finality or guardian proof.",
                        ChainOf(observed, config));
                row = observed;
                continue;
            }
            if (row.State is not ("Admitted" or "Ready" or "Active"))
                return Observation(ActionExternalOperationStatus.Pending, reference, $"The transfer is {row.State}.", ChainOf(row, config));
            var advanced = await lifecycle.ContinueForWorkflowAsync(witness, operationId, new(row.Version, true), ct);
            if (advanced.IsError)
                return Observation(advanced.Code == AzoaErrorCodes.Forbidden ? ActionExternalOperationStatus.Ambiguous : ActionExternalOperationStatus.Pending,
                    reference, advanced.Message, ChainOf(row, config));
            var next = await operations.GetAsync(operationId, ct) ?? row;
            if (next.Version == row.Version) return Observation(ActionExternalOperationStatus.Pending, reference, next.Detail ?? "Awaiting progress.",
                ChainOf(next, config));
            row = next;
        }
        return Observation(ActionExternalOperationStatus.Pending, reference, $"The transfer is {row.State}.", ChainOf(row, config));
    }

    /// <summary>Quotes and admits under the node witness; returns a terminal observation only when admission can never succeed.</summary>
    private async Task<ActionExternalOperationObservation?> AdmitAsync(WorkflowSigningWitness witness, ActionExternalOperationReference reference,
        WttActionConfig config, CancellationToken ct)
    {
        var chain = config.Source.ChainType;
        if (!WttModuleResolver.TryParseNetwork(config.Source.Network, out var sourceNetwork)
            || !WttModuleResolver.TryParseNetwork(config.Target.Network, out var targetNetwork)
            || modules.Resolve<IWttSourceModule>(config.Source.ChainType, sourceNetwork) is null
            || modules.Resolve<IWttTargetModule>(config.Target.ChainType, targetNetwork) is null)
            return Observation(ActionExternalOperationStatus.Failed, reference,
                "No reviewed WTT module is enabled for this exact route; nothing was quoted or admitted.", chain);
        var request = new WttQuoteRequest(reference.RequestKey, config.Source, config.TokenAddress, config.NativeAsset, config.Amount, "0",
            config.Target, config.RecipientAddress, false, config.SourceBudget, config.TargetBudget, config.ReturnOfOperationId);
        var quoted = await lifecycle.QuoteForWorkflowAsync(witness, request, ct);
        if (quoted.IsError || quoted.Result is not { } offer)
            return Observation(Terminal(quoted) ? ActionExternalOperationStatus.Failed : ActionExternalOperationStatus.Pending, reference, quoted.Message, chain);
        var started = await lifecycle.StartForWorkflowAsync(witness, new(offer.OfferId, offer.OfferHash, true), reference.RequestKey, ct);
        if (started.IsError && Terminal(started) && await operations.GetAsync(reference.OperationId!, ct) is null)
            return Observation(ActionExternalOperationStatus.Failed, reference, started.Message, chain);
        return null;
    }

    private static bool Terminal<T>(AZOAResult<T> result) => result.Code is AzoaErrorCodes.InvalidRequest or AzoaErrorCodes.Forbidden;

    private static string ChainOf(WttOperation row, WttActionConfig config)
        => row.Phase is "Target" or "Complete" ? config.Target.ChainType : config.Source.ChainType;

    private static ActionExternalOperationObservation Settled(ActionExternalOperationReference reference, WttOperation row)
    {
        var intent = WttLifecycleService.AdmittedIntent(row);
        var settlement = WttLifecycleService.Read<WttSettlement>(row.SettlementJson!);
        var output = JsonSerializer.Serialize(new WttActionOutput(SurrealRecordGuid.BareId(row.Id), intent.Source.ChainType, intent.Target.ChainType,
            settlement.RecipientAddress, settlement.RecipientAmount, settlement.TargetTransactionId, settlement.BodyDigest, row.ReturnOfOperationId),
            ActionNodeJson.Options);
        return new(ActionExternalOperationStatus.Succeeded, reference, output, TransactionId: settlement.TargetTransactionId,
            ChainType: intent.Target.ChainType);
    }

    private static ActionExternalOperationObservation Observation(ActionExternalOperationStatus status, ActionExternalOperationReference reference,
        string detail, string? chain = null)
        => new(status, reference, Detail: detail, ChainType: chain);
}
