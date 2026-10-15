// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Models.Requests;

/// <summary>
/// Which account creates a fungible asset and therefore holds all four of its ASA
/// authorities (manager, reserve, freeze, clawback).
/// <para>
/// This is deliberately NOT a field on any caller-facing request. The destination is
/// node policy (<c>Reserve:FungibleMintTarget</c>) because a sponsor or project owner
/// should never be asked to choose an on-chain custody arrangement — and because the
/// wrong choice is unbroadcastable: an ASA-create is signed with the PLATFORM key, so a
/// create whose sender is a caller's custodial wallet has a sender the signature does
/// not match.
/// </para>
/// </summary>
public enum FungibleMintTarget
{
    /// <summary>
    /// The platform reserve creates and holds the asset, and a reserve ledger row is
    /// opened so named partitions can police every later draw. The default.
    /// </summary>
    PlatformReserve = 0,

    /// <summary>
    /// Legacy: the target user's custodial wallet is named as creator. Retained only so
    /// an operator can reproduce pre-reserve behaviour on a simulated chain; it cannot
    /// broadcast on Algorand (sender/signature mismatch) and opens no reserve ledger row.
    /// </summary>
    CallerWallet = 1,
}

/// <summary>
/// Body for the one-shot <c>POST /api/nft/fungible-mint</c> endpoint
/// (fungible-mint-and-render-model, §11.3). The direct (no-DAG) parallel to the
/// <c>FungibleTokenCreate</c> action node: launch a fungible token (an Algorand ASA
/// with a real <see cref="Total"/> supply + <see cref="Decimals"/>), gated by the SAME
/// KYC choke point and deduped by the SAME idempotency discipline the action node uses.
///
/// The asset is created into the node's configured <see cref="FungibleMintTarget"/> —
/// by default the platform reserve, which then holds it and accounts for it under named
/// partitions. The caller's custodial wallet is still provisioned (it is where units are
/// later issued TO), but it is no longer the asset's creator or authority.
///
/// The field shape mirrors <see cref="AZOA.WebAPI.Models.Actions.FungibleTokenCreateNodeConfig"/>
/// for consistency between the in-DAG and direct launch paths — total supply +
/// decimals are tenant-supplied and authoritative; AZOA derives no economic
/// meaning (peg/valuation stays tenant-side).
///
/// IDOR note: like <see cref="FungibleTokenCreateRequest"/> and
/// <see cref="AllocationRequest"/>, this DTO carries no owner / target user id.
/// The target user is resolved from the authenticated principal; any owner id a
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
