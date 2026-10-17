using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;

namespace AZOA.WebAPI.Helpers;

/// <summary>Derives the held purchase's single funds leg from its immutable source.</summary>
public static class ExchangeHoldWirePlan
{
    public static ExchangeWireIntent? Create(string sourceId, long generation, ExchangeHoldSourceIntent source,
        ExchangeParticipantAuthority authority)
    {
        if (source.Version != 1 || sourceId != ExchangeHoldSourceIdentity.ForIntent(source) || generation < 0
            || !UnsignedIntegerText.TryParse(source.MaximumNetworkFeeBaseUnits, out var maximumFee) || maximumFee <= 0
            || source.Opening.Definition is not { FundingSource: "Reserve" }
            || source.ParticipantWallet != authority.Wallet || source.ParticipantUserId != authority.ParticipantUserId
            || source.TenantUserId != authority.Tenant.Credential.ActorUserId) return null;
        var asset = source.Opening.Asset;
        var participant = SigningContext.ForUser(source.ParticipantUserId, source.ParticipantWallet.Id)
            .ForChain(asset.ChainType).ActingAs(source.TenantUserId, AzoaScopes.SwapSign, source.ParticipantUserId);
        var reserve = SigningContext.PlatformFor(asset.ChainType)
            .ActingAs(source.TenantUserId, AzoaScopes.SwapSign, source.ParticipantUserId);
        return new(1, sourceId, generation, asset.ChainType, asset.Network, asset.ProtocolIdentity,
            [new(source.PlatformAssetId, source.PlatformQuantityBaseUnits, source.ParticipantWallet.Address,
                source.TreasuryAddress, "Participant", participant)], asset.HoldingAddress, reserve,
            source.MaximumNetworkFeeBaseUnits);
    }
}
