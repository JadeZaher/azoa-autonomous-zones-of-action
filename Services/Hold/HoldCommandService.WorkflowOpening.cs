using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;

namespace AZOA.WebAPI.Services.Hold;

public sealed partial class HoldCommandService
{
    /// <summary>Reads exact recovery evidence after the version fence without changing the parked opening.</summary>
    public async Task<AZOAResult<HoldWorkflowOpeningResolution>> ReconcileWorkflowRecoveryAsync(string id,
        HoldWorkflowOpeningRecoveryLookup lookup, SigningCredentialWitness credential, CancellationToken ct = default)
    {
        if (lookup?.Request is not { } request || request.RequestId == Guid.Empty || request.Version < 0
            || lookup.Kind is not ("WorkflowOpeningReplace" or "WorkflowOpeningWithdraw"))
            return AZOAResult<HoldWorkflowOpeningResolution>.Failure("The original recovery request is required.");
        var row = await store.GetCommandAsync(id, ct);
        if (row?.WorkflowMandateDraftJson is null || row.ActorUserId != credential.ActorUserId.ToString("N")
            || (lookup.Kind == "WorkflowOpeningWithdraw" ? credential.TenantId is not null || !credential.OwnerAuthority
                || credential.Kind != "Jwt" || credential.TokenUse != AzoaClaims.TokenUseLogin
                : row.TenantId != credential.TenantId?.ToString("N")))
            return AZOAResult<HoldWorkflowOpeningResolution>.Failure("The original recovery request is unavailable.");
        var auditId = HoldRecoveryIdentity.Id(lookup.Kind, id, credential.ActorUserId, credential.TenantId, request.RequestId);
        var hash = HoldRecoveryIdentity.RequestHash(lookup.Kind, id, credential.ActorUserId, credential.TenantId, request);
        var receipt = await store.GetRecoveryAsync(auditId, ct);
        if (receipt is not null && (receipt.Kind != lookup.Kind || receipt.OperationId != id || receipt.RequestHash != hash
            || receipt.ActorUserId != row.ActorUserId || receipt.TenantId != credential.TenantId?.ToString("N")))
            return AZOAResult<HoldWorkflowOpeningResolution>.Failure("The request key belongs to another recovery command.");
        var result = receipt is null ? await ReadResultAsync(row, ct)
            : Result(JsonSerializer.Deserialize<AZOA.WebAPI.Persistence.SurrealDb.Models.HoldCommand>(receipt.OutcomeJson)!);
        return result.IsError || result.Result is null ? AZOAResult<HoldWorkflowOpeningResolution>.Failure(result.Message)
            : AZOAResult<HoldWorkflowOpeningResolution>.Success(new(receipt is not null ? "Applied"
                : row.Version > request.Version ? "Obsolete" : "Unresolved", result.Result));
    }

    /// <summary>Refreshes provider costs for the unchanged unaccepted opening and requires new explicit acceptance.</summary>
    public async Task<AZOAResult<HoldCommandView>> ReplaceWorkflowOpeningAsync(string id, HoldRecoveryRequest request,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        var row = await store.GetCommandAsync(id, ct);
        if (request is null || row?.WorkflowMandateDraftJson is null || row.ActorUserId != credential.ActorUserId.ToString("N")
            || row.TenantId != credential.TenantId?.ToString("N")) return Refuse();
        var auditId = HoldRecoveryIdentity.Id("WorkflowOpeningReplace", id, credential.ActorUserId, credential.TenantId, request.RequestId);
        var hash = HoldRecoveryIdentity.RequestHash("WorkflowOpeningReplace", id, credential.ActorUserId, credential.TenantId, request);
        var prior = await store.GetRecoveryAsync(auditId, ct);
        if (prior is not null) return prior.Kind == "WorkflowOpeningReplace" && prior.RequestHash == hash
            ? Result(JsonSerializer.Deserialize<AZOA.WebAPI.Persistence.SurrealDb.Models.HoldCommand>(prior.OutcomeJson)!) : Refuse();
        if (row.State != "AwaitingConsent" || row.Version != request.Version) return Refuse();
        var draft = JsonSerializer.Deserialize<HoldWorkflowMandateDraft>(row.WorkflowMandateDraftJson)!;
        var authority = await authorities.BindConsentAsync(credential, [AzoaScopes.TransferSign], ct);
        if (authority.IsError || authority.Result is null) return AZOAResult<HoldCommandView>.Failure(authority.Message);
        var access = await valueAccess.RequireValueAccessAsync(credential.ActorUserId, credential.TenantId, ct);
        if (access.IsError) return AZOAResult<HoldCommandView>.Failure(access.Message);
        var provider = providers.GetProvider(draft.Terms.Asset.ChainType, draft.Terms.Asset.Network);
        if (!provider.TryGetModule<IValueCustodyModule>(out var custody))
            return AZOAResult<HoldCommandView>.Failure("Current complete custody costs are unavailable.");
        var quote = await custody!.QuoteValueCustodyAsync(draft.Quote.Request, ct);
        if (quote.IsError || quote.Result is null) return AZOAResult<HoldCommandView>.Failure(quote.Message);
        var replacement = await store.ReplaceWorkflowOpeningAsync(row, draft with { Quote = quote.Result }, request, authority.Result, ct);
        return replacement is null ? AZOAResult<HoldCommandView>.Failure("The original execution, immutable mandate bounds or disclosure version changed.")
            : Result(replacement);
    }

    /// <summary>Withdraws an unaccepted opening using current owner authentication without reviving its workflow.</summary>
    public async Task<AZOAResult<HoldCommandView>> WithdrawWorkflowOpeningAsync(string id, HoldRecoveryRequest request,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        var row = await store.WithdrawWorkflowOpeningAsync(id, request, credential, ct);
        return row is null ? AZOAResult<HoldCommandView>.Failure("Only the original owner can withdraw an unchanged unaccepted opening.") : Result(row);
    }

    /// <summary>Resolves an original acceptance without executing it or inferring noncommit from absence.</summary>
    public async Task<AZOAResult<HoldWorkflowOpeningResolution>> ReconcileWorkflowOpeningAsync(string id,
        HoldWorkflowOpeningAcceptance request, Guid actor, Guid? tenant, CancellationToken ct = default)
    {
        if (actor == Guid.Empty || request is null || request.RequestId == Guid.Empty || request.Version < 0
            || request.DraftHash is not { Length: 64 })
            return AZOAResult<HoldWorkflowOpeningResolution>.Failure("The original opening acceptance is required.");
        var row = await store.GetCommandAsync(id, ct);
        if (row?.WorkflowMandateDraftJson is null || row.ActorUserId != actor.ToString("N")
            || row.TenantId != tenant?.ToString("N"))
            return AZOAResult<HoldWorkflowOpeningResolution>.Failure("The original opening is unavailable.");
        var auditId = HoldWorkflowOpeningIdentity.AuditId(id, actor, tenant, request.RequestId);
        var hash = HoldWorkflowOpeningIdentity.RequestHash(id, row.IntentHash, request);
        var receipt = await store.GetRecoveryAsync(auditId, ct);
        if (receipt is not null && (receipt.Kind != "WorkflowOpeningAccept" || receipt.OperationId != id
            || receipt.RequestHash != hash || receipt.ActorUserId != row.ActorUserId || receipt.TenantId != row.TenantId))
            return AZOAResult<HoldWorkflowOpeningResolution>.Failure("The request key belongs to another acceptance.");
        var result = await ReadResultAsync(row, ct);
        if (result.IsError || result.Result is null)
            return AZOAResult<HoldWorkflowOpeningResolution>.Failure(result.Message);
        return AZOAResult<HoldWorkflowOpeningResolution>.Success(new(receipt is not null ? "Applied"
            : row.Version > request.Version ? "Obsolete" : "Unresolved", result.Result));
    }

    /// <summary>Accepts the exact frozen workflow opening disclosure under the actual actor's current credential.</summary>
    public async Task<AZOAResult<HoldCommandView>> AcceptWorkflowOpeningAsync(string id, HoldWorkflowOpeningAcceptance request,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        var row = await store.GetCommandAsync(id, ct);
        if (request is null || request.RequestId == Guid.Empty || row?.WorkflowMandateDraftJson is null
            || row.ActorUserId != credential.ActorUserId.ToString("N") || row.TenantId != credential.TenantId?.ToString("N")) return Refuse();
        var auditId = HoldWorkflowOpeningIdentity.AuditId(id, credential.ActorUserId, credential.TenantId, request.RequestId);
        var hash = HoldWorkflowOpeningIdentity.RequestHash(id, row.IntentHash, request);
        var prior = await store.GetRecoveryAsync(auditId, ct);
        if (prior is not null)
        {
            if (prior.Kind != "WorkflowOpeningAccept" || prior.OperationId != id || prior.RequestHash != hash
                || prior.ActorUserId != row.ActorUserId || prior.TenantId != row.TenantId) return Refuse();
            var original = await store.GetReceiptAsync(id, ct);
            return original is null ? AZOAResult<HoldCommandView>.Failure("The original accepted opening receipt is unavailable.")
                : AZOAResult<HoldCommandView>.Success(JsonSerializer.Deserialize<HoldCommandView>(original.OutcomeJson)!);
        }
        var current = await authorities.BindConsentAsync(credential, [AzoaScopes.TransferSign], ct);
        if (current.IsError || current.Result is null) return AZOAResult<HoldCommandView>.Failure(current.Message);
        var ready = await valueAccess.RequireValueAccessAsync(credential.ActorUserId, credential.TenantId, ct);
        if (ready.IsError) return AZOAResult<HoldCommandView>.Failure(ready.Message);
        var receipt = await store.CommitWorkflowOpeningAsync(row, request, current.Result, ct);
        return receipt is null ? AZOAResult<HoldCommandView>.Failure("The original execution, exact disclosure or current authority changed.")
            : AZOAResult<HoldCommandView>.Success(JsonSerializer.Deserialize<HoldCommandView>(receipt.OutcomeJson)!);
    }
}
