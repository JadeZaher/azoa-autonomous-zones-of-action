using System.Text.Json;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Users;

public sealed partial class NftLifecycleService
{
    /// <inheritdoc/>
    public async Task<AZOAResult<NftProtocolCostQuote>> QuoteMintCostsAsync(Guid actor, Guid recipient,
        UserNFTMintModel request, string requestKey, CancellationToken ct = default, Guid actingTenantId = default)
    {
        if (request is null || actor == Guid.Empty || recipient == Guid.Empty || !ValidRequestKey(requestKey)
            || string.IsNullOrWhiteSpace(request.ChainType) || string.IsNullOrWhiteSpace(request.MetadataURI)
            || !string.IsNullOrWhiteSpace(request.TokenId) || (actor != recipient && !request.IsSoulbound))
            return CostRefused("NFT costs require an exact mint draft, authenticated participants and Idempotency-Key.");
        var lifecycleId = NftLifecycleIdentity.ForRequest(actor, "Mint", requestKey, actingTenantId);
        var draftHash = NftProtocolCostContract.DraftHash(lifecycleId, actor, recipient, request);
        var existing = await store.GetAsync(lifecycleId, ct);
        if (existing is not null)
        {
            var original = JsonSerializer.Deserialize<NftLifecycleCommand>(existing.RequestJson)
                ?? throw new InvalidOperationException("NFT intent is unreadable.");
            return existing.Stage != "Cancellation" && original.Actor == actor && original.Recipient == recipient
                && original.ActingTenantId == actingTenantId && original.ProtocolCostQuote is { } saved
                && saved.DraftHash == draftHash
                ? AZOAResult<NftProtocolCostQuote>.Success(saved)
                : CostRefused("This request key already belongs to an admitted or cancelled NFT operation.");
        }
        var gate = await access.RequireValueAccessAsync(actor, actingTenantId == Guid.Empty ? null : actingTenantId, ct);
        if (gate.IsError) return CostRefused(gate.Message);
        var policy = await governance.EnsureAllowedAsync(request.ChainType, "NFT", "nft:mint");
        if (policy.IsRefused) return CostRefused(policy.ConsumerMessage);
        var provider = providers.GetProvider(request.ChainType, providers.DefaultNetwork);
        if (!provider.TryGetModule<INftTransactionModule>(out var module) || module is null
            || !provider.TryGetModule<INftProtocolCostModule>(out var costs) || costs is null)
            return CostRefused("This chain does not offer verified NFT protocol cost quotes.");
        var fee = NftNativeFeePolicy.Quote(configuration, module, provider.ChainType, provider.ActiveNetwork, "Mint", actor);
        if (fee.IsError || fee.Result is null) return CostRefused(fee.Message);
        if (!NftNativeFeePolicy.IsAuthorized(fee.Result, request.FeeAuthorization))
            return CostRefused("Accept the current platform fee quote before requesting exact protocol costs.");
        var actorWallet = await WalletAsync(actor, provider.ChainType, ct, request.WalletId);
        var recipientWallet = recipient == actor ? actorWallet : await WalletAsync(recipient, provider.ChainType, ct);
        if (actorWallet is null || recipientWallet is null) return CostRefused("Both NFT participants need a wallet on this chain.");
        var command = new NftLifecycleCommand(actor, recipient, actorWallet.Id, recipientWallet.Id,
            actorWallet.Address, recipientWallet.Address, request, -1, actingTenantId,
            FeeQuote: fee.Result, FeeAuthorization: request.FeeAuthorization,
            PlatformAuthorityAddress: request.IsSoulbound ? module.CredentialAuthorityAddress : null);
        var candidate = Candidate(actor, recipient, Guid.Empty, "Mint", provider.ChainType, provider.ActiveNetwork, requestKey, command);
        var intent = NftLifecycleIntentFactory.Create(candidate, command, provider.ActiveNetwork,
            module.CredentialAuthorityAddress, actingTenantId);
        if (intent is null) return CostRefused("Credential authority is unavailable.");
        var estimate = await costs.QuoteNftProtocolCostsAsync(intent, ct);
        if (estimate.IsError || estimate.Result is null) return CostRefused(estimate.Message);
        var now = clock.GetUtcNow();
        var quote = new NftProtocolCostQuote(Guid.NewGuid().ToString("N"), actor, actingTenantId,
            actorWallet.Id, actorWallet.Address, provider.ChainType, provider.ActiveNetwork,
            estimate.Result.PhysicalNetworkIdentity, lifecycleId, draftHash, candidate.Stage,
            candidate.PreparationGeneration, estimate.Result.TokenStandard, estimate.Result.PolicyEvidenceJson,
            estimate.Result.Limits, now, now.AddMinutes(5), NftProtocolCostContract.IntentHash(intent, command.RecipientWalletId));
        if (!NftProtocolCostContract.IsValid(quote))
            throw new InvalidOperationException("NFT provider returned invalid protocol cost evidence.");
        var receipt = await protocolCosts.ClaimAsync(quote, ct);
        return receipt is null ? CostRefused("The cost receipt could not be recorded.")
            : AZOAResult<NftProtocolCostQuote>.Success(receipt);
    }

    private static AZOAResult<NftProtocolCostQuote> CostRefused(string reason)
        => AZOAResult<NftProtocolCostQuote>.FailureWithCode(reason, AzoaErrorCodes.OperationNotAllowed);
}
