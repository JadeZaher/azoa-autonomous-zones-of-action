using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Signing;

namespace AZOA.WebAPI.Services.Dex;

/// <summary>Owns authenticated immutable swap admission and public operation projections.</summary>
public sealed partial class SwapOperationService(ISwapOperationStore operations, IDexQuoteStore quotes,
    SigningAdmissionAuthorityFactory authorities, DexModuleResolver modules, SwapStageCoordinator stages, SwapRecoveryService recovery,
    IRealValueKycGate valueGate)
{
    public async Task<AZOAResult<SwapOperationView>> StartAsync(SigningCredentialWitness credential,
        SwapAdmissionRequest request, WorkflowSigningWitness? workflow = null, CancellationToken ct = default)
    {
        if (request is null || !request.AcceptEconomicEffects || !ValidKey(request.RequestKey) ||
            !Guid.TryParseExact(request.QuoteId, "N", out var quoteId) || quoteId == Guid.Empty ||
            request.QuoteHash is not { Length: 64 } || credential.ActorUserId == Guid.Empty || credential.TenantId == Guid.Empty)
            return Fail("Explicit acceptance of the original quote and a stable request key are required.");
        var key = SwapOperationIdentity.RequestKey(request.RequestKey);
        var id = SwapOperationIdentity.Operation(credential.ActorUserId, credential.TenantId, key);
        var hash = CanonicalJson.Hash(request);
        var existing = await operations.GetAsync(id, ct);
        if (existing is not null)
        {
            if (!InScope(existing, credential, false) || existing.RequestHash != hash || existing.RequestKeyHash != key ||
                !SameWorkflow(existing, workflow)) return Fail("The request key already belongs to a different immutable swap.");
            return AZOAResult<SwapOperationView>.Success(await ProjectAsync(existing, ct));
        }
        if (await ValueRefusalAsync(credential, null, ct) is { } refused) return refused;
        var quote = await quotes.GetAsync(request.QuoteId, ct);
        if (quote is null || quote.ActorUserId != credential.ActorUserId.ToString("N") || quote.TenantId != Tenant(credential.TenantId) ||
            quote.QuoteHash != request.QuoteHash || quote.QuoteHash != CanonicalJson.HashCanonical(quote.QuoteJson))
            return Fail("The accepted quote does not belong to this authenticated scope.");
        var terms = JsonSerializer.Deserialize<DexQuoteTerms>(quote.QuoteJson)!;
        var authority = await BindAsync(credential, terms, workflow, ct);
        if (authority.IsError || authority.Result is null) return Fail(authority.Message);
        var intent = new SwapIntent(1, id, request.QuoteId, request.QuoteHash, terms, hash);
        var json = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(intent));
        var now = DateTimeOffset.UtcNow;
        var candidate = new SwapOperationRecord
        {
            Id = id, ActorUserId = credential.ActorUserId.ToString("N"), TenantId = Tenant(credential.TenantId),
            RequestKeyHash = key, RequestHash = hash, QuoteId = request.QuoteId, QuoteHash = request.QuoteHash,
            IntentJson = json, IntentHash = CanonicalJson.HashCanonical(json),
            ActionRunId = workflow?.Action.RunId.ToString("N"), ActionNodeId = workflow?.Action.NodeId.ToString("N"),
            ActionIntentHash = workflow?.Action.IntentHash, CreatedAt = now, UpdatedAt = now, NextAttemptAt = now,
        };
        var saved = await operations.AdmitAsync(candidate, authority.Result, ct);
        return saved is null ? Fail("The quote expired, was consumed, or its current authority changed before admission.")
            : AZOAResult<SwapOperationView>.Success(await ProjectAsync(saved, ct));
    }

    public async Task<AZOAResult<SwapOperationView>> GetAsync(string operationId, SigningCredentialWitness credential, CancellationToken ct = default)
    {
        var operation = await ReadOwnedAsync(operationId, credential, true, ct);
        return operation is null ? Fail("Swap not found in the authenticated participant scope.")
            : AZOAResult<SwapOperationView>.Success(await ProjectAsync(operation, ct));
    }

    private Task<AZOAResult<SigningAdmissionAuthority>> BindAsync(SigningCredentialWitness credential, DexQuoteTerms terms,
        WorkflowSigningWitness? workflow, CancellationToken ct)
    {
        if (terms.ActorUserId != credential.ActorUserId || terms.TenantId != credential.TenantId ||
            workflow is not null && CanonicalJson.Hash(workflow.Credential) != CanonicalJson.Hash(credential))
            return Task.FromResult(AZOAResult<SigningAdmissionAuthority>.Failure("Original swap and signing credential scope differ."));
        return workflow is null
            ? authorities.BindDirectAsync(credential, terms.Owner.WalletId, terms.Network.ChainType, terms.Owner.Address, [AzoaScopes.SwapSign], ct)
            : authorities.BindWorkflowAsync(workflow, terms.Owner.WalletId, terms.Network.ChainType, terms.Owner.Address, ct);
    }

    private async Task<SwapOperationRecord?> ReadOwnedAsync(string id, SigningCredentialWitness credential, bool directOwner, CancellationToken ct)
    {
        if (id is not { Length: 64 } || id.Any(character => !char.IsAsciiHexDigitLower(character)) || !SwapScope(credential)) return null;
        var operation = await operations.GetAsync(id, ct);
        return operation is not null && InScope(operation, credential, directOwner) ? operation : null;
    }

    private async Task<SwapOperationView> ProjectAsync(SwapOperationRecord operation, CancellationToken ct)
    {
        var intent = JsonSerializer.Deserialize<SwapIntent>(operation.IntentJson)!;
        var stage = operation.CurrentStageId is { } stageId ? await operations.GetStageAsync(stageId, ct) : null;
        var plan = stage is null ? null : JsonSerializer.Deserialize<SwapStagePlan>(stage.PlanJson);
        var candidate = stage?.PreparedJson is { } prepared ? JsonSerializer.Deserialize<PreparedSwapStage>(prepared) : null;
        var observed = stage?.ObservationJson is { } observation ? JsonSerializer.Deserialize<SwapStageObservation>(observation) : null;
        var settled = operation.SettlementReceiptJson is { } receipt ? JsonSerializer.Deserialize<SwapSettlementReceipt>(receipt) : null;
        var accounting = new SwapAccountingView(operation.UnresolvedRentStageCount == 0 &&
            (!operation.CleanupStarted || operation.CleanupReceiptJson is not null), operation.ConfirmedEffectNetworkFee,
            operation.ConfirmedCleanupNetworkFee, operation.ConfirmedRetainedRent, operation.UnresolvedRentReservation,
            operation.UnresolvedRentStageCount, operation.ConfirmedReleasedRent);
        return new(SurrealRecordGuid.BareId(operation.Id), operation.QuoteId, operation.QuoteHash, operation.State, stage?.Stage ?? "",
            operation.Version, intent.Terms.Network, observed?.TransactionIds ?? candidate?.TransactionIds ?? [], settled?.ActualInput, settled?.ActualOutput,
            operation.CleanupReceiptJson is not null ? [] : settled?.RetainedEffects ?? [], observed?.Detail, accounting,
            operation.CleanupReceiptJson is not null || settled is not null && !operation.CleanupStarted,
            stage is null || plan is null ? null : new(SurrealRecordGuid.BareId(stage.Id), stage.Stage, stage.Version, stage.State, plan.Purpose,
                observed?.TransactionIds ?? candidate?.TransactionIds ?? [], observed?.ActualInput, observed?.ActualOutput, observed?.PlatformFeePaid,
                observed?.NetworkFeePaid, stage.ResolvedLockedBalance ?? observed?.LockedBalance,
                stage.AuthorizedAt is not null && stage.ServerSubmissionWithdrawnAt is null,
                stage.ExternalExposedAt is not null, stage.ServerSubmissionWithdrawnAt is not null, plan.PreparationKey,
                stage.PreparedJson is not null));
    }

    private static bool InScope(SwapOperationRecord row, SigningCredentialWitness credential, bool directOwner)
        => credential.ActorUserId != Guid.Empty && credential.TenantId != Guid.Empty && row.ActorUserId == credential.ActorUserId.ToString("N") &&
            (row.TenantId == Tenant(credential.TenantId) || directOwner && credential.TenantId is null);
    private static bool SameWorkflow(SwapOperationRecord row, WorkflowSigningWitness? workflow)
        => row.ActionRunId == workflow?.Action.RunId.ToString("N") && row.ActionNodeId == workflow?.Action.NodeId.ToString("N") &&
            row.ActionIntentHash == workflow?.Action.IntentHash;
    /// <summary>Swap reads and commands sit under the same swap-signing ceiling as admission.</summary>
    internal static bool SwapScope(SigningCredentialWitness credential)
        => credential.OwnerAuthority || credential.Scopes.Contains(AzoaScopes.SwapSign, StringComparer.Ordinal);
    private static bool ValidKey(string key) => key is { Length: > 0 and <= 256 } && key == key.Trim() && !key.Any(char.IsControl);
    private static string Tenant(Guid? tenant) => tenant?.ToString("N") ?? "";
    /// <summary>X-2: the risk-hold-wrapped real-value gate (KYC + reversal pause); a residual-return Cleanup stage stays open.</summary>
    private async Task<AZOAResult<SwapOperationView>?> ValueRefusalAsync(SigningCredentialWitness credential, SwapStageRecord? stage,
        CancellationToken ct)
    {
        if (stage is not null && JsonSerializer.Deserialize<SwapStagePlan>(stage.PlanJson)?.Purpose == "Cleanup") return null;
        var admitted = credential.TenantId is { } tenant
            ? await valueGate.RequireCurrentApprovalAsync(credential.ActorUserId, tenant, ct)
            : await valueGate.RequireCurrentApprovalAsync(credential.ActorUserId, ct);
        return admitted.IsError || !admitted.Result
            ? AZOAResult<SwapOperationView>.FailureWithCode(admitted.Message ?? "This account cannot move value right now.", AzoaErrorCodes.Forbidden)
            : null;
    }

    private static AZOAResult<SwapOperationView> Fail(string message) => AZOAResult<SwapOperationView>.FailureWithCode(message, "Conflict");
}
