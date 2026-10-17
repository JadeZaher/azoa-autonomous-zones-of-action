using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;

namespace AZOA.WebAPI.Services.Hold;

public sealed partial class HoldCommandService
{
    /// <summary>Admits the exact direct command and quote without acquiring custody signatures.</summary>
    public async Task<AZOAResult<HoldCommandView>> AdmitAsync(HoldCommandIntent intent, Guid? senderWalletId,
        Guid? recipientWalletId, SigningCredentialWitness credential, CancellationToken ct = default)
    {
        if (intent.ActorUserId != credential.ActorUserId || intent.TenantId != credential.TenantId
            || intent.Asset is null || string.IsNullOrWhiteSpace(intent.RequestKey) || intent.RequestKey.Length > 200) return Refuse();
        var id = HoldCommandIdentity.Operation(intent);
        var prior = await store.GetCommandAsync(id, ct);
        if (prior is not null)
        {
            if (prior.IntentHash != CanonicalJson.Hash(intent) || prior.ActorUserId != credential.ActorUserId.ToString("N")
                || prior.TenantId != credential.TenantId?.ToString("N")) return Refuse();
            var original = prior.RailPlanJson is null ? null : JsonSerializer.Deserialize<HoldRailPlan>(prior.RailPlanJson);
            if (original?.SenderWallet?.Id != senderWalletId || original?.RecipientWallet?.Id != recipientWalletId) return Refuse();
            return await ReadResultAsync(prior, ct);
        }
        var access = await valueAccess.RequireValueAccessAsync(credential.ActorUserId, credential.TenantId, ct);
        if (access.IsError) return AZOAResult<HoldCommandView>.Failure(access.Message);
        var consent = await authorities.BindConsentAsync(credential, [AzoaScopes.TransferSign], ct);
        if (consent.IsError || consent.Result is null) return AZOAResult<HoldCommandView>.Failure(consent.Message);
        var current = await store.GetHoldAsync(HoldCommandIdentity.Hold(intent), ct);
        var local = intent.Kind == "ReleaseIntoHold" || (intent.Kind == "Open" && intent.Definition?.FundingSource == "Reserve")
            || (intent.Kind == "Refund" && current?.FundingSource == "Reserve");
        if (local)
        {
            if (senderWalletId is not null || recipientWalletId is not null) return Refuse();
            var receipt = await store.CommitLocalAsync(intent, new(1, Direct: consent.Result), ct);
            return receipt is null ? AZOAResult<HoldCommandView>.Failure("The original custody liability or current local command authority changed.")
                : AZOAResult<HoldCommandView>.Success(JsonSerializer.Deserialize<HoldCommandView>(receipt.OutcomeJson)!);
        }
        var kind = intent.Kind switch { "Open" => "Funding", "Release" => "Release", "Refund" => "FundedRefund", _ => null };
        if (kind is null) return AZOAResult<HoldCommandView>.Failure("Unknown hold command.");
        var quantity = kind == "Funding" ? intent.Definition?.QuantityBaseUnits : current?.Amount;
        if (quantity is null) return Refuse();
        SigningWalletWitness? sender = null, recipient = null;
        SigningAdmissionAuthority? senderAuthority = null;
        SigningContext signing;
        if (kind == "Funding")
        {
            if (senderWalletId is null || recipientWalletId is not null) return Refuse();
            var loaded = await wallets.GetByIdAsync(senderWalletId.Value, ct);
            if (loaded.IsError || loaded.Result is not { } wallet || wallet.UserId != credential.ActorUserId
                || wallet.ChainType != intent.Asset.ChainType) return Refuse();
            var bound = await authorities.BindDirectAsync(credential, wallet.Id, wallet.ChainType, wallet.Address,
                [AzoaScopes.TransferSign], ct);
            if (bound.IsError || bound.Result is null) return AZOAResult<HoldCommandView>.Failure(bound.Message);
            senderAuthority = bound.Result;
            sender = bound.Result.Wallet;
            signing = new(wallet.UserId, wallet.Id, false, credential.TenantId ?? Guid.Empty,
                credential.TenantId is null ? Guid.Empty : wallet.UserId, AzoaScopes.TransferSign, wallet.ChainType);
        }
        else
        {
            if (recipientWalletId is null || senderWalletId is not null || current?.DefinitionJson is null) return Refuse();
            var definition = JsonSerializer.Deserialize<HoldDefinition>(current.DefinitionJson)!;
            var recipientId = kind == "Release" ? definition.BeneficiaryUserId : definition.FunderUserId;
            var loaded = await wallets.GetByIdAsync(recipientWalletId.Value, ct);
            if (loaded.IsError || loaded.Result is not { } wallet || wallet.UserId != recipientId
                || wallet.ChainType != intent.Asset.ChainType) return Refuse();
            recipient = new(wallet.Id, wallet.UserId, wallet.Address, wallet.ChainType);
            signing = new(Guid.Empty, Guid.Empty, true, credential.TenantId ?? Guid.Empty,
                credential.TenantId is null ? Guid.Empty : credential.ActorUserId, AzoaScopes.TransferSign, intent.Asset.ChainType);
        }
        var provider = providers.GetProvider(intent.Asset.ChainType, intent.Asset.Network);
        if (!provider.TryGetModule<IValueCustodyModule>(out var custody)
            || !provider.TryGetModule<IValueTransferTransactionModule>(out _))
            return AZOAResult<HoldCommandView>.Failure("The selected network does not expose the complete custody lifecycle.");
        var quote = await custody!.QuoteValueCustodyAsync(new(intent.Asset.ChainType, intent.Asset.Network, intent.Asset.AssetId,
            sender?.Address ?? intent.Asset.HoldingAddress, recipient?.Address ?? intent.Asset.HoldingAddress,
            quantity, intent.Asset.HoldingAddress), ct);
        if (quote.IsError || quote.Result is null) return AZOAResult<HoldCommandView>.Failure(quote.Message);
        var plan = new HoldRailPlan(1, kind, id, 0, quote.Result, sender, recipient, signing);
        var admitted = await store.ClaimRailAsync(intent, plan, new(1, Direct: consent.Result, Sender: senderAuthority), ct);
        return admitted is null ? AZOAResult<HoldCommandView>.Failure("The current custody liability, immutable quote or command authority changed.")
            : Result(admitted);
    }
}
