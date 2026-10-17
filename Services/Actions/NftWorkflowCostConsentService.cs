using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Services.Signing;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>Discloses and accepts exact frozen workflow mint costs without replacing original authority.</summary>
public sealed class NftWorkflowCostConsentService(IActionNodeExecutionStore executions, IActionRunStore runs,
    INftLifecycleService lifecycle, INftProtocolCostQuoteStore quotes, INftWorkflowCostAcceptanceStore acceptances,
    SigningAdmissionAuthorityFactory authorities, TimeProvider clock)
{
    private sealed record Context(ActionNodeExecution Execution, ActionExternalOperationReference Reference,
        WorkflowSigningWitness OriginalAuthority, UserNFTMintModel Draft);

    public async Task<AZOAResult<NftWorkflowCostDisclosure>> QuoteAsync(Guid executionId, SigningCredentialWitness credential, CancellationToken ct = default)
    {
        var context = await ReadContextAsync(executionId, credential.ActorUserId, credential.TenantId, ct);
        if (context is null) return AZOAResult<NftWorkflowCostDisclosure>.Failure("The original active NFT execution is unavailable.");
        var current = await authorities.BindConsentAsync(credential, [AzoaScopes.NftMint], ct);
        if (current.IsError) return AZOAResult<NftWorkflowCostDisclosure>.Failure(current.Message);
        var offered = await lifecycle.QuoteMintCostsAsync(credential.ActorUserId, credential.ActorUserId, context.Draft,
            context.Reference.RequestKey, ct, credential.TenantId ?? Guid.Empty);
        return offered.IsError || offered.Result is null ? AZOAResult<NftWorkflowCostDisclosure>.Failure(offered.Message)
            : AZOAResult<NftWorkflowCostDisclosure>.Success(new(executionId, context.Execution.Version, NftProtocolCostOffer.From(offered.Result)));
    }

    public async Task<AZOAResult<NftLifecycleView>> AcceptAsync(NftWorkflowCostAcceptance request,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        if (!ValidRequest(request)) return AZOAResult<NftLifecycleView>.Failure("The exact saved acceptance request is required.");
        var prior = await acceptances.GetAsync(credential.ActorUserId, credential.TenantId, request.ExecutionId, request.RequestId, ct);
        if (prior is not null)
            return prior.RequestHash == NftWorkflowCostAcceptanceIdentity.RequestHash(request, prior.LifecycleId, prior.FrozenInputHash)
                ? await lifecycle.GetAsync(credential.ActorUserId, prior.LifecycleId, ct, credential.TenantId ?? Guid.Empty)
                : AZOAResult<NftLifecycleView>.Failure("This acceptance key belongs to another request.");
        var context = await ReadContextAsync(request.ExecutionId, credential.ActorUserId, credential.TenantId, ct);
        if (context is null || context.Execution.Version != request.ExpectedExecutionVersion)
            return AZOAResult<NftLifecycleView>.Failure("The original execution changed; reconcile this acceptance before retrying.");
        var quote = await quotes.GetAsync(request.Authorization.QuoteId, credential.ActorUserId, credential.TenantId ?? Guid.Empty, ct);
        if (quote is null || !NftProtocolCostContract.IsAuthorized(quote, request.Authorization)
            || quote.LifecycleId != context.Reference.OperationId
            || quote.DraftHash != NftProtocolCostContract.DraftHash(quote.LifecycleId, credential.ActorUserId, credential.ActorUserId, context.Draft))
            return AZOAResult<NftLifecycleView>.Failure("The selected quote does not describe this frozen workflow mint.");
        var current = await authorities.BindConsentAsync(credential, [AzoaScopes.NftMint], ct);
        if (current.IsError || current.Result is null) return AZOAResult<NftLifecycleView>.Failure(current.Message);
        context.Draft.ProtocolCostAuthorization = request.Authorization;
        var receipt = new NftWorkflowCostAcceptanceReceipt(1, request, context.Reference, context.OriginalAuthority,
            current.Result, quote, clock.GetUtcNow());
        if (!NftWorkflowCostAcceptanceIdentity.Matches(receipt))
            return AZOAResult<NftLifecycleView>.Failure("The original and accepting authorities do not match this execution.");
        var admitted = await lifecycle.AdmitWorkflowMintAsync(context.Draft, receipt, ct);
        if (admitted.IsError) return admitted;
        var committed = await acceptances.GetAsync(credential.ActorUserId, credential.TenantId, request.ExecutionId, request.RequestId, ct);
        return committed?.RequestHash == NftWorkflowCostAcceptanceIdentity.RequestHash(request, quote.LifecycleId, context.Reference.IntentHash)
            ? admitted : AZOAResult<NftLifecycleView>.Failure("No matching acceptance receipt was committed; reconcile the original request.");
    }

    public async Task<AZOAResult<NftWorkflowCostResolution>> ReconcileAsync(NftWorkflowCostAcceptance request,
        Guid actor, Guid? tenant, CancellationToken ct = default)
    {
        if (!ValidRequest(request) || actor == Guid.Empty)
            return AZOAResult<NftWorkflowCostResolution>.Failure("The original acceptance request is required.");
        var execution = await executions.GetByIdAsync(request.ExecutionId, ct);
        var audit = await acceptances.GetAsync(actor, tenant, request.ExecutionId, request.RequestId, ct);
        if (audit is not null)
        {
            if (audit.RequestHash != NftWorkflowCostAcceptanceIdentity.RequestHash(request, audit.LifecycleId, audit.FrozenInputHash))
                return AZOAResult<NftWorkflowCostResolution>.Failure("This acceptance key belongs to another request.");
            var original = await lifecycle.GetAsync(actor, audit.LifecycleId, ct, tenant ?? Guid.Empty);
            return original.IsError || original.Result is null ? AZOAResult<NftWorkflowCostResolution>.Failure(original.Message)
                : AZOAResult<NftWorkflowCostResolution>.Success(new("Applied", original.Result));
        }
        if (execution.IsError || execution.Result?.ExternalOperation is not { } reference
            || reference.ActorUserId != actor || reference.ActingTenantId != tenant || reference.Domain != "nft")
            return AZOAResult<NftWorkflowCostResolution>.Failure("The original scoped execution is unavailable.");
        return AZOAResult<NftWorkflowCostResolution>.Success(new(execution.Result.Version > request.ExpectedExecutionVersion ? "Obsolete" : "Unresolved"));
    }

    public async Task<AZOAResult<NftWorkflowCostResolution>> RetireAsync(NftWorkflowCostAcceptance request,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        var prior = await ReconcileAsync(request, credential.ActorUserId, credential.TenantId, ct);
        if (prior.IsError || prior.Result?.State is "Applied" or "Obsolete") return prior;
        var current = await authorities.BindConsentAsync(credential, [AzoaScopes.NftMint], ct);
        if (current.IsError || current.Result is null) return AZOAResult<NftWorkflowCostResolution>.Failure(current.Message);
        await acceptances.RetireAsync(request, current.Result, ct);
        return await ReconcileAsync(request, credential.ActorUserId, credential.TenantId, ct);
    }

    private async Task<Context?> ReadContextAsync(Guid id, Guid actor, Guid? tenant, CancellationToken ct)
    {
        var result = await executions.GetByIdAsync(id, ct);
        if (result.IsError || result.Result is not { State: ActionNodeState.Running } execution
            || execution.Id != id || execution.ExternalOperation is not { Version: 1, Domain: "nft" } reference
            || reference.ActorUserId != actor || reference.ActingTenantId != tenant
            || execution.FrozenInput is not { } frozen || execution.SigningAuthorityJson is not { } signing
            || NftWorkflowMintDraft.Read(frozen, reference) is not { } draft) return null;
        var original = JsonSerializer.Deserialize<WorkflowSigningWitness>(signing)
            ?? throw new InvalidOperationException("Original workflow signing authority is unreadable.");
        if (original.Action != new ActionEffectAuthority(execution.RunId, execution.NodeId, actor, tenant, reference.IntentHash)
            || reference.OperationId != NftLifecycleIdentity.ForRequest(actor, "Mint", reference.RequestKey, tenant ?? Guid.Empty)) return null;
        var run = await runs.GetByIdAsync(execution.RunId, ct);
        if (run.IsError || run.Result is null || run.Result.UserId != actor || run.Result.ActingTenantId != tenant
            || run.Result.Status is not (ActionRunStatus.Running or ActionRunStatus.AwaitingReconciliation)) return null;
        return new(execution, reference, original, draft);
    }

    private static bool ValidRequest(NftWorkflowCostAcceptance? request)
        => request is not null && request.RequestId != Guid.Empty && request.ExecutionId != Guid.Empty
            && request.ExpectedExecutionVersion >= 0 && request.ExpectedExecutionVersion < long.MaxValue
            && request.Authorization is not null;
}
