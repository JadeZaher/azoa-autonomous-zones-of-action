// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Models.Requests;

/// <summary>
/// The shape of an allocation. The discriminator (D4) dispatches the allocation to the
/// right primitive without the caller needing to know which one runs.
/// </summary>
public enum AllocationKind
{
    /// <summary>
    /// RETIRED for tenants (D47). Creating supply per allocation made the balance a
    /// ledger of record and let two callers create units for the same purpose. Supply is
    /// created ONCE into the platform reserve; every later allocation is
    /// <see cref="IssueUnits"/>. The member stays so historical receipts still
    /// deserialise, and the manager refuses it for any tenant-driven caller.
    /// </summary>
    Mint = 0,

    /// <summary>Transfer an existing asset into the user's custodial wallet.</summary>
    Transfer = 1,

    /// <summary>
    /// Issue units to the user from the platform reserve (D47). The units already exist;
    /// this draws them from a named reserve partition, prepares the recipient's account if
    /// it has never held this asset, and moves the net amount.
    /// </summary>
    IssueUnits = 2
}

/// <summary>
/// Single tenant-callable allocation request (D4 — one DTO with an asset-kind
/// discriminator). It carries the asset descriptor + an already-decided amount;
/// the fiat-settlement tenant remains authoritative for that gross amount and
/// its economics. AZOA validates it and applies the node's server-authoritative
/// fee schedule before materialising the wallet and moving the net asset.
///
/// IDOR note: this DTO intentionally does NOT carry an owner / target user id.
/// The allocation target user is supplied out-of-band (route / contract
/// parameter) and resolved from the authenticated principal; any owner id a
/// caller might bolt on here would be ignored (ActionGraphBundle precedent). The amount /
/// asset descriptor are the only economically meaningful fields.
/// </summary>
public class AllocationRequest
{
    /// <summary>
    /// Which primitive to run (D4). <see cref="AllocationKind.IssueUnits"/> is the
    /// tenant-facing default; <see cref="AllocationKind.Mint"/> is retired for tenants.
    /// </summary>
    public AllocationKind Kind { get; set; } = AllocationKind.IssueUnits;

    /// <summary>
    /// Target chain for provision-if-absent + the allocation (e.g. "Algorand",
    /// "Solana"). Matched case-insensitively against the user's wallets to
    /// decide reuse vs. generate.
    /// </summary>
    public string ChainType { get; set; } = string.Empty;

    /// <summary>
    /// The already-decided amount of the asset to allocate, as a string for
    /// arbitrary precision (house convention). It is the caller-authoritative
    /// gross amount; AZOA validates the provider range and applies node fees.
    /// </summary>
    public string Amount { get; set; } = string.Empty;

    /// <summary>Human-readable asset name (mint path).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional asset description (mint path).</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Chain-native asset / token identifier. REQUIRED on
    /// <see cref="AllocationKind.IssueUnits"/> and <see cref="AllocationKind.Transfer"/>
    /// (D47: assetId is required, never optional — the units being issued must name the
    /// asset they are units OF); optional only on the retired mint path.
    /// </summary>
    public string? AssetId { get; set; }

    /// <summary>
    /// For <see cref="AllocationKind.Transfer"/>: the existing AZOA asset (NFT)
    /// id to transfer to the target user's custodial wallet.
    /// </summary>
    public Guid? AssetRecordId { get; set; }

    /// <summary>
    /// The named reserve partition an <see cref="AllocationKind.IssueUnits"/> draw comes
    /// from (<c>contributor</c>, <c>investor</c>, or any the tenant has declared).
    /// Required on that path: a default would let one purpose quietly overdraw another's
    /// earmark, which is the exact oversubscription the node-side partition counters exist
    /// to refuse.
    /// </summary>
    public string? Partition { get; set; }

    /// <summary>
    /// Why these units are being issued, in the caller's own words (e.g.
    /// <c>"milestone-2 contributor stake"</c>). Recorded against the durable operation so
    /// an operator, an auditor, or the recipient can see what a movement was FOR. Free
    /// text, bounded; it is never interpreted.
    /// </summary>
    public string? Purpose { get; set; }

    /// <summary>
    /// The fee schedule version the caller quoted (see <see cref="PreviewOnly"/>). When
    /// set and the node's live schedule has moved on, the allocation is REFUSED rather
    /// than settling on terms the caller never agreed to.
    /// </summary>
    public long? ExpectedFeeScheduleVersion { get; set; }

    /// <summary>
    /// Quote only. The node runs every readiness check and returns the exact gross, node
    /// fee, net and schedule version — and performs NO allocation, claims no idempotency
    /// key, and provisions nothing. This is how a caller sees the fee BEFORE it locks
    /// terms with a person. A preview result carries an empty operation id.
    /// </summary>
    public bool PreviewOnly { get; set; }

    /// <summary>Optional extra asset metadata (mint path).</summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>Optional free-text memo carried onto the transfer.</summary>
    public string? Memo { get; set; }
}
