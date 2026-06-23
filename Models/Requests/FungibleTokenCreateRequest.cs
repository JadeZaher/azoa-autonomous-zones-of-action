// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Models.Requests;

/// <summary>
/// Tenant-callable request to launch a fungible token (an Algorand ASA with a real
/// <see cref="Total"/> supply + <see cref="Decimals"/>, as opposed to the supply-1
/// NFT path). The supply and decimals are tenant-supplied and authoritative — AZOA
/// derives no economic meaning (peg/valuation stays tenant-side). The token is
/// minted into the target avatar's provisioned custodial wallet, whose address is
/// used as the ASA manager/reserve/freeze/clawback address (mechanism only;
/// configurable roles are a follow-up).
///
/// IDOR note: like <see cref="AllocationRequest"/>, this DTO carries no owner /
/// target avatar id. The target avatar is supplied out-of-band (route / run
/// context) and resolved from the authenticated principal; any owner id a caller
/// might bolt on here would be ignored.
/// </summary>
public class FungibleTokenCreateRequest
{
    /// <summary>Human-readable asset name (ASA name).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Short unit name / ticker (ASA unit name).</summary>
    public string UnitName { get; set; } = string.Empty;

    /// <summary>
    /// Target chain for provision-if-absent + the token launch (e.g. "Algorand").
    /// Matched case-insensitively against the avatar's wallets to decide reuse vs.
    /// generate. Algorand-only in v1 (the ASA module is Algorand-specific).
    /// </summary>
    public string ChainType { get; set; } = "Algorand";

    /// <summary>
    /// Total supply in base units. Tenant-supplied and authoritative; AZOA treats
    /// it as opaque. Must be &gt; 0.
    /// </summary>
    public ulong Total { get; set; }

    /// <summary>Number of decimal places (0..19). Tenant-supplied and authoritative.</summary>
    public int Decimals { get; set; }
}
