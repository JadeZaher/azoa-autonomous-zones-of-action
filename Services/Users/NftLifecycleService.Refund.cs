using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Users;

public sealed partial class NftLifecycleService
{
    /// <inheritdoc/>
    public Task<AZOAResult<NftLifecycleView>> RefundAsync(Guid actor, string sourceLifecycleId, string requestKey,
        CancellationToken ct = default, Guid actingTenantId = default, NftFeeAuthorization? feeAuthorization = null,
        AZOA.WebAPI.Models.Actions.ActionEffectAuthority? actionAuthority = null, AZOA.WebAPI.Models.Signing.SigningCredentialWitness? credential = null)
        => RefundCoreAsync(actor, sourceLifecycleId, requestKey, ct, actingTenantId, feeAuthorization, actionAuthority, false, credential);

    /// <inheritdoc/>
    public Task<AZOAResult<NftLifecycleView>> AdmitRefundAsync(Guid actor, string sourceLifecycleId, string requestKey,
        CancellationToken ct = default, Guid actingTenantId = default, NftFeeAuthorization? feeAuthorization = null,
        AZOA.WebAPI.Models.Actions.ActionEffectAuthority? actionAuthority = null)
        => RefundCoreAsync(actor, sourceLifecycleId, requestKey, ct, actingTenantId, feeAuthorization, actionAuthority, true);

    private async Task<AZOAResult<NftLifecycleView>> RefundCoreAsync(Guid actor, string sourceLifecycleId, string requestKey,
        CancellationToken ct, Guid actingTenantId, NftFeeAuthorization? feeAuthorization,
        AZOA.WebAPI.Models.Actions.ActionEffectAuthority? actionAuthority, bool admissionOnly, AZOA.WebAPI.Models.Signing.SigningCredentialWitness? credential = null)
    {
        if (actor == Guid.Empty || !ValidRequestKey(requestKey)) return Refused("An authenticated requester and Idempotency-Key are required.");
        var replay = await store.GetAsync(NftLifecycleIdentity.ForRequest(actor, "Refund", requestKey, actingTenantId), ct);
        if (replay is not null)
        {
            var original = JsonSerializer.Deserialize<NftLifecycleCommand>(replay.RequestJson)
                ?? throw new InvalidOperationException("NFT intent is unreadable.");
            if (replay.Stage == "Cancellation") return original.ActingTenantId == actingTenantId ? AZOAResult<NftLifecycleView>.Success(View(replay)) : Refused("NFT request not found.");
            if (original.SourceLifecycleId != sourceLifecycleId || original.ActingTenantId != actingTenantId
                || original.ActionAuthority != actionAuthority || original.FeeAuthorization != feeAuthorization) return Refused("Idempotency-Key is bound to another refund.");
            return admissionOnly ? AZOAResult<NftLifecycleView>.Success(View(replay)) : await AdvanceAsync(replay, actor, false, ct, actingTenantId);
        }
        var source = await store.GetAsync(sourceLifecycleId, ct);
        if (source is null || source.Operation != "Transfer" || source.State != "Completed" || !RequesterOwns(source, actor, actingTenantId))
            return Refused("A refund must reference this caller's completed NFT transfer.");
        var previous = JsonSerializer.Deserialize<NftLifecycleCommand>(source.RequestJson)
            ?? throw new InvalidOperationException("Original NFT transfer is unreadable.");
        var authority = await store.GetAuthorityAsync(source.NftId, ct);
        if (authority is null || authority.Burned || authority.Revoked || authority.Soulbound
            || authority.LatestLifecycleId != source.Id || authority.HolderUserId != previous.Recipient.ToString("N")
            || !ChainAddressIdentity.Equivalent(source.ChainType, authority.OwnerAddress, previous.RecipientAddress) || authority.AssetId != source.AssetId
            || authority.ProtocolIdentity != source.ProtocolIdentity || authority.ContractAddress != source.ContractAddress)
            return Refused("The original transfer's exact asset is no longer held by its recipient.");
        var provider = providers.GetProvider(source.ChainType, Enum.Parse<ChainNetwork>(source.Network));
        if (!provider.TryGetModule<INftTransactionModule>(out var module) || module is null) return Refused("NFT capability is unavailable.");
        var owned = await module.ReadNftOwnershipAsync(authority.AssetId, authority.OwnerAddress, authority.ProtocolIdentity, ct, authority.ContractAddress);
        if (owned.IsError || owned.Result is not { Exists: true }
            || !ChainAddressIdentity.Equivalent(source.ChainType, owned.Result.OwnerAddress, authority.OwnerAddress)) return Refused("Current recipient ownership cannot be proven.");
        var payer = await WalletAsync(actor, source.ChainType, ct, previous.ActorWalletId);
        var holder = await WalletAsync(previous.Recipient, source.ChainType, ct, previous.RecipientWalletId);
        if (payer is null || holder is null || !ChainAddressIdentity.Equivalent(source.ChainType, payer.Address, previous.ActorAddress)
            || !ChainAddressIdentity.Equivalent(source.ChainType, holder.Address, previous.RecipientAddress))
            return Refused("Original transfer signing wallets are unavailable.");
        var quote = NftNativeFeePolicy.Quote(configuration, module, provider.ChainType, provider.ActiveNetwork, "Refund", actor);
        if (quote.IsError || quote.Result is null) return Refused(quote.Message);
        if (!NftNativeFeePolicy.IsAuthorized(quote.Result, feeAuthorization)) return FeeAuthorizationRequired();
        var actorAuthority = await BindActorAsync(actor, actingTenantId, payer, "Refund", credential, ct, actionAuthority);
        if (actorAuthority is null) return Refused("Original refund requester authority is required.");
        var command = new NftLifecycleCommand(actor, previous.Recipient, payer.Id, holder.Id, payer.Address, holder.Address,
            previous.Metadata, authority.Version, actingTenantId, SourceLifecycleId: source.Id,
            FeeQuote: quote.Result, FeeAuthorization: feeAuthorization, ActionAuthority: actionAuthority, ActorAuthority: actorAuthority);
        var row = Candidate(actor, previous.Recipient, Guid.Parse(source.NftId), "Refund", source.ChainType, provider.ActiveNetwork, requestKey, command);
        row.State = "AwaitingRefundAuthorization";
        row.Stage = NftTransactionKinds.Transfer;
        row.AssetId = source.AssetId;
        row.ProtocolIdentity = source.ProtocolIdentity;
        row.ContractAddress = source.ContractAddress;
        row.TokenStandard = source.TokenStandard;
        row.CreationTransactionId = source.CreationTransactionId;
        var claimed = await ClaimAsync(row, ct);
        if (claimed is null) return Refused("The workflow no longer authorizes this NFT refund.");
        if (claimed.Stage == "Cancellation") return CancelledClaim(claimed, actingTenantId);
        return claimed.RequestHash != row.RequestHash ? Refused("Idempotency-Key is bound to another refund.")
            : AZOAResult<NftLifecycleView>.Success(View(claimed));
    }
}
