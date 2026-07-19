// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Models.Responses;

/// <summary>Strict public projection of a fungible-token launch result.</summary>
public sealed class FungibleTokenResultResponse
{
    public Guid AvatarId { get; init; }
    public Guid WalletId { get; init; }
    public string WalletAddress { get; init; } = string.Empty;
    public bool WalletProvisioned { get; init; }
    public string AssetId { get; init; } = string.Empty;
    public bool Replayed { get; init; }

    /// <summary>Creates the allowlisted public launch result.</summary>
    public static FungibleTokenResultResponse From(FungibleTokenResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new FungibleTokenResultResponse
        {
            AvatarId = result.AvatarId,
            WalletId = result.WalletId,
            WalletAddress = result.WalletAddress,
            WalletProvisioned = result.WalletProvisioned,
            AssetId = result.AssetId,
            Replayed = result.Replayed,
        };
    }
}
