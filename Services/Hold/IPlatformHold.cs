// SPDX-License-Identifier: UNLICENSED

using System.Numerics;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Hold;

/// <summary>
/// The node's HOLD primitive (D14): units are committed in a platform-custodial
/// account for a named beneficiary and stay there until the hold window elapses, at
/// which point exactly one of three things happens to them — they are released to the
/// beneficiary, released into a SECOND hold, or given back.
/// <para>
/// Nothing on any layer could hold before this. That is what made "you own it" dishonest
/// under a chargeback: freshly issued stake landed in a person's own account immediately,
/// so the only way to undo a reversed payment was to take units back out of an account
/// the node had already told them was theirs.
/// </para>
/// <para>
/// The holding account is the platform reserve account (see
/// <see cref="AZOA.WebAPI.Services.Reserve.IPlatformReserve"/>) and the commitment is
/// expressed on the reserve's named-partition ledger with
/// <see cref="AZOA.WebAPI.Services.Reserve.ReserveDrawKind.Hold"/>. That is deliberate:
/// the reserve is the manager/reserve authority on every asset this node creates, so it
/// can already hold those assets and no second custodial account has to be funded and
/// made able to accept each asset one at a time. An asset the reserve has no ledger row
/// for cannot be held at all — see <c>Services/Hold/AGENTS.md</c> §holding-account.
/// </para>
/// </summary>
public interface IPlatformHold
{
    /// <summary>
    /// Opens a hold, or returns the one this hold id already opened. The hold id is the
    /// idempotency anchor: a retried open with the same material terms returns the
    /// existing hold and commits nothing twice; one with DIFFERENT terms is refused
    /// rather than silently reinterpreted.
    /// </summary>
    Task<AZOAResult<HoldOutcome>> OpenAsync(HoldOpenRequest request, CancellationToken ct = default);

    /// <summary>
    /// Hands a due hold's units to its beneficiary. Refused before
    /// <see cref="HoldLedger.ReleaseNotBefore"/>; the beneficiary's account is made able
    /// to receive the asset first, and a release that was already broadcast is reconciled
    /// against the rail rather than re-sent.
    /// </summary>
    Task<AZOAResult<HoldOutcome>> ReleaseAsync(HoldSettlementRequest request, CancellationToken ct = default);

    /// <summary>
    /// Abandons a hold: the commitment goes back to the partition it was drawn from and
    /// nothing moves, because nothing ever left the holding account. Refused once the
    /// release draw has committed — those units are already counted as issued.
    /// </summary>
    Task<AZOAResult<HoldOutcome>> RefundAsync(HoldSettlementRequest request, CancellationToken ct = default);

    /// <summary>
    /// Closes one hold by opening the NEXT one over the same units. This is what
    /// "approve completion" is — a release into the worker's own window — and a plain
    /// release cannot express it, because a release ENDS the commitment while the second
    /// window has to keep it.
    /// </summary>
    Task<AZOAResult<HoldOutcome>> ReleaseIntoHoldAsync(HoldChainRequest request, CancellationToken ct = default);

    /// <summary>One hold, or a not-found failure. Pure read.</summary>
    Task<AZOAResult<HoldLedger>> GetAsync(HoldReference reference, CancellationToken ct = default);

    /// <summary>Every hold a user funds or benefits from, newest first.</summary>
    Task<AZOAResult<IReadOnlyList<HoldLedger>>> ListForUserAsync(
        Guid userId, int limit = 100, CancellationToken ct = default);

    /// <summary>
    /// Holds whose window has elapsed and that asked to be released automatically, plus
    /// any left mid-release by a crash. The hosted sweep's work list.
    /// </summary>
    Task<AZOAResult<IReadOnlyList<HoldLedger>>> ListDueAsync(
        string chainType, DateTimeOffset asOf, int limit = 50, CancellationToken ct = default);
}

/// <summary>Persistence seam for the hold ledger; the Surreal implementation ships with the service.</summary>
public interface IHoldLedgerStore
{
    /// <summary>Reads one hold by its deterministic record id; null when absent.</summary>
    Task<AZOAResult<HoldLedger?>> GetAsync(string recordId, CancellationToken ct = default);

    /// <summary>Creates the row only if it is absent. A losing racer gets the winner's row and <c>Created = false</c>.</summary>
    Task<AZOAResult<HoldLedgerClaim>> CreateIfAbsentAsync(HoldLedger row, CancellationToken ct = default);

    /// <summary>
    /// Compare-and-swap write: applies <paramref name="row"/> only while the persisted
    /// <c>version</c> still equals <paramref name="expectedVersion"/>. A lost race is an
    /// error, never a silent overwrite.
    /// </summary>
    Task<AZOAResult<HoldLedger>> UpdateIfVersionAsync(
        HoldLedger row, long expectedVersion, CancellationToken ct = default);

    /// <summary>Every hold one user funds or benefits from, newest first.</summary>
    Task<AZOAResult<IReadOnlyList<HoldLedger>>> ListForUserAsync(
        string userId, CancellationToken ct = default);

    /// <summary>
    /// Holds on one chain/network that the sweep should look at: due auto-release rows
    /// and rows stuck mid-release. Ordered oldest window first so the longest-overdue
    /// beneficiary is paid first.
    /// </summary>
    Task<AZOAResult<IReadOnlyList<HoldLedger>>> ListDueAsync(
        string chain, string network, DateTimeOffset asOf, int limit, CancellationToken ct = default);
}

/// <summary>Outcome of a create-if-absent claim: the row, plus whether this caller created it.</summary>
public sealed record HoldLedgerClaim(HoldLedger Row, bool Created);

/// <summary>Names one hold: the chain it lives on and the caller's own hold id.</summary>
public sealed record HoldReference(string ChainType, string HoldId)
{
    /// <summary>Trimmed chain name.</summary>
    public string NormalizedChainType => ChainType?.Trim() ?? string.Empty;

    /// <summary>Trimmed hold id; the caller's vocabulary, never parsed further.</summary>
    public string NormalizedHoldId => HoldId?.Trim() ?? string.Empty;

    /// <summary>True when every component needed to address a hold is present.</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(NormalizedChainType) && !string.IsNullOrWhiteSpace(NormalizedHoldId);
}

/// <summary>Where a hold's units come from.</summary>
public enum HoldFundingSource
{
    /// <summary>
    /// Drawn from a named partition of the platform reserve — freshly issued stake that
    /// sits in the holding account until its window elapses. The only source built today.
    /// </summary>
    Reserve = 0,

    /// <summary>
    /// Moved in from a person's own custodial account. DECLARED, NOT BUILT: an inbound
    /// value move needs an idempotency anchor of its own or a retried open would charge
    /// the funder twice, and that anchor is
    /// <c>IBlockchainOperationManager.ExecuteAsync</c>, which this service does not yet
    /// route through. <see cref="IPlatformHold.OpenAsync"/> refuses it rather than
    /// pretending; see <c>Services/Hold/AGENTS.md</c> §funder-source.
    /// </summary>
    Funder = 1,
}

/// <summary>Lifecycle of one hold. Mirrors <c>hold_ledger.state</c> exactly.</summary>
public enum HoldState
{
    /// <summary>The row is claimed but the commitment has not been drawn yet.</summary>
    Pending = 0,

    /// <summary>Units are committed and sitting in the holding account.</summary>
    Held = 1,

    /// <summary>
    /// The partition draw has committed and the outbound move is in flight. A refund from
    /// here is refused: those units are already counted as issued.
    /// </summary>
    Releasing = 2,

    /// <summary>The beneficiary has the units. Terminal.</summary>
    Released = 3,

    /// <summary>The commitment went back to its partition; nothing moved. Terminal.</summary>
    Refunded = 4,

    /// <summary>The hold was continued as a second hold over the same units. Terminal.</summary>
    ReleasedIntoHold = 5,
}

/// <summary>What a caller must supply to open one hold.</summary>
/// <param name="Reference">Chain plus the caller's hold id — the idempotency anchor.</param>
/// <param name="AssetId">Asset being held. Must be one the reserve has a ledger row for.</param>
/// <param name="Amount">Amount in the asset's base units. Must be positive.</param>
/// <param name="FundingSource">Where the units come from.</param>
/// <param name="BeneficiaryUserId">The only person a release of this hold may pay.</param>
/// <param name="ReservePartition">Named partition the commitment is drawn from.</param>
/// <param name="FunderUserId">The person who funded it, on a funder-sourced hold.</param>
/// <param name="ReleaseNotBefore">The hold window; null means releasable immediately.</param>
/// <param name="AutoRelease">Whether the hosted sweep may release it once due.</param>
/// <param name="ActingTenantId">Tenant driving this on someone's behalf, when one is.</param>
/// <param name="Purpose">The caller's own reason, carried onto the ledger row.</param>
public sealed record HoldOpenRequest(
    HoldReference Reference,
    string AssetId,
    BigInteger Amount,
    HoldFundingSource FundingSource,
    Guid BeneficiaryUserId,
    string? ReservePartition = null,
    Guid FunderUserId = default,
    DateTimeOffset? ReleaseNotBefore = null,
    bool AutoRelease = true,
    Guid? ActingTenantId = null,
    string? Purpose = null);

/// <summary>A release or a refund of one existing hold.</summary>
public sealed record HoldSettlementRequest(
    HoldReference Reference,
    Guid? ActingTenantId = null,
    string? Reason = null);

/// <summary>Closing one hold by opening the next one over the same units.</summary>
/// <param name="Reference">The hold being continued.</param>
/// <param name="NextHoldId">Id of the hold that continues it.</param>
/// <param name="NextBeneficiaryUserId">Who the units are held for now.</param>
/// <param name="ReleaseNotBefore">
/// The next window. It may never be earlier than the parent's: chaining into a
/// shorter window would be a way out of the funder's window.
/// </param>
/// <param name="AutoRelease">Whether the sweep may release the next hold once due.</param>
/// <param name="ActingTenantId">Tenant driving this on someone's behalf, when one is.</param>
/// <param name="Purpose">The caller's own reason for the next hold.</param>
public sealed record HoldChainRequest(
    HoldReference Reference,
    string NextHoldId,
    Guid NextBeneficiaryUserId,
    DateTimeOffset? ReleaseNotBefore = null,
    bool AutoRelease = true,
    Guid? ActingTenantId = null,
    string? Purpose = null);

/// <summary>What a hold operation did.</summary>
/// <param name="Hold">The hold the call addressed, as it now stands.</param>
/// <param name="NextHold">The hold it was continued into, on a chain call only.</param>
/// <param name="TransactionId">The rail's reference for an outbound move, when one happened.</param>
/// <param name="Replayed">True when this call found the work already done and moved nothing.</param>
public sealed record HoldOutcome(
    HoldLedger Hold,
    HoldLedger? NextHold,
    string? TransactionId,
    bool Replayed);
