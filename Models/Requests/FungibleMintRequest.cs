// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Models.Requests;

/// <summary>
/// Body for the one-shot <c>POST /api/nft/fungible-mint</c> endpoint
/// (fungible-mint-and-render-model, §11.3). The direct (no-DAG) parallel to the
/// <c>FungibleTokenCreate</c> quest node: launch a fungible token (an Algorand ASA
/// with a real <see cref="Total"/> supply + <see cref="Decimals"/>) into the
/// caller's provisioned custodial wallet, gated by the SAME KYC choke point and
/// deduped by the SAME idempotency discipline the quest node uses.
///
/// The field shape mirrors <see cref="AZOA.WebAPI.Models.Quest.FungibleTokenCreateNodeConfig"/>
/// for consistency between the in-DAG and direct launch paths — total supply +
/// decimals are tenant-supplied and authoritative; AZOA derives no economic
/// meaning (peg/valuation stays tenant-side).
///
/// IDOR note: like <see cref="FungibleTokenCreateRequest"/> and
/// <see cref="AllocationRequest"/>, this DTO carries no owner / target avatar id.
/// The target avatar is resolved from the authenticated principal; any owner id a
/// caller might bolt on would be ignored.
/// </summary>
public class FungibleMintRequest
{
    /// <summary>
    /// Target chain for provision-if-absent + the token launch (e.g. "Algorand").
    /// Algorand-only in v1 (the ASA module is Algorand-specific).
    /// </summary>
    public string ChainType { get; set; } = "Algorand";

    /// <summary>Human-readable asset name (ASA name).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Short unit name / ticker (ASA unit name).</summary>
    public string UnitName { get; set; } = string.Empty;

    /// <summary>
    /// Total supply in base units. Tenant-supplied and authoritative; AZOA treats
    /// it as opaque. Must be &gt; 0.
    /// </summary>
    public ulong Total { get; set; }

    /// <summary>Number of decimal places (0..19). Tenant-supplied and authoritative.</summary>
    public int Decimals { get; set; }

    /// <summary>
    /// Optional holon to link the created asset to (mirrors
    /// <c>FungibleTokenCreateNodeConfig.HolonId</c>). When supplied, the holon's
    /// <c>token_id</c>/<c>chain_id</c> are populated from the created asset id on a
    /// successful launch (D10 Holon↔asset link). Null ⇒ no link.
    /// </summary>
    public Guid? HolonId { get; set; }
}
