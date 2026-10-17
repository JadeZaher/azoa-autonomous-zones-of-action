using System.Text.Json;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;

namespace AZOA.WebAPI.Services.Users;

public sealed partial class NftLifecycleService
{
    /// <inheritdoc/>
    public async Task<AZOAResult<NftLifecycleView>> RenewActorAuthorityAsync(string operationId, NftStageAcceptance request,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        if (request is null || request.RequestId == Guid.Empty) return Refused("Exact requester renewal is required.");
        var row = await store.GetAsync(operationId, ct);
        if (row is null || row.ActorUserId != credential.ActorUserId.ToString("N")) return Refused("NFT operation not found.");
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson)!;
        if (command.ActingTenantId != (credential.TenantId ?? Guid.Empty)) return Refused("NFT operation not found.");
        var prior = NftActorContinuationIdentity.Read(row).SingleOrDefault(item => item.Request.RequestId == request.RequestId);
        if (prior is not null) return prior.Request == request && prior.Authority.Credential.ActorUserId == credential.ActorUserId
            ? AZOAResult<NftLifecycleView>.Success(View(row)) : Refused("The renewal request is bound to another lifecycle version.");
        if (command.ActorAuthority?.Kind is not ("DirectJwt" or "DirectApiKey") || command.ActionAuthority is not null)
            return Refused("Workflow authority continues through its original run, not a direct renewal.");
        if (!NftActorContinuationIdentity.Continuable(row) || request.ExpectedVersion != row.Version || request.State != row.State
            || request.Stage != row.Stage || request.Generation != row.PreparationGeneration)
            return Refused("NFT stage changed; review its current state before renewing authority.");
        var wallet = await wallets.GetByIdAsync(command.ActorWalletId, ct);
        if (wallet.IsError || wallet.Result is null || wallet.Result.Address != command.ActorAddress)
            return Refused("The original requester wallet changed.");
        var authority = await BindActorAsync(command.Actor, command.ActingTenantId, wallet.Result, row.Operation, credential, ct);
        if (authority is null) return Refused("Current requester authority is required to renew this NFT operation.");
        var renewal = new NftActorContinuation(request, authority, clock.GetUtcNow());
        row.ActorContinuationsJson = JsonSerializer.Serialize(NftActorContinuationIdentity.Read(row).Append(renewal));
        if (!NftActorContinuationIdentity.Matches(row, command, renewal)) return Refused("Renewed authority does not match the original requester.");
        var saved = await SaveAsync(row, ct, requireActionAuthority: true);
        if (saved is not null) return AZOAResult<NftLifecycleView>.Success(View(saved));
        var current = await ReloadAsync(row.Id, ct);
        return NftActorContinuationIdentity.Read(current).Any(item => item.Request == request)
            ? AZOAResult<NftLifecycleView>.Success(View(current))
            : Refused("Requester renewal did not commit; refresh the lifecycle before retrying.");
    }
}
