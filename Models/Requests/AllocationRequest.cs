// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Models.Requests;

/// <summary>
/// The on-chain shape of an allocation. The discriminator (D4) dispatches the
/// allocation to the existing mint/transfer surface
/// (<c>INftManager.MintAsync</c> / <c>TransferAsync</c>) without the caller
/// needing to know which controller-level primitive runs.
/// </summary>
public enum AllocationKind
{
    /// <summary>Mint a new asset into the avatar's custodial wallet.</summary>
    Mint = 0,

    /// <summary>Transfer an existing asset into the avatar's custodial wallet.</summary>
    Transfer = 1
}

/// <summary>
/// Single tenant-callable allocation request (D4 — one DTO with an asset-kind
/// discriminator). It carries the asset descriptor + an already-decided amount;
/// AZOA does NOT compute amounts, run token economics, or evaluate any gate —
/// those stay in the fiat-settlement tenant. AZOA materialises the wallet (if
/// absent) and moves the on-chain/custodial asset.
///
/// IDOR note: this DTO intentionally does NOT carry an owner / target avatar id.
/// The allocation target avatar is supplied out-of-band (route / contract
/// parameter) and resolved from the authenticated principal; any owner id a
/// caller might bolt on here would be ignored (STARODK precedent). The amount /
/// asset descriptor are the only economically meaningful fields.
/// </summary>
public class AllocationRequest
{
    /// <summary>
    /// Which on-chain primitive to run (D4). <see cref="AllocationKind.Mint"/>
    /// creates a new asset; <see cref="AllocationKind.Transfer"/> moves an
    /// existing one.
    /// </summary>
    public AllocationKind Kind { get; set; } = AllocationKind.Mint;

    /// <summary>
    /// Target chain for provision-if-absent + the allocation (e.g. "Algorand",
    /// "Solana"). Matched case-insensitively against the avatar's wallets to
    /// decide reuse vs. generate.
    /// </summary>
    public string ChainType { get; set; } = string.Empty;

    /// <summary>
    /// The already-decided amount of the asset to allocate, as a string for
    /// arbitrary precision (house convention). AZOA treats this as opaque and
    /// authoritative — it does not derive or validate economic meaning.
    /// </summary>
    public string Amount { get; set; } = string.Empty;

    /// <summary>Human-readable asset name (mint path).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional asset description (mint path).</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Chain-native asset / token identifier. Required on the
    /// <see cref="AllocationKind.Transfer"/> path (identifies the asset to move);
    /// optional on mint.
    /// </summary>
    public string? AssetId { get; set; }

    /// <summary>
    /// For <see cref="AllocationKind.Transfer"/>: the existing AZOA asset (NFT)
    /// id to transfer to the target avatar's custodial wallet.
    /// </summary>
    public Guid? AssetRecordId { get; set; }

    /// <summary>Optional extra asset metadata (mint path).</summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>Optional free-text memo carried onto the transfer.</summary>
    public string? Memo { get; set; }
}
