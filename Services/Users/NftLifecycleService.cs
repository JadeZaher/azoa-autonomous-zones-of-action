using System.Text.Json;
using SurrealForge.Client;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;

namespace AZOA.WebAPI.Services.Users;

/// <inheritdoc/>
public sealed partial class NftLifecycleService(INftLifecycleStore store, IWalletStore wallets, INftStore nfts,
    IBlockchainProviderFactory providers, IValueAccessService access, INodeGovernanceGuard governance,
    IConfiguration configuration, TimeProvider clock, INftProtocolCostQuoteStore protocolCosts,
    INftWorkflowCostAcceptanceStore workflowCosts, AZOA.WebAPI.Services.Signing.SigningAdmissionAuthorityFactory signingAuthorities, IActionNodeExecutionStore actionExecutions,
    IWalletOwnershipProofStore? ownershipProofs = null) : INftLifecycleService
{
    /// <inheritdoc/>
    public Task<AZOAResult<NftLifecycleView>> AdmitWorkflowMintAsync(UserNFTMintModel request,
        NftWorkflowCostAcceptanceReceipt receipt, CancellationToken ct = default)
        => MintCoreAsync(receipt.Reference.ActorUserId, receipt.Reference.ActorUserId, request, receipt.Reference.RequestKey,
            ct, receipt.Reference.ActingTenantId ?? Guid.Empty, true, receipt.OriginalAuthority.Action, receipt);

    /// <inheritdoc/>
    public Task<AZOAResult<NftLifecycleView>> MintAsync(Guid actor, Guid recipient, UserNFTMintModel request, string requestKey, CancellationToken ct = default, Guid actingTenantId = default, AZOA.WebAPI.Models.Actions.ActionEffectAuthority? actionAuthority = null, AZOA.WebAPI.Models.Signing.SigningCredentialWitness? credential = null)
        => MintCoreAsync(actor, recipient, request, requestKey, ct, actingTenantId, admissionOnly: false, actionAuthority, credential: credential);

    /// <inheritdoc/>
    public Task<AZOAResult<NftLifecycleView>> AdmitMintAsync(Guid actor, Guid recipient, UserNFTMintModel request, string requestKey, CancellationToken ct = default, Guid actingTenantId = default, AZOA.WebAPI.Models.Actions.ActionEffectAuthority? actionAuthority = null)
        => MintCoreAsync(actor, recipient, request, requestKey, ct, actingTenantId, admissionOnly: true, actionAuthority);

    private async Task<AZOAResult<NftLifecycleView>> MintCoreAsync(Guid actor, Guid recipient, UserNFTMintModel request, string requestKey, CancellationToken ct, Guid actingTenantId, bool admissionOnly, AZOA.WebAPI.Models.Actions.ActionEffectAuthority? actionAuthority,
        NftWorkflowCostAcceptanceReceipt? workflowAcceptance = null, AZOA.WebAPI.Models.Signing.SigningCredentialWitness? credential = null)
    {
        if (request is null || actor == Guid.Empty || recipient == Guid.Empty || !ValidRequestKey(requestKey)
            || string.IsNullOrWhiteSpace(request.ChainType) || string.IsNullOrWhiteSpace(request.MetadataURI)
            || !string.IsNullOrWhiteSpace(request.TokenId))
            return Refused("NFT mint requires identities, metadata, Idempotency-Key, and no imported token identifier.");
        if (actor != recipient && !request.IsSoulbound)
            return Refused("Third-party issuance requires a soulbound credential.");
        var replay = await store.GetAsync(NftLifecycleIdentity.ForRequest(actor, "Mint", requestKey, actingTenantId), ct);
        if (replay is not null)
        {
            var original = JsonSerializer.Deserialize<NftLifecycleCommand>(replay.RequestJson) ?? throw new InvalidOperationException("NFT intent is unreadable.");
            if (replay.Stage == "Cancellation") return original.ActingTenantId == actingTenantId ? AZOAResult<NftLifecycleView>.Success(View(replay)) : Refused("NFT request not found.");
            if (original.Actor != actor || original.Recipient != recipient || original.ActingTenantId != actingTenantId
                || original.ActionAuthority != actionAuthority
                || JsonSerializer.Serialize(original.Metadata) != JsonSerializer.Serialize(request))
                return Refused("Idempotency-Key is already bound to another NFT mint request.");
            return admissionOnly ? AZOAResult<NftLifecycleView>.Success(View(replay))
                : await AdvanceAsync(replay, actor, false, ct, actingTenantId, workflowCaller: actionAuthority is not null);
        }
        var gate = await access.RequireValueAccessAsync(actor, actingTenantId == Guid.Empty ? null : actingTenantId, ct);
        if (gate.IsError) return Refused(gate.Message);
        var policy = await governance.EnsureAllowedAsync(request.ChainType, "NFT", "nft:mint");
        if (policy.IsRefused) return Refused(policy.ConsumerMessage);
        var provider = providers.GetProvider(request.ChainType, providers.DefaultNetwork);
        if (!provider.TryGetModule<INftTransactionModule>(out var module) || module is null)
            return Refused("This chain does not offer a verified NFT lifecycle.");
        var quote = NftNativeFeePolicy.Quote(configuration, module, provider.ChainType, provider.ActiveNetwork, "Mint", actor);
        if (quote.IsError || quote.Result is null) return Refused(quote.Message);
        if (!NftNativeFeePolicy.IsAuthorized(quote.Result, request.FeeAuthorization)) return FeeAuthorizationRequired();
        var actorWallet = await WalletAsync(actor, provider.ChainType, ct, request.WalletId);
        var recipientWallet = recipient == actor ? actorWallet : await WalletAsync(recipient, provider.ChainType, ct);
        if (actorWallet is null || recipientWallet is null) return Refused("Both NFT participants need a wallet on this chain.");
        var actorAuthority = await BindActorAsync(actor, actingTenantId, actorWallet, "Mint", credential, ct, actionAuthority);
        if (actorAuthority is null) return Refused("The original authenticated NFT issuer credential is required.");
        NftProtocolCostQuote? protocolQuote = null;
        if (provider.ChainType == "Solana" || request.ProtocolCostAuthorization is not null)
        {
            if (request.ProtocolCostAuthorization is not { } acceptance)
                return Refused("Accept an NFT protocol cost quote for this exact request before minting.");
            protocolQuote = await protocolCosts.GetAsync(acceptance.QuoteId, actor, actingTenantId, ct);
            if (protocolQuote is null || !AZOA.WebAPI.Helpers.NftProtocolCostContract.IsAuthorized(protocolQuote, acceptance))
                return Refused("The selected NFT protocol cost quote is unavailable or differs from the accepted receipt.");
        }
        var command = new NftLifecycleCommand(actor, recipient, actorWallet.Id, recipientWallet.Id,
            actorWallet.Address, recipientWallet.Address, request, -1, actingTenantId, FeeQuote: quote.Result, FeeAuthorization: request.FeeAuthorization,
            ActionAuthority: actionAuthority, ProtocolCostQuote: protocolQuote, WorkflowCostAcceptance: workflowAcceptance, ActorAuthority: actorAuthority,
            PlatformAuthorityAddress: request.IsSoulbound ? module.CredentialAuthorityAddress : null);
        var row = Candidate(actor, recipient, Guid.NewGuid(), "Mint", provider.ChainType, provider.ActiveNetwork, requestKey, command);
        if (protocolQuote is not null)
        {
            var quotedIntent = AZOA.WebAPI.Helpers.NftLifecycleIntentFactory.Create(row, command, provider.ActiveNetwork,
                module.CredentialAuthorityAddress, actingTenantId);
            if (quotedIntent is null || protocolQuote.QuotedIntentHash != AZOA.WebAPI.Helpers.NftProtocolCostContract.IntentHash(quotedIntent, command.RecipientWalletId))
                return Refused("NFT participants or signing authority changed after quotation; review a new cost quote before admission.");
        }
        NftLifecycle? claimed;
        if (workflowAcceptance is null) claimed = await ClaimAsync(row, ct);
        else
        {
            var acceptance = await workflowCosts.ClaimAsync(row, workflowAcceptance, ct);
            claimed = acceptance is null ? null : await store.GetAsync(row.Id, ct);
        }
        if (claimed is null) return Refused("The selected quote or current authority no longer permits this NFT admission.");
        if (claimed.Stage == "Cancellation") return CancelledClaim(claimed, actingTenantId);
        if (claimed.RequestHash != row.RequestHash) return Refused("Idempotency-Key is already bound to another NFT request.");
        return admissionOnly ? AZOAResult<NftLifecycleView>.Success(View(claimed))
            : await AdvanceAsync(claimed, actor, false, ct, actingTenantId, workflowCaller: actionAuthority is not null);
    }

    /// <inheritdoc/>
    public Task<AZOAResult<NftLifecycleView>> MoveAsync(Guid actor, Guid nftId, string? recipientAddress, bool burn, string requestKey, CancellationToken ct = default, Guid actingTenantId = default, NftFeeAuthorization? feeAuthorization = null, Guid? sourceWalletId = null, AZOA.WebAPI.Models.Actions.ActionEffectAuthority? actionAuthority = null, AZOA.WebAPI.Models.Signing.SigningCredentialWitness? credential = null)
        => MoveCoreAsync(actor, nftId, recipientAddress, burn, requestKey, ct, actingTenantId, feeAuthorization, sourceWalletId, admissionOnly: false, actionAuthority, credential);

    /// <inheritdoc/>
    public Task<AZOAResult<NftLifecycleView>> AdmitMoveAsync(Guid actor, Guid nftId, string? recipientAddress, bool burn, string requestKey, CancellationToken ct = default, Guid actingTenantId = default, NftFeeAuthorization? feeAuthorization = null, Guid? sourceWalletId = null, AZOA.WebAPI.Models.Actions.ActionEffectAuthority? actionAuthority = null)
        => MoveCoreAsync(actor, nftId, recipientAddress, burn, requestKey, ct, actingTenantId, feeAuthorization, sourceWalletId, admissionOnly: true, actionAuthority);

    private async Task<AZOAResult<NftLifecycleView>> MoveCoreAsync(Guid actor, Guid nftId, string? recipientAddress, bool burn, string requestKey, CancellationToken ct, Guid actingTenantId, NftFeeAuthorization? feeAuthorization, Guid? sourceWalletId, bool admissionOnly, AZOA.WebAPI.Models.Actions.ActionEffectAuthority? actionAuthority, AZOA.WebAPI.Models.Signing.SigningCredentialWitness? credential = null)
    {
        if (!ValidRequestKey(requestKey)) return Refused("Idempotency-Key is required.");
        var replay = await store.GetAsync(NftLifecycleIdentity.ForRequest(actor, burn ? "Burn" : "Transfer", requestKey, actingTenantId), ct);
        if (replay is not null)
        {
            var original = JsonSerializer.Deserialize<NftLifecycleCommand>(replay.RequestJson) ?? throw new InvalidOperationException("NFT intent is unreadable.");
            if (replay.Stage == "Cancellation") return original.ActingTenantId == actingTenantId ? AZOAResult<NftLifecycleView>.Success(View(replay)) : Refused("NFT request not found.");
            if (replay.NftId != nftId.ToString("N") || original.ActingTenantId != actingTenantId
                || original.ActionAuthority != actionAuthority
                || (sourceWalletId.HasValue && sourceWalletId != original.ActorWalletId)
                || original.FeeAuthorization != feeAuthorization || (!burn && !ChainAddressIdentity.Equivalent(replay.ChainType, original.RecipientAddress, recipientAddress)))
                return Refused("Idempotency-Key is already bound to another NFT request.");
            return admissionOnly ? AZOAResult<NftLifecycleView>.Success(View(replay))
                : await AdvanceAsync(replay, actor, false, ct, actingTenantId, workflowCaller: actionAuthority is not null);
        }
        var authority = await store.GetAuthorityAsync(nftId.ToString("N"), ct);
        if (authority is null || authority.Burned || authority.Revoked || authority.HolderUserId != actor.ToString("N") || authority.Soulbound)
            return Refused("The NFT is not transferable by this caller.");
        var gate = await access.RequireValueAccessAsync(actor, actingTenantId == Guid.Empty ? null : actingTenantId, ct);
        if (gate.IsError) return Refused(gate.Message);
        var read = await nfts.GetUserNFTByIdAsync(nftId, ct);
        if (read.IsError || read.Result is null) return Refused("NFT metadata is unavailable.");
        var provider = providers.GetProvider(authority.ChainType, Enum.Parse<ChainNetwork>(authority.Network));
        if (!provider.TryGetModule<INftTransactionModule>(out var module) || module is null) return Refused("NFT operations are unavailable.");
        var quote = NftNativeFeePolicy.Quote(configuration, module, provider.ChainType, provider.ActiveNetwork, burn ? "Burn" : "Transfer", actor);
        if (quote.IsError || quote.Result is null) return Refused(quote.Message);
        if (!NftNativeFeePolicy.IsAuthorized(quote.Result, feeAuthorization)) return FeeAuthorizationRequired();
        var ownership = await module.ReadNftOwnershipAsync(authority.AssetId, authority.OwnerAddress, authority.ProtocolIdentity, ct, authority.ContractAddress);
        if (ownership.IsError || ownership.Result is not { Exists: true }
            || !ChainAddressIdentity.Equivalent(authority.ChainType, ownership.Result.OwnerAddress, authority.OwnerAddress)) return Refused("Current NFT ownership could not be proven.");
        var wallet = (await wallets.GetByUserAsync(actor, ct)).Result?.FirstOrDefault(item => item.ChainType.Equals(authority.ChainType, StringComparison.OrdinalIgnoreCase)
            && ChainAddressIdentity.Equivalent(authority.ChainType, item.Address, authority.OwnerAddress) && (!sourceWalletId.HasValue || item.Id == sourceWalletId));
        if (wallet is null || !ChainAddressIdentity.Equivalent(authority.ChainType, wallet.Address, authority.OwnerAddress)) return Refused("The current owner's signing wallet is unavailable.");
        var recipient = actor;
        var destination = wallet;
        if (burn)
        {
            var issuance = await store.GetAsync(authority.IssuanceLifecycleId, ct);
            var issuer = issuance is null ? null : JsonSerializer.Deserialize<NftLifecycleCommand>(issuance.RequestJson);
            if (issuer is null || issuer.Actor.ToString("N") != authority.CreatorUserId) return Refused("Original NFT creator is unavailable.");
            recipient = issuer.Actor;
            destination = await WalletAsync(recipient, authority.ChainType, ct, issuer.ActorWalletId);
            if (destination is null || !ChainAddressIdentity.Equivalent(authority.ChainType, destination.Address, issuer.ActorAddress)) return Refused("The original creator's signing wallet is unavailable.");
        }
        if (!burn)
        {
            if (string.IsNullOrWhiteSpace(recipientAddress)) return Refused("A recipient address is required.");
            var resolved = await ResolveRecipientWalletAsync(authority.ChainType, recipientAddress, ct);
            if (resolved.IsError || resolved.Result is null) return AZOAResult<NftLifecycleView>.FailureWithCode(resolved.Message, resolved.Code!);
            destination = resolved.Result;
            recipient = destination.UserId;
        }
        if (!burn && !read.Result.IsTransferable) return Refused("This NFT is not transferable.");
        var policy = await governance.EnsureAllowedAsync(authority.ChainType, "NFT", burn ? "nft:burn" : "nft:transfer");
        if (policy.IsRefused) return Refused(policy.ConsumerMessage);
        var nft = read.Result;
        var metadata = new UserNFTMintModel
        {
            ChainType = authority.ChainType, TokenId = authority.AssetId, NFTContractAddress = authority.ContractAddress,
            MetadataURI = nft.MetadataURI, TokenStandard = nft.TokenStandard, Name = nft.Name, Description = nft.Description,
            ImageURI = nft.ImageURI, Attributes = nft.Attributes, IsTransferable = nft.IsTransferable,
            RoyaltyPercentage = nft.RoyaltyPercentage, RoyaltyRecipient = nft.RoyaltyRecipient,
        };
        var actorAuthority = await BindActorAsync(actor, actingTenantId, wallet, burn ? "Burn" : "Transfer", credential, ct, actionAuthority);
        if (actorAuthority is null) return Refused("Original NFT participant authority is required.");
        var command = new NftLifecycleCommand(actor, recipient, wallet.Id, destination.Id, wallet.Address, destination.Address, metadata, authority.Version, actingTenantId, FeeQuote: quote.Result, FeeAuthorization: feeAuthorization, ActionAuthority: actionAuthority, ActorAuthority: actorAuthority);
        var row = Candidate(actor, recipient, nftId, burn ? "Burn" : "Transfer", authority.ChainType, provider.ActiveNetwork, requestKey, command);
        row.Stage = burn ? destination.Id == wallet.Id ? NftTransactionKinds.Burn : NftTransactionKinds.ReturnForBurn : NftTransactionKinds.Transfer;
        row.AssetId = authority.AssetId;
        row.ProtocolIdentity = authority.ProtocolIdentity;
        row.ContractAddress = authority.ContractAddress;
        row.TokenStandard = authority.TokenStandard;
        row.CreationTransactionId = authority.CreationTransactionId;
        var claimed = await ClaimAsync(row, ct);
        if (claimed is null) return Refused("The workflow no longer authorizes this NFT admission.");
        if (claimed.Stage == "Cancellation") return CancelledClaim(claimed, actingTenantId);
        return claimed.RequestHash != row.RequestHash ? Refused("Idempotency-Key is already bound to another NFT request.")
            : admissionOnly ? AZOAResult<NftLifecycleView>.Success(View(claimed))
                : await AdvanceAsync(claimed, actor, false, ct, actingTenantId, workflowCaller: actionAuthority is not null);
    }

    /// <inheritdoc/>
    public Task<AZOAResult<NftLifecycleView>> RevokeAsync(Guid actor, Guid nftId, string requestKey, CancellationToken ct = default, Guid actingTenantId = default, NftFeeAuthorization? feeAuthorization = null, string? reason = null, AZOA.WebAPI.Models.Actions.ActionEffectAuthority? actionAuthority = null, AZOA.WebAPI.Models.Signing.SigningCredentialWitness? credential = null)
        => RevokeCoreAsync(actor, nftId, requestKey, ct, actingTenantId, feeAuthorization, reason, admissionOnly: false, actionAuthority, credential);

    /// <inheritdoc/>
    public Task<AZOAResult<NftLifecycleView>> AdmitRevokeAsync(Guid actor, Guid nftId, string requestKey, CancellationToken ct = default, Guid actingTenantId = default, NftFeeAuthorization? feeAuthorization = null, string? reason = null, AZOA.WebAPI.Models.Actions.ActionEffectAuthority? actionAuthority = null)
        => RevokeCoreAsync(actor, nftId, requestKey, ct, actingTenantId, feeAuthorization, reason, admissionOnly: true, actionAuthority);

    private async Task<AZOAResult<NftLifecycleView>> RevokeCoreAsync(Guid actor, Guid nftId, string requestKey, CancellationToken ct, Guid actingTenantId, NftFeeAuthorization? feeAuthorization, string? reason, bool admissionOnly, AZOA.WebAPI.Models.Actions.ActionEffectAuthority? actionAuthority, AZOA.WebAPI.Models.Signing.SigningCredentialWitness? credential = null)
    {
        if (!ValidRequestKey(requestKey)) return Refused("Idempotency-Key is required.");
        var replay = await store.GetAsync(NftLifecycleIdentity.ForRequest(actor, "Revoke", requestKey, actingTenantId), ct);
        if (replay is not null)
        {
            var original = JsonSerializer.Deserialize<NftLifecycleCommand>(replay.RequestJson)
                ?? throw new InvalidOperationException("NFT intent is unreadable.");
            if (replay.Stage == "Cancellation") return original.ActingTenantId == actingTenantId ? AZOAResult<NftLifecycleView>.Success(View(replay)) : Refused("NFT request not found.");
            if (replay.NftId != nftId.ToString("N") || original.ActingTenantId != actingTenantId
                || original.ActionAuthority != actionAuthority
                || original.FeeAuthorization != feeAuthorization || original.Reason != reason)
                return Refused("Idempotency-Key is already bound to another credential revocation.");
            return admissionOnly ? AZOAResult<NftLifecycleView>.Success(View(replay))
                : await AdvanceAsync(replay, actor, false, ct, actingTenantId, workflowCaller: actionAuthority is not null);
        }
        var authority = await store.GetAuthorityAsync(nftId.ToString("N"), ct);
        if (authority is null || authority.Burned || authority.Revoked || !authority.Soulbound
            || authority.IssuerUserId != actor.ToString("N") || authority.HolderUserId == actor.ToString("N"))
            return Refused("Only the original issuer may revoke a live credential.");
        if (string.IsNullOrWhiteSpace(authority.OwnerAddress))
            return Refused("The credential holder's on-chain address is unrecorded; it cannot be clawed back.");
        var provenance = await store.FindCredentialAsync(authority.ChainType, authority.Network, authority.AssetId, authority.ContractAddress, ct);
        if (provenance is not { HasVerifiedIssuance: true }) return Refused("Credential issuance has no confirmed chain provenance.");
        var issuance = await store.GetAsync(authority.IssuanceLifecycleId, ct);
        var originalIssuance = issuance is null ? null : JsonSerializer.Deserialize<NftLifecycleCommand>(issuance.RequestJson);
        if (issuance is not { Operation: "Mint", State: "Completed" } || issuance.NftId != authority.NftId
            || issuance.ChainType != authority.ChainType || issuance.Network != authority.Network
            || issuance.AssetId != authority.AssetId || issuance.ProtocolIdentity != authority.ProtocolIdentity
            || issuance.ContractAddress != authority.ContractAddress || issuance.CreationTransactionId != authority.CreationTransactionId
            || originalIssuance?.Actor != actor || !originalIssuance.Metadata.IsSoulbound
            || string.IsNullOrWhiteSpace(originalIssuance.PlatformAuthorityAddress))
            return Refused("The original confirmed credential signer identity is unavailable.");
        var provider = providers.GetProvider(authority.ChainType, Enum.Parse<ChainNetwork>(authority.Network));
        if (!provider.TryGetModule<INftTransactionModule>(out var module) || module is null) return Refused("NFT operations are unavailable.");
        var quote = NftNativeFeePolicy.Quote(configuration, module, provider.ChainType, provider.ActiveNetwork, "Revoke", actor);
        if (quote.IsError || quote.Result is null) return Refused(quote.Message);
        if (!NftNativeFeePolicy.IsAuthorized(quote.Result, feeAuthorization)) return FeeAuthorizationRequired();
        var nft = await nfts.GetUserNFTByIdAsync(nftId, ct);
        var wallet = await WalletAsync(actor, authority.ChainType, ct);
        if (nft.IsError || nft.Result is null || wallet is null) return Refused("Credential metadata or issuer wallet is unavailable.");
        var metadata = new UserNFTMintModel
        {
            ChainType = authority.ChainType, TokenId = authority.AssetId, NFTContractAddress = authority.ContractAddress,
            MetadataURI = nft.Result.MetadataURI, Name = nft.Result.Name, Description = nft.Result.Description,
            ImageURI = nft.Result.ImageURI, Attributes = nft.Result.Attributes, IsSoulbound = true, IsTransferable = false,
        };
        var actorAuthority = await BindActorAsync(actor, actingTenantId, wallet, "Revoke", credential, ct, actionAuthority);
        if (actorAuthority is null) return Refused("Original credential issuer authority is required.");
        var command = new NftLifecycleCommand(actor, actor, wallet.Id, wallet.Id, wallet.Address, wallet.Address,
            metadata, authority.Version, actingTenantId, authority.OwnerAddress, Guid.Parse(authority.HolderUserId),
            Reason: reason, FeeQuote: quote.Result, FeeAuthorization: feeAuthorization, ActionAuthority: actionAuthority, ActorAuthority: actorAuthority,
            PlatformAuthorityAddress: originalIssuance.PlatformAuthorityAddress);
        var row = Candidate(actor, actor, nftId, "Revoke", authority.ChainType, Enum.Parse<ChainNetwork>(authority.Network), requestKey, command);
        row.Stage = NftTransactionKinds.Revoke;
        row.AssetId = authority.AssetId;
        row.ProtocolIdentity = authority.ProtocolIdentity;
        row.ContractAddress = authority.ContractAddress;
        row.TokenStandard = authority.TokenStandard;
        row.CreationTransactionId = authority.CreationTransactionId;
        var claimed = await ClaimAsync(row, ct);
        if (claimed is null) return Refused("The workflow no longer authorizes this NFT admission.");
        if (claimed.Stage == "Cancellation") return CancelledClaim(claimed, actingTenantId);
        return claimed.RequestHash != row.RequestHash ? Refused("Idempotency-Key is already bound to another credential revocation.")
            : admissionOnly ? AZOAResult<NftLifecycleView>.Success(View(claimed))
                : await AdvanceAsync(claimed, actor, false, ct, actingTenantId, workflowCaller: actionAuthority is not null);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NftLifecycleView>> GetAsync(Guid actor, string operationId, CancellationToken ct = default, Guid actingTenantId = default)
    {
        var row = await store.GetAsync(operationId, ct);
        return row is null || !Visible(row, actor, actingTenantId)
            ? Refused("NFT operation not found.") : AZOAResult<NftLifecycleView>.Success(View(row));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NftLifecycleView>> RefreshAsync(Guid actor, string operationId, bool accept, CancellationToken ct = default, Guid actingTenantId = default,
        AZOA.WebAPI.Models.Signing.SigningCredentialWitness? credential = null, NftStageAcceptance? acceptance = null)
    {
        var row = await store.GetAsync(operationId, ct);
        if (row is null || !Visible(row, actor, actingTenantId)) return Refused("NFT operation not found.");
        if (accept)
        {
            if (acceptance is null || acceptance.RequestId == Guid.Empty) return Refused("Exact NFT stage acceptance is required.");
            var prior = AZOA.WebAPI.Helpers.NftStageConsentIdentity.Read(row)
                .SingleOrDefault(item => item.Request?.RequestId == acceptance.RequestId);
            if (prior is not null)
                return prior.Request == acceptance && prior.Authority.Credential.ActorUserId == actor
                    && (prior.Authority.Credential.TenantId ?? Guid.Empty) == actingTenantId
                    ? await AdvanceAsync(row, actor, true, ct, actingTenantId,
                        allowPreparation: row.Stage == prior.Stage && row.PreparationGeneration == prior.Generation,
                        acceptance: acceptance)
                    : Refused("NFT acceptance request is already bound to another participant or stage.");
            if (acceptance.ExpectedVersion != row.Version || acceptance.State != row.State
                || acceptance.Stage != row.Stage || acceptance.Generation != row.PreparationGeneration)
                return Refused("NFT stage changed; review its current state before accepting.");
            if (row.State is "Preparing" or "Prepared" && AZOA.WebAPI.Helpers.NftStageConsentIdentity.Current(row) is not null
                && AZOA.WebAPI.Helpers.NftStageConsentIdentity.Required(row, JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson)!))
            {
                var command = JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson)!;
                if (!await RenewStageConsentAsync(row, command, actor, actingTenantId, credential, acceptance, ct))
                    return Refused("Only the participant who accepted this stage can renew its authority.");
                var renewed = await SaveAsync(row, ct, requireActionAuthority: true);
                if (renewed is null) return Refused("Your accepted stage credential is still valid or the stage changed; resume the saved acceptance instead.");
                return await AdvanceAsync(renewed, actor, true, ct, actingTenantId, credential: credential, acceptance: acceptance);
            }
            if (row.State is not ("AwaitingRecipientAcceptance" or "AwaitingCreatorDestruction" or "AwaitingRefundAuthorization"
                or "AwaitingRefundDestinationAcceptance" or "Preparing"))
                return Refused("This NFT stage is not awaiting new participant acceptance.");
            if (row.State == "Preparing" && !AZOA.WebAPI.Helpers.NftStageConsentIdentity.NeedsReplacementConsent(row,
                    JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson)!))
                return Refused("Resume the original saved NFT acceptance instead of creating a new one.");
        }
        var acceptanceUser = row.State == "AwaitingRefundDestinationAcceptance" ? row.ActorUserId : row.RecipientUserId;
        if (accept && acceptanceUser != actor.ToString("N")) return Refused("Only the required participant can authorize this NFT stage.");
        return await AdvanceAsync(row, actor, accept, ct, actingTenantId, credential: credential, acceptance: acceptance);
    }

    /// <inheritdoc/>
    public async Task<bool> IsCurrentOwnerAsync(Guid actor, Guid nftId, CancellationToken ct = default)
    {
        var authority = await store.GetAuthorityAsync(nftId.ToString("N"), ct);
        if (authority is null || authority.Burned || authority.Revoked || authority.HolderUserId != actor.ToString("N")) return false;
        var provider = providers.GetProvider(authority.ChainType, Enum.Parse<ChainNetwork>(authority.Network));
        if (!provider.TryGetModule<INftTransactionModule>(out var module) || module is null) return false;
        var result = await module.ReadNftOwnershipAsync(authority.AssetId, authority.OwnerAddress, authority.ProtocolIdentity, ct, authority.ContractAddress);
        return !result.IsError && result.Result is { Exists: true } && ChainAddressIdentity.Equivalent(authority.ChainType, result.Result.OwnerAddress, authority.OwnerAddress);
    }

    private async Task<AZOAResult<NftLifecycleView>> AdvanceAsync(NftLifecycle row, Guid actor, bool accept, CancellationToken ct, Guid actingTenantId, bool allowPreparation = true,
        AZOA.WebAPI.Models.Signing.SigningCredentialWitness? credential = null, NftStageAcceptance? acceptance = null, bool workflowCaller = false)
    {
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson) ?? throw new InvalidOperationException("NFT intent is unreadable.");
        if (row.State is "Completed" or "Failed" or "Cancelled") return AZOAResult<NftLifecycleView>.Success(View(row));
        if (workflowCaller && AZOA.WebAPI.Helpers.NftActorContinuationIdentity.TakenOver(row, command))
            return Refused("The owner has taken this NFT operation over; its workflow run no longer authorizes it.");
        Task<NftLifecycle?> Effect(NftLifecycle next) => SaveAsync(next, ct, requireActionAuthority: true, workflowAuthority: workflowCaller);
        if (!allowPreparation && row.State is not ("Submitted" or "NeedsOperator" or "Confirmed"))
            return AZOAResult<NftLifecycleView>.Success(View(row));
        if (row.State == "Confirmed")
        {
            await CommitAsync(row, command, ct);
            return AZOAResult<NftLifecycleView>.Success(View(await ReloadAsync(row.Id, ct)));
        }
        var provider = providers.GetProvider(row.ChainType, Enum.Parse<ChainNetwork>(row.Network));
        if (!provider.TryGetModule<INftTransactionModule>(out var module) || module is null) return Refused("NFT capability is unavailable.");
        if (row.State is "AwaitingRecipientAcceptance" or "AwaitingCreatorDestruction" or "AwaitingRefundAuthorization" or "AwaitingRefundDestinationAcceptance")
        {
            if (!accept) return AZOAResult<NftLifecycleView>.Success(View(row));
            var requiredParticipant = row.State == "AwaitingRefundDestinationAcceptance" ? command.Actor : command.Recipient;
            if (actor != requiredParticipant) return Refused("Only the required participant can authorize NFT acceptance.");
            var gate = await access.RequireValueAccessAsync(actor, actingTenantId == Guid.Empty ? null : actingTenantId, ct);
            if (gate.IsError) return Refused(gate.Message);
            row.Stage = row.State == "AwaitingCreatorDestruction" ? NftTransactionKinds.Burn
                : row.State == "AwaitingRefundAuthorization" ? NftTransactionKinds.Transfer : NftTransactionKinds.Accept;
            row.State = "Preparing";
            row.PreparedTransactionJson = null;
            if (!await CaptureStageConsentAsync(row, command, actor, actingTenantId, credential, module, acceptance, ct))
                return Refused("This NFT stage requires the participant's authenticated consent.");
            var accepted = await Effect(row);
            if (accepted is null) return Refused("NFT participant authority changed; refresh the existing lifecycle.");
            row = accepted;
        }
        if (allowPreparation && row.State == "Preparing")
        {
            if (AZOA.WebAPI.Helpers.NftStageConsentIdentity.Required(row, command)
                && AZOA.WebAPI.Helpers.NftStageConsentIdentity.Current(row) is null)
            {
                if (!accept || !await CaptureStageConsentAsync(row, command, actor, actingTenantId, credential, module, acceptance, ct))
                    return Refused("This NFT preparation generation requires fresh participant consent.");
                var accepted = await Effect(row);
                if (accepted is null) return Refused("NFT participant authority changed; refresh the existing lifecycle.");
                row = accepted;
            }
            if (row.ChainType == "Solana" && row.Stage == NftTransactionKinds.Create &&
                ((row.PreparationGeneration == 0 ? command.ProtocolCostQuote : AZOA.WebAPI.Helpers.NftReplacementCostContract.Current(row)?.Quote)
                    is not { } acceptedCost || acceptedCost.PreparationGeneration != row.PreparationGeneration))
                return Refused("This NFT preparation generation requires an explicitly accepted protocol cost quote.");
            var recipientSigns = AZOA.WebAPI.Helpers.NftStageConsentIdentity.RecipientSigns(row, command);
            if (row.Operation == "Refund" && row.Stage == NftTransactionKinds.Accept && (!accept || actor != command.Actor))
                return AZOAResult<NftLifecycleView>.Success(View(row));
            if (recipientSigns && (!accept || actor != command.Recipient))
                return AZOAResult<NftLifecycleView>.Success(View(row));
            // F-M11: a cost-bearing Solana stage stays parked, unsigned, until its paying participant accepts an exact quote.
            if (AZOA.WebAPI.Helpers.NftStageCostContract.Required(row) && !AZOA.WebAPI.Helpers.NftStageCostContract.Ready(row, command))
                return AZOAResult<NftLifecycleView>.Success(View(row));
            var participant = recipientSigns ? command.Recipient : command.Actor;
            var stageConsent = AZOA.WebAPI.Helpers.NftStageConsentIdentity.Current(row);
            var tenant = stageConsent is not null ? stageConsent.Authority.Credential.TenantId ?? Guid.Empty
                : recipientSigns ? actingTenantId : command.ActingTenantId;
            var readiness = await access.RequireValueAccessAsync(participant, tenant == Guid.Empty ? null : tenant, ct);
            if (readiness.IsError) return Refused(readiness.Message);
            if (participant != command.Actor)
            {
                var requesterReady = await access.RequireValueAccessAsync(command.Actor, command.ActingTenantId == Guid.Empty ? null : command.ActingTenantId, ct);
                if (requesterReady.IsError) return Refused(requesterReady.Message);
            }
            var action = row.Operation == "Mint" ? "nft:mint" : row.Operation == "Burn" ? "nft:burn"
                : row.Operation == "Revoke" ? "credential:revoke" : "nft:transfer";
            var policy = await governance.EnsureAllowedAsync(row.ChainType, "NFT", action, ct);
            if (policy.IsRefused) return Refused(policy.ConsumerMessage);
            if (row.Operation != "Mint")
            {
                var current = await store.GetAuthorityAsync(row.NftId, ct);
                if (current is null || current.Version != command.AuthorityVersion
                    || !await store.ReserveAuthorityAsync(current, row, ct))
                    return Refused("Another NFT operation owns this asset version; reconcile that operation first.");
            }
            var intent = AZOA.WebAPI.Helpers.NftLifecycleIntentFactory.Create(row, command, provider.ActiveNetwork,
                module.CredentialAuthorityAddress, actingTenantId);
            if (intent is null) return Refused("Credential authority is unavailable.");
            if (AZOA.WebAPI.Helpers.NftStageCostContract.Required(row) && await StageCostsChangedAsync(provider, intent, ct) is { } changed)
                return changed;
            var preparing = await Effect(row);
            if (preparing is null) return Refused("NFT preparation authority changed; refresh the existing lifecycle.");
            row = preparing;
            var prepared = await module.PrepareNftTransactionAsync(intent, ct);
            if (prepared.IsError || prepared.Result is null)
            {
                if (prepared.Code != "NFT_RECIPIENT_ACCEPTANCE_REQUIRED") return Refused(prepared.Message);
                row.State = row.Operation == "Refund" ? "AwaitingRefundDestinationAcceptance" : "AwaitingRecipientAcceptance";
                row.Detail = prepared.Message;
                row = await SaveAsync(row, ct) ?? await ReloadAsync(row.Id, ct);
                return AZOAResult<NftLifecycleView>.Success(View(row));
            }
            if (prepared.Result.Intent != intent || string.IsNullOrWhiteSpace(prepared.Result.TransactionId)
                || string.IsNullOrWhiteSpace(prepared.Result.SignedTransactionBase64)
                || (row.ProtocolIdentity is not null && row.ProtocolIdentity != prepared.Result.ProtocolIdentity)
                || (row.ContractAddress is not null && row.ContractAddress != prepared.Result.ContractAddress)
                || (row.TokenStandard is not null && row.TokenStandard != (prepared.Result.TokenStandard ?? module.NftTokenStandard)))
                return Refused("Prepared NFT transaction does not match the authorized asset and intent.");
            row.TokenStandard = prepared.Result.TokenStandard ?? module.NftTokenStandard;
            row.PreparedTransactionJson = JsonSerializer.Serialize(prepared.Result);
            row.State = "Prepared";
            row = await SaveAsync(row, ct) ?? await ReloadAsync(row.Id, ct);
        }
        if (row.State is "Prepared" or "Submitted" or "NeedsOperator")
        {
            var prepared = JsonSerializer.Deserialize<PreparedNftTransaction>(row.PreparedTransactionJson!)
                ?? throw new InvalidOperationException("Prepared NFT transaction is missing.");
            if (row.State == "Prepared")
            {
                if (!allowPreparation) return AZOAResult<NftLifecycleView>.Success(View(row));
                row.State = "Submitted";
                var won = await Effect(row);
                if (won is null) return AZOAResult<NftLifecycleView>.Success(View(await ReloadAsync(row.Id, ct)));
                row = won;
            }
            var observation = await module.ObserveNftTransactionAsync(prepared, ct);
            if (observation.IsError || observation.Result is null) return Refused(observation.Message);
            if (observation.Result.Status == "Unseen")
            {
                try { await module.SubmitNftTransactionAsync(prepared, ct); }
                catch (HttpRequestException) { return AZOAResult<NftLifecycleView>.Success(View(row)); }
                observation = await module.ObserveNftTransactionAsync(prepared, ct);
                if (observation.IsError || observation.Result is null) return Refused(observation.Message);
            }
            if ((observation.Result.Status is "Confirmed" or "ExpiredAbsent" or "FailedOnChain")
                && (observation.Result.TransactionId != prepared.TransactionId
                    || observation.Result.ProtocolIdentity != prepared.ProtocolIdentity || observation.Result.ContractAddress != prepared.ContractAddress))
                return Refused("NFT chain evidence does not match the persisted transaction identity.");
            if (observation.Result.Status == "NeedsOperator")
            {
                row.State = "NeedsOperator";
                row.Detail = "The original transaction expired without authoritative final evidence; reconcile its recorded identity.";
                row = await SaveAsync(row, ct) ?? await ReloadAsync(row.Id, ct);
                return AZOAResult<NftLifecycleView>.Success(View(row));
            }
            if (observation.Result.Status is "ExpiredAbsent" or "FailedOnChain")
            {
                ArchivePrepared(row, observation.Result.Status);
                row.PreparedTransactionJson = null;
                row.PreparationGeneration++;
                row.State = "Preparing";
                row.Detail = row.ConfirmedStageCount == 0
                    ? "Final chain evidence proves no effect from the recorded transaction; cancellation or a new preparation is available."
                    : "Final chain evidence proves no effect from this attempt; prior confirmed stages remain and require lifecycle completion.";
                row = await SaveAsync(row, ct) ?? await ReloadAsync(row.Id, ct);
                return AZOAResult<NftLifecycleView>.Success(View(row));
            }
            if (observation.Result.Status != "Confirmed") return AZOAResult<NftLifecycleView>.Success(View(row));
            if ((prepared.Intent.AssetId is not null && observation.Result.AssetId != prepared.Intent.AssetId)
                || string.IsNullOrWhiteSpace(observation.Result.AssetId))
                return Refused("NFT confirmation does not match the persisted transaction identity.");
            ArchivePrepared(row, "Confirmed");
            row.ConfirmedStageCount++;
            row.AssetId = observation.Result.AssetId;
            row.ProtocolIdentity = observation.Result.ProtocolIdentity;
            row.ContractAddress = observation.Result.ContractAddress;
            row.FinalTransactionId = observation.Result.TransactionId;
            if (row.Stage == NftTransactionKinds.Create)
            {
                row.CreationTransactionId = observation.Result.TransactionId;
                row.State = command.Metadata.IsSoulbound ? "AwaitingRecipientAcceptance" : "Confirmed";
            }
            else if (row.Stage == NftTransactionKinds.Accept)
            {
                row.Stage = row.Operation == "Mint" ? NftTransactionKinds.Deliver
                    : row.Operation == "Burn" ? NftTransactionKinds.ReturnForBurn
                    : row.Operation == "Revoke" ? NftTransactionKinds.Revoke : NftTransactionKinds.Transfer;
                row.State = row.Operation == "Refund" ? "AwaitingRefundAuthorization" : "Preparing";
                row.PreparedTransactionJson = null;
            }
            else if (row.Stage == NftTransactionKinds.ReturnForBurn)
                row.State = "AwaitingCreatorDestruction";
            else row.State = "Confirmed";
            row = await SaveAsync(row, ct) ?? await ReloadAsync(row.Id, ct);
        }
        if (row.State == "Confirmed") await CommitAsync(row, command, ct);
        return AZOAResult<NftLifecycleView>.Success(View(await ReloadAsync(row.Id, ct)));
    }

    private async Task CommitAsync(NftLifecycle row, NftLifecycleCommand command, CancellationToken ct)
    {
        if (row.AssetId is null || row.ProtocolIdentity is null || row.ContractAddress is null || row.CreationTransactionId is null || row.FinalTransactionId is null)
            throw new InvalidOperationException("Confirmed NFT operation has incomplete evidence.");
        var authority = await store.GetAuthorityAsync(row.NftId, ct);
        if (row.Operation != "Mint" && (authority is null || authority.Version != command.AuthorityVersion)) return;
        authority ??= new NftAssetAuthority
        {
            Id = NftLifecycleIdentity.Fingerprint(JsonSerializer.Serialize(new[] { row.ChainType, row.Network, row.ProtocolIdentity, row.ContractAddress, row.AssetId })),
            NftId = row.NftId, ChainType = row.ChainType, Network = row.Network, ProtocolIdentity = row.ProtocolIdentity,
            ContractAddress = row.ContractAddress, AssetId = row.AssetId, IssuanceLifecycleId = row.Id,
            TokenStandard = row.TokenStandard ?? throw new InvalidOperationException("Confirmed NFT standard is missing."),
            IssuerUserId = command.Actor != command.Recipient ? command.Actor.ToString("N") : string.Empty,
            CreatorUserId = command.Actor.ToString("N"),
            CreationTransactionId = row.CreationTransactionId, Soulbound = command.Metadata.IsSoulbound, Version = -1,
        };
        authority.HolderUserId = (row.Operation == "Refund" ? command.Actor : command.Recipient).ToString("N");
        authority.OwnerAddress = row.Operation == "Refund" ? command.ActorAddress : command.RecipientAddress;
        authority.LatestTransactionId = row.FinalTransactionId;
        authority.LatestLifecycleId = row.Id;
        authority.Burned = row.Operation == "Burn";
        authority.Revoked = row.Operation == "Revoke";
        authority.Version++;
        authority.ActiveLifecycleId = null;
        authority.UpdatedAt = clock.GetUtcNow();
        var issuance = await store.GetAsync(authority.IssuanceLifecycleId, ct)
            ?? throw new InvalidOperationException("NFT issuance journal disappeared.");
        var m = command.Metadata;
        var ownership = new NftOwnership
        {
            Id = row.NftId, UserId = SurrealLink.ToLink("user", authority.HolderUserId)!, ChainType = row.ChainType,
            ContractAddress = row.ContractAddress, TokenId = row.AssetId, TokenStandard = authority.TokenStandard,
            MetadataUri = m.MetadataURI, Name = m.Name, Description = m.Description, ImageUri = m.ImageURI,
            Attributes = JsonSerializer.SerializeToElement(m.Attributes), RoyaltyPercentage = m.RoyaltyPercentage,
            RoyaltyRecipient = m.RoyaltyRecipient, IsSoulbound = authority.Soulbound, IsTransferable = !authority.Soulbound && m.IsTransferable,
            IsCurrent = !authority.Burned, CurrentOwner = authority.OwnerAddress,
            IssuerUserId = string.IsNullOrEmpty(authority.IssuerUserId) ? null : SurrealLink.ToLink("user", authority.IssuerUserId),
            IsActive = !authority.Burned && !authority.Revoked, MintedDate = issuance.CreatedAt, LastTransferDate = authority.UpdatedAt,
        };
        await store.CommitAsync(row, authority, ownership, ct);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NftLifecycleView>> CancelAsync(Guid actor, string operationId, CancellationToken ct = default,
        Guid actingTenantId = default, AZOA.WebAPI.Models.Signing.SigningCredentialWitness? credential = null)
    {
        var row = await store.GetAsync(operationId, ct);
        if (row is null || !RequesterOwns(row, actor, actingTenantId)) return Refused("NFT operation not found.");
        if (row.State == "Cancelled") return AZOAResult<NftLifecycleView>.Success(View(row));
        if (credential is null || credential.ActorUserId != actor || (credential.TenantId ?? Guid.Empty) != actingTenantId)
            return Refused("Cancelling an admitted NFT operation requires the requester's current authority.");
        // RequesterOwns already pinned the caller: the admitted tenant, or the owner alone after a takeover.
        var consent = await signingAuthorities.BindConsentAsync(credential, [AZOA.WebAPI.Helpers.NftSigningScope.ForOperation(row.Operation)], ct);
        if (consent.IsError || consent.Result is null) return Refused(consent.Message);
        var authority = await store.GetAuthorityAsync(row.NftId, ct);
        if (!await store.CancelUnsentAsync(row, authority, consent.Result, ct))
            return Refused("This NFT operation has progressed beyond cancellation or its requester authority changed; reconcile it.");
        return AZOAResult<NftLifecycleView>.Success(View(await ReloadAsync(row.Id, ct)));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NftFeeQuote>> QuoteFeeAsync(Guid actor, string chainType, string operation, CancellationToken ct = default, Guid? nftId = null, string? sourceOperationId = null,
        Guid actingTenantId = default)
    {
        if (nftId.HasValue && sourceOperationId is not null) return AZOAResult<NftFeeQuote>.Failure("Choose one NFT fee target.");
        var network = providers.DefaultNetwork;
        if (nftId.HasValue)
        {
            var authority = await store.GetAuthorityAsync(nftId.Value.ToString("N"), ct);
            if (authority is null || (authority.HolderUserId != actor.ToString("N") && authority.IssuerUserId != actor.ToString("N")))
                return AZOAResult<NftFeeQuote>.Failure("NFT fee target not found.");
            chainType = authority.ChainType;
            network = Enum.Parse<ChainNetwork>(authority.Network);
        }
        if (sourceOperationId is not null)
        {
            var source = await store.GetAsync(sourceOperationId, ct);
            if (operation != "Refund" || source is null || source.Operation != "Transfer" || source.State != "Completed" || !RequesterOwns(source, actor, actingTenantId))
                return AZOAResult<NftFeeQuote>.Failure("Original NFT transfer not found.");
            chainType = source.ChainType;
            network = Enum.Parse<ChainNetwork>(source.Network);
        }
        var provider = providers.GetProvider(chainType, network);
        if (!provider.TryGetModule<INftTransactionModule>(out var module) || module is null)
            return AZOAResult<NftFeeQuote>.Failure("NFT capability is unavailable.");
        return NftNativeFeePolicy.Quote(configuration, module, provider.ChainType, provider.ActiveNetwork, operation, actor);
    }

    private static AZOAResult<NftLifecycleView> FeeAuthorizationRequired()
        => AZOAResult<NftLifecycleView>.FailureWithCode("Read the NFT fee quote and authorize its revision and maximum native fee before submission.", "NFT_FEE_AUTHORIZATION_REQUIRED");

    /// <summary>RV-L4-02: only a custodial wallet or a signature-proven External wallet names the recipient; ambiguity refuses.</summary>
    private async Task<AZOAResult<IWallet>> ResolveRecipientWalletAsync(string chain, string address, CancellationToken ct)
    {
        var candidates = new List<IWallet>();
        foreach (var id in await store.FindCustodialWalletIdsAsync(chain, address, ct))
            if (await wallets.GetByIdAsync(id, ct) is { IsError: false, Result: { WalletType: WalletType.Platform } custodial }
                && custodial.ChainType.Equals(chain, StringComparison.OrdinalIgnoreCase)
                && ChainAddressIdentity.Equivalent(chain, custodial.Address, address)) candidates.Add(custodial);
        if (ownershipProofs is not null && await ownershipProofs.GetAsync(chain, address, ct) is { } proof
            && await wallets.GetByIdAsync(proof.WalletId, ct) is { IsError: false, Result: { } proven }
            && proven.UserId == proof.UserId && proven.ChainType.Equals(chain, StringComparison.OrdinalIgnoreCase)
            && ChainAddressIdentity.Equivalent(chain, proven.Address, address)) candidates.Add(proven);
        var owners = candidates.Select(wallet => wallet.UserId).Distinct().ToArray();
        if (owners.Length > 1)
            return AZOAResult<IWallet>.FailureWithCode("The recipient address is claimed by more than one account; it cannot receive NFT ownership until resolved.",
                "NFT_RECIPIENT_AMBIGUOUS");
        return owners.Length == 0
            ? AZOAResult<IWallet>.FailureWithCode("The recipient must hold a custodial or signature-proven wallet for this address before NFT ownership can transfer.",
                AzoaErrorCodes.OperationNotAllowed)
            : AZOAResult<IWallet>.Success(candidates.OrderByDescending(wallet => wallet.WalletType == WalletType.Platform).ThenBy(wallet => wallet.Id).First());
    }

    private async Task<IWallet?> WalletAsync(Guid user, string chain, CancellationToken ct, Guid? walletId = null)
        => (await wallets.GetByUserAsync(user, ct)).Result?.FirstOrDefault(wallet => wallet.ChainType.Equals(chain, StringComparison.OrdinalIgnoreCase) && (!walletId.HasValue || wallet.Id == walletId.Value));
    private void ArchivePrepared(NftLifecycle row, string verdict)
    {
        if (row.PreparedTransactionJson is null) return;
        var history = JsonSerializer.Deserialize<List<NftTransactionAttempt>>(row.TransactionHistoryJson)
            ?? throw new InvalidOperationException("NFT transaction history is unreadable.");
        var prepared = JsonSerializer.Deserialize<PreparedNftTransaction>(row.PreparedTransactionJson)
            ?? throw new InvalidOperationException("NFT prepared transaction is unreadable.");
        if (history.All(item => item.Transaction.TransactionId != prepared.TransactionId)) history.Add(new(prepared, verdict, clock.GetUtcNow()));
        row.TransactionHistoryJson = JsonSerializer.Serialize(history);
    }
    private async Task<NftLifecycle?> ClaimAsync(NftLifecycle row, CancellationToken ct)
    {
        try { return await store.ClaimAsync(row, ct); }
        catch (ActionEffectAuthorityChangedException) { return null; }
    }

    private Task<NftLifecycle?> SaveAsync(NftLifecycle row, CancellationToken ct, bool requireActionAuthority = false, bool workflowAuthority = false)
    {
        row.UpdatedAt = clock.GetUtcNow();
        return store.UpdateAsync(row, row.Version, ct, requireActionAuthority, workflowAuthority);
    }
    private async Task<NftLifecycle> ReloadAsync(string id, CancellationToken ct)
        => await store.GetAsync(id, ct) ?? throw new InvalidOperationException("NFT operation disappeared.");
    private NftLifecycle Candidate(Guid actor, Guid recipient, Guid nftId, string operation, string chain, ChainNetwork network, string key, NftLifecycleCommand command)
    {
        var json = JsonSerializer.Serialize(command);
        return new NftLifecycle
        {
            Id = NftLifecycleIdentity.ForRequest(actor, operation, key, command.ActingTenantId), ActorUserId = actor.ToString("N"), RecipientUserId = recipient.ToString("N"),
            NftId = nftId.ToString("N"), RequestHash = NftLifecycleIdentity.Fingerprint(json), RequestJson = json, Operation = operation, ChainType = chain,
            Network = network.ToString(), CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow(),
        };
    }
    private static bool ValidRequestKey(string key) => !string.IsNullOrWhiteSpace(key) && key.Length <= 200;
    private static AZOAResult<NftLifecycleView> Refused(string reason) => AZOAResult<NftLifecycleView>.FailureWithCode(reason, AzoaErrorCodes.OperationNotAllowed);
    private NftLifecycleView View(NftLifecycle row)
    {
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson)!;
        var lapsed = AZOA.WebAPI.Helpers.NftStageConsentIdentity.LapsedConsent(row, command, clock.GetUtcNow());
        return new(row.Id, Guid.Parse(row.NftId), row.State, row.Stage,
            row.AssetId, row.FinalTransactionId, row.Detail, row.Operation, command.FeeQuote, Guid.Parse(row.ActorUserId), Guid.Parse(row.RecipientUserId),
            row.Version, row.PreparationGeneration, AZOA.WebAPI.Helpers.NftStageConsentIdentity.Current(row)?.Request,
            AZOA.WebAPI.Helpers.NftStageConsentIdentity.NeedsReplacementConsent(row, command),
            AZOA.WebAPI.Helpers.NftReplacementCostContract.Eligible(row) && AZOA.WebAPI.Helpers.NftReplacementCostContract.Current(row) is null,
            AZOA.WebAPI.Helpers.NftReplacementCostContract.Current(row) is not null,
            AZOA.WebAPI.Helpers.NftActorContinuationIdentity.NeedsRenewal(row, command, clock.GetUtcNow()),
            lapsed is not null, lapsed?.Authority.Credential.ActorUserId,
            AZOA.WebAPI.Helpers.NftStageCostContract.Eligible(row, command) && !AZOA.WebAPI.Helpers.NftStageCostContract.Ready(row, command),
            AZOA.WebAPI.Helpers.NftStageCostContract.Required(row) ? AZOA.WebAPI.Helpers.NftStageCostContract.Payer(row, command).User : (Guid?)null,
            AZOA.WebAPI.Helpers.NftStageCostContract.Required(row) ? AZOA.WebAPI.Helpers.NftStageCostContract.Current(row)?.Quote.Limits : null);
    }
}
