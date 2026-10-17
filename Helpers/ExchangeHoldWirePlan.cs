using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;

namespace AZOA.WebAPI.Helpers;

/// <summary>Derives the exchange's exact ordered transfer legs from its immutable source.</summary>
public static class ExchangeHoldWirePlan
{
    public static ExchangeWireIntent? Create(string sourceId, long generation, ExchangeHoldSourceIntent source,
        ExchangeParticipantAuthority authority)
    {
        var asset = source.Opening?.Asset ?? source.Units?.Asset;
        if (source.Version != 1 || asset is null || sourceId != ExchangeHoldSourceIdentity.ForIntent(source) || generation < 0
            || !UnsignedIntegerText.TryParse(source.MaximumNetworkFeeBaseUnits, out var maximumFee) || maximumFee <= 0
            || (source.Opening is not null && source.Opening.Definition is not { FundingSource: "Reserve" })
            || source.ParticipantWallet != authority.Wallet || source.ParticipantUserId != authority.ParticipantUserId
            || source.TenantUserId != authority.Tenant.Credential.ActorUserId) return null;
        var participant = SigningContext.ForUser(source.ParticipantUserId, source.ParticipantWallet.Id)
            .ForChain(asset.ChainType).ActingAs(source.TenantUserId, AzoaScopes.SwapSign, source.ParticipantUserId);
        var platform = SigningContext.PlatformFor(asset.ChainType)
            .ActingAs(source.TenantUserId, AzoaScopes.SwapSign, source.ParticipantUserId);
        var wallet = source.ParticipantWallet.Address;
        IReadOnlyList<ExchangeWireLeg> legs;
        ExchangeWireTreasury? treasury = null;
        if (source.Units is null)
            legs = [new(source.PlatformAssetId, source.PlatformQuantityBaseUnits, wallet, source.TreasuryAddress, "Participant", participant)];
        else if (source.Units.Kind == "Issue")
            legs = [new(source.PlatformAssetId, source.PlatformQuantityBaseUnits, wallet, source.TreasuryAddress, "Participant", participant),
                new(asset.AssetId, source.Units.QuantityBaseUnits, asset.HoldingAddress, wallet, "Reserve", platform)];
        else if (source.Units.Kind == "Return")
        {
            treasury = new(source.TenantUserId, source.ProjectReference);
            legs = [new(source.PlatformAssetId, source.PlatformQuantityBaseUnits, source.TreasuryAddress, wallet, "Treasury", platform),
                new(asset.AssetId, source.Units.QuantityBaseUnits, wallet, asset.HoldingAddress, "Participant", participant)];
        }
        else return null;
        ExchangeWireReadiness? readiness = null;
        if (source.Units?.RecipientFundingBaseUnits is { } funding)
        {
            if (!UnsignedIntegerText.TryParse(funding, out _)) return null;
            readiness = new(source.Units.Kind == "Issue" ? asset.AssetId : source.PlatformAssetId, wallet, funding, participant);
        }
        return new(1, sourceId, generation, asset.ChainType, asset.Network, asset.ProtocolIdentity, legs,
            asset.HoldingAddress, platform, source.MaximumNetworkFeeBaseUnits, treasury, readiness);
    }
}
