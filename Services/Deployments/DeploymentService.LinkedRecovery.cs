using System.Text.Json;
using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Services.Deployments;

public sealed partial class DeploymentService
{
    /// <inheritdoc/>
    public async Task<AZOAResult<DeploymentReceipt>> InvalidateRecoveryAsync(string operationId, Guid actor, Guid? tenant,
        DeploymentRecoveryStartRequest request, CancellationToken ct = default)
    {
        if (request?.Quote?.Intent is not { } intent || intent.Budget is null || !RecoveryKey(request.RequestKey)
            || intent.OperationKey != DeploymentIdentity.Operation(actor, tenant, request.RequestKey)
            || intent.Kind != "Recovery" || intent.Quote is null || intent.Input.ValueKind != JsonValueKind.Object)
            return Fail<DeploymentReceipt>("An exact recovery request is required.");
        var parent = await OwnedAsync(operationId, actor, tenant, ct);
        if (parent is null || parent.ParentOperationId is not null)
            return Fail<DeploymentReceipt>("Original operation not found.");
        NativeRecoveryInput? recovery;
        try { recovery = Read<NativeRecoveryInput>(intent.Input.GetRawText()); }
        catch (JsonException) { return Fail<DeploymentReceipt>("Malformed recovery input."); }
        var original = Read<DeploymentStartRequest>(parent.RequestJson).NativeIntent;
        if (original is null || recovery?.OriginalIntent is null || recovery.ParentVersion != request.Quote.ParentVersion
            || recovery.OriginalIntent.OperationKey != parent.Id
            || CanonicalJson.Hash(recovery.OriginalIntent) != CanonicalJson.Hash(original)
            || !NativeOperationAuthorization.ValidQuote(intent, requireUnexpired: false))
            return Fail<DeploymentReceipt>("Recovery quote belongs to a different original operation.");
        var quote = await store.GetQuoteAsync(intent.Quote.QuoteId, ct);
        if (quote is null || quote.ActorUserId != parent.ActorUserId || quote.TenantId != parent.TenantId
            || quote.IntentHash != intent.Quote.IntentHash || quote.QuoteJson != JsonSerializer.Serialize(intent.Quote))
            return Fail<DeploymentReceipt>("The exact server-issued recovery quote is required.");
        // Read after the parent: admission creates the child and advances the parent atomically.
        if (await store.GetAsync(intent.OperationKey, ct) is not null)
            return Fail<DeploymentReceipt>("This recovery key has already been admitted; reconcile its saved operation.");
        if (parent.Version > request.Quote.ParentVersion)
            return AZOAResult<DeploymentReceipt>.Success(View(parent));
        if (parent.Version != request.Quote.ParentVersion || !RecoveryEligible(parent, allowChild: true)
            || request.Quote.BindingHash != RecoveryBinding(parent, intent))
            return Fail<DeploymentReceipt>("Recovery admission cannot yet be excluded for this request.");
        DeploymentOperation? saved;
        if (parent.RecoveryChildId is { } predecessorId)
        {
            var predecessor = await store.GetAsync(predecessorId, ct);
            if (predecessor is null || recovery.Predecessor is null || NativeRecoveryReplacement.ProofKind(predecessor) is null
                || recovery.Predecessor != await AuditedPredecessorAsync(parent, predecessor, ct))
                return Fail<DeploymentReceipt>("Quoted predecessor changed; reconcile its current operation.");
            saved = await store.InvalidateReplacementQuoteAsync(parent, predecessor, ct);
        }
        else
        {
            if (recovery.Predecessor is not null) return Fail<DeploymentReceipt>("Quoted predecessor linkage changed.");
            parent.Stage = "RecoveryQuoteInvalidated";
            saved = await store.UpdateAsync(parent, parent.Version, ct);
        }
        return saved is null ? Fail<DeploymentReceipt>("Operation changed; reconcile or retry the exact invalidation request.")
            : AZOAResult<DeploymentReceipt>.Success(View(saved));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<DeploymentReceipt>> RecoverWithinBudgetAsync(string operationId, Guid actor, Guid? tenant,
        DeploymentOriginalBudgetRecoveryRequest request, CancellationToken ct = default)
    {
        if (request is null || !request.AcknowledgeRecovery || request.Mode is not ("AbortCleanup" or "ContinueCleanup" or "FinalizeAuthority"))
            return Fail<DeploymentReceipt>("Explicit recovery mode consent is required.");
        var parent = await OwnedAsync(operationId, actor, tenant, ct);
        if (parent is null) return Fail<DeploymentReceipt>("Original operation not found.");
        if (parent.RecoveryMode == request.Mode && parent.RecoveryChildId is null)
            return await AdvanceAsync(parent, actor, tenant, true, ct);
        if (parent.Version != request.ExpectedVersion || !RecoveryEligible(parent) || parent.RecoveryMode != "None")
            return Fail<DeploymentReceipt>("Original operation must be resolved and unchanged before selecting recovery.");
        var original = Read<DeploymentStartRequest>(parent.RequestJson);
        if (original.NativeIntent is not { } intent || Module(original.Target) is not INativeRecoveryModule recovery)
            return Fail<DeploymentReceipt>("Provider does not support original-budget recovery.");
        var authority = await CheckSigningAuthorityAsync(intent, actor, tenant, ct);
        if (authority is not null) return Fail<DeploymentReceipt>(authority);
        var next = await recovery.GetRecoveryNextStageAsync(intent, Read<List<NativeStageHistory>>(parent.HistoryJson), request.Mode, ct);
        if (next.IsError || next.Result is null) return Fail<DeploymentReceipt>(next.Message);
        parent.RecoveryMode = request.Mode; parent.State = "AwaitingAuthorization";
        var saved = await store.UpdateAsync(parent, parent.Version, ct);
        return saved is null ? Fail<DeploymentReceipt>("Original operation changed while selecting recovery.")
            : await AdvanceAsync(saved, actor, tenant, true, ct);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<DeploymentRecoveryQuote>> QuoteRecoveryAsync(string operationId, Guid actor, Guid? tenant,
        DeploymentRecoveryQuoteRequest request, CancellationToken ct = default)
    {
        if (request is null || request.Authorities is null || request.Authorities.Any(item => item is null) || request.Budget is null)
            return Fail<DeploymentRecoveryQuote>("Recovery authorities and bounded costs are required.");
        var parent = await OwnedAsync(operationId, actor, tenant, ct);
        if (parent is null || parent.Version != request.ExpectedVersion || !RecoveryEligible(parent, allowChild: true)
            || !RecoveryKey(request.RequestKey) || request.Mode is not ("AbortCleanup" or "ContinueCleanup" or "FinalizeAuthority"))
            return Fail<DeploymentRecoveryQuote>("Recovery requires the current resolved original operation and a bounded new request key.");
        var original = Read<DeploymentStartRequest>(parent.RequestJson);
        if (original.NativeIntent is not { } source || Module(original.Target) is not INativeRecoveryModule)
            return Fail<DeploymentRecoveryQuote>("This operation does not support native loader recovery.");
        var predecessor = await RecoveryPredecessorAsync(parent, ct);
        if (predecessor.IsError) return Fail<DeploymentRecoveryQuote>(predecessor.Message);
        var predecessorWitness = predecessor.Result is { } prior ? await AuditedPredecessorAsync(parent, prior, ct) : null;
        if (predecessor.Result is not null && predecessorWitness is null)
            return Fail<DeploymentRecoveryQuote>("Inspect the complete current recovery ancestry before requesting a replacement quote.");
        var intent = new NativeOperationIntent(1, DeploymentIdentity.Operation(actor, tenant, request.RequestKey), "Recovery",
            source.ChainType, source.Network, source.ProtocolIdentity, source.ArtifactHash,
            JsonSerializer.SerializeToElement(new NativeRecoveryInput(source, Read<List<NativeStageHistory>>(parent.HistoryJson),
                request.Mode, request.PayerAddress, parent.Version, predecessorWitness)), request.Authorities, request.Budget with { QuoteId = "" });
        if (intent.OperationKey == parent.Id || !ValidAuthority(intent, actor, tenant)
            || !Positive(intent.Budget.MaximumNetworkFee) || !Positive(intent.Budget.MaximumLockedRent) || !Positive(intent.Budget.MaximumValue))
            return Fail<DeploymentRecoveryQuote>("Recovery requires explicit caller authority and canonical bounded costs.");
        var denied = await CheckSigningAuthorityAsync(intent, actor, tenant, ct);
        if (denied is not null) return Fail<DeploymentRecoveryQuote>(denied);
        var module = Module(original.Target);
        if (module is null) return Fail<DeploymentRecoveryQuote>("Native recovery provider is unavailable.");
        var result = await module.QuoteNativeOperationAsync(intent, ct);
        if (result.IsError || result.Result is null) return Fail<DeploymentRecoveryQuote>(result.Message);
        if (result.Result.ProtocolIdentity != source.ProtocolIdentity)
            return Fail<DeploymentRecoveryQuote>("Original protocol identity changed.");
        var quote = result.Result with { QuoteId = Guid.NewGuid().ToString("N"), IntentHash = NativeOperationAuthorization.IntentHash(intent) };
        intent = intent with { Quote = quote, Budget = intent.Budget with { QuoteId = quote.QuoteId } };
        if (!NativeOperationAuthorization.ValidQuote(intent)) return Fail<DeploymentRecoveryQuote>("Recovery cost exceeds the explicit caps.");
        await store.AddQuoteAsync(new NativeQuote { Id = quote.QuoteId, ActorUserId = actor.ToString("N"), TenantId = Tenant(tenant),
            IntentHash = quote.IntentHash, QuoteJson = JsonSerializer.Serialize(quote), ExpiresAt = quote.ExpiresAt }, ct);
        return AZOAResult<DeploymentRecoveryQuote>.Success(new(parent.Version, RecoveryBinding(parent, intent), intent));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<DeploymentReceipt>> StartRecoveryAsync(string operationId, Guid actor, Guid? tenant,
        DeploymentRecoveryStartRequest request, CancellationToken ct = default)
    {
        if (request?.Quote?.Intent is not { } supplied || supplied.Authorities is null
            || supplied.Authorities.Any(item => item is null) || supplied.Budget is null || supplied.Input.ValueKind != JsonValueKind.Object)
            return Fail<DeploymentReceipt>("An exact quoted recovery intent is required.");
        if (!request.AcknowledgeCosts || !RecoveryKey(request.RequestKey)) return Fail<DeploymentReceipt>("Explicit recovery cost consent and request key are required.");
        var parent = await OwnedAsync(operationId, actor, tenant, ct);
        if (parent is null) return Fail<DeploymentReceipt>("Original operation not found.");
        var original = Read<DeploymentStartRequest>(parent.RequestJson);
        var intent = request.Quote.Intent;
        var childId = DeploymentIdentity.Operation(actor, tenant, request.RequestKey);
        var childRequest = new DeploymentStartRequest(parent.ArtifactId, parent.ArtifactHash, original.Target, intent);
        var json = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(childRequest));
        var existing = await store.GetAsync(childId, ct);
        if (existing is not null)
            return existing.ParentOperationId == parent.Id && existing.ActorUserId == actor.ToString("N") && existing.TenantId == Tenant(tenant)
                && existing.RequestJson == json && existing.RecoveryBindingHash == request.Quote.BindingHash
                ? AZOAResult<DeploymentReceipt>.Success(View(existing)) : Fail<DeploymentReceipt>("Recovery key belongs to another immutable request.");
        if (parent.Version != request.Quote.ParentVersion || !RecoveryEligible(parent, allowChild: true) || intent.Kind != "Recovery"
            || intent.OperationKey != childId || !ValidAuthority(intent, actor, tenant)
            || request.Quote.BindingHash != RecoveryBinding(parent, intent) || !NativeOperationAuthorization.ValidQuote(intent))
            return Fail<DeploymentReceipt>("Recovery snapshot, quote or authority changed.");
        NativeRecoveryInput? recovery;
        try { recovery = Read<NativeRecoveryInput>(intent.Input.GetRawText()); }
        catch (JsonException) { return Fail<DeploymentReceipt>("Malformed recovery input."); }
        if (recovery is null || recovery.ParentVersion != request.Quote.ParentVersion || recovery.OriginalIntent is null || recovery.ResolvedHistory is null || recovery.ResolvedHistory.Any(item => item is null)
            || original.NativeIntent is null || CanonicalJson.Hash(recovery.OriginalIntent) != CanonicalJson.Hash(original.NativeIntent)
            || CanonicalJson.Hash(recovery.ResolvedHistory) != CanonicalJson.Hash(Read<List<NativeStageHistory>>(parent.HistoryJson)))
            return Fail<DeploymentReceipt>("Recovery does not describe the exact original operation and resolved history.");
        var savedQuote = await store.GetQuoteAsync(intent.Quote!.QuoteId, ct);
        if (savedQuote is null || savedQuote.ActorUserId != actor.ToString("N") || savedQuote.TenantId != Tenant(tenant)
            || savedQuote.IntentHash != intent.Quote.IntentHash || savedQuote.QuoteJson != JsonSerializer.Serialize(intent.Quote))
            return Fail<DeploymentReceipt>("Recovery quote is not the admitted server quote.");
        var predecessor = await RecoveryPredecessorAsync(parent, ct);
        if (predecessor.IsError) return Fail<DeploymentReceipt>(predecessor.Message);
        var expectedPredecessor = predecessor.Result is { } old ? await AuditedPredecessorAsync(parent, old, ct) : null;
        if ((predecessor.Result is not null && expectedPredecessor is null) || recovery.Predecessor != expectedPredecessor)
            return Fail<DeploymentReceipt>("Quoted recovery predecessor changed; obtain a new quote and consent.");
        var authority = await CheckSigningAuthorityAsync(intent, actor, tenant, ct);
        if (authority is not null) return Fail<DeploymentReceipt>(authority);
        var candidate = new DeploymentOperation
        {
            Id = childId, ActorUserId = parent.ActorUserId, TenantId = parent.TenantId, ArtifactId = parent.ArtifactId,
            ArtifactHash = parent.ArtifactHash, IntentHash = CanonicalJson.HashCanonical(json), RequestJson = json,
            ParentOperationId = parent.Id, RecoveryMode = recovery.Mode, RecoveryBindingHash = request.Quote.BindingHash,
            ReplacesOperationId = predecessor.Result?.Id,
            State = "Admitted", Stage = "RecoveryPlan", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var child = predecessor.Result is { } previous
            ? await store.ReplaceRecoveryChildAsync(parent, previous, candidate, ct)
            : await store.ClaimRecoveryChildAsync(parent, candidate, ct);
        return child is null ? Fail<DeploymentReceipt>("Recovery claim lost its original-operation fence.")
            : await AdvanceAsync(child, actor, tenant, true, ct);
    }

    private static bool RecoveryKey(string key) => !string.IsNullOrWhiteSpace(key) && key.Length <= 200 && !key.Any(char.IsControl);
    private static Task<AZOAResult<NativeNextStage>> NextNativeStageAsync(INativeOperationModule module, NativeOperationIntent intent,
        IReadOnlyList<NativeStageHistory> history, DeploymentOperation row, CancellationToken ct)
        => row.ParentOperationId is null && row.RecoveryMode != "None"
            ? module is INativeRecoveryModule recovery ? recovery.GetRecoveryNextStageAsync(intent, history, row.RecoveryMode, ct)
                : Task.FromResult(Fail<NativeNextStage>("Provider recovery capability is unavailable."))
            : module.GetNextStageAsync(intent, history, ct);
    private static string CompletionState(NativeNextStage stage)
        => stage.Disposition switch { "AbortedClean" => "AbortedClean", "FailedClean" => "FailedClean",
            "Normal" or "Succeeded" => "Confirmed", _ => throw new InvalidOperationException("Unknown terminal native disposition.") };
    private async Task<DeploymentOperation> ProjectRecoveryAsync(DeploymentOperation child, CancellationToken ct)
    {
        if (child.ParentOperationId is null || child.RecoveryProjected || child.State != "Confirmed") return child;
        var parent = await store.GetAsync(child.ParentOperationId, ct) ?? throw new InvalidOperationException("Recovery parent disappeared.");
        await store.CompleteRecoveryChildAsync(child.Id, child.Version, parent.Version, ct);
        return await RequireCurrentAsync(child.Id, ct);
    }
    private static bool RecoveryEligible(DeploymentOperation parent, bool allowChild = false)
        => parent.ParentOperationId is null && (allowChild || parent.RecoveryChildId is null) && parent.PreparedJson is null && !parent.SubmissionAuthorized
            && parent.State is not ("Confirmed" or "Cancelled" or "AbortedClean" or "FailedClean")
            && Read<List<NativeStageHistory>>(parent.HistoryJson) is { Count: > 0 } history
            && history.All(item => item.Observation.Verdict is "Confirmed" or "FailedOnChain" or "ExpiredNoEffect");
    private static string RecoveryBinding(DeploymentOperation parent, NativeOperationIntent intent)
        => CanonicalJson.Hash(new { parent.Id, parent.Version, parent.Generation, parent.RequestJson, parent.HistoryJson,
            IntentHash = NativeOperationAuthorization.IntentHash(intent) });
}
