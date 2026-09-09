// SPDX-License-Identifier: UNLICENSED

using System.Numerics;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Reserve;

/// <summary>
/// The operator-owned custodial account every unit a sponsor or a worker ever receives
/// comes FROM. It holds the platform unit and every project asset, is the manager /
/// reserve / freeze / clawback authority stamped on the assets this node creates, and
/// carries the fee-payer role. Its key is the per-chain platform key resolved through
/// <see cref="AZOA.WebAPI.Interfaces.Managers.IKeyCustodyService"/> — the reserve owns no
/// separate secret, so a chain with no configured platform key resolves NO reserve
/// address rather than one the node cannot sign for.
/// <para>
/// The reserve also keeps a node-side ledger row per asset carrying NAMED PARTITIONS
/// (<c>contributor</c>, <c>investor</c>, and anything a tenant declares). Those counters
/// are what makes an oversubscription check possible ON THE NODE: sponsor exchanges and
/// contributor holds draw from the same reserve, so a partition policed only in a
/// tenant's own database can be overdrawn by the other caller.
/// </para>
/// </summary>
public interface IPlatformReserve
{
    /// <summary>The reserve account on <paramref name="chainType"/>; fails closed when no key resolves.</summary>
    Task<AZOAResult<ReserveAccount>> GetAccountAsync(string chainType, CancellationToken ct = default);

    /// <summary>Reserve holding of one asset (null = the chain's own unit), in base units.</summary>
    Task<AZOAResult<string>> GetBalanceAsync(
        string chainType, string? assetId = null, CancellationToken ct = default);

    /// <summary>
    /// Runs <paramref name="sign"/> against the reserve's decrypted key inside the custody
    /// seam's decrypt-sign-zero scope. The key is never returned and never logged.
    /// </summary>
    Task<AZOAResult<T>> SignAsReserveAsync<T>(
        string chainType, SigningContext signingContext, Func<byte[], Task<T>> sign);

    /// <summary>Creates the ledger row for an asset the reserve holds. Idempotent per asset.</summary>
    Task<AZOAResult<PlatformReserveLedger>> RegisterAssetAsync(
        ReserveAssetRegistration registration, CancellationToken ct = default);

    /// <summary>The ledger row for an asset, or a not-found failure.</summary>
    Task<AZOAResult<PlatformReserveLedger>> GetLedgerAsync(
        string chainType, string assetId, CancellationToken ct = default);

    /// <summary>
    /// Declares (or widens) named partitions on an asset, on the stated authority. Refuses
    /// when the declared total across every partition would exceed the asset's total supply,
    /// refuses to shrink a partition below what it has already committed, and refuses an
    /// authority that does not own the asset's ledger row. A re-declaration SETS a
    /// partition's budget rather than adding to it, so declaring the same amount twice
    /// leaves the budget where it was.
    /// <para>
    /// <paramref name="authority"/> is required and positional for the same reason it is on
    /// <see cref="ReserveDrawRequest"/> (OI-20 / OI-21): declaring a partition is what CARVES
    /// the shared supply, so a new declaration seam cannot be written without stating on
    /// whose authority it carves.
    /// </para>
    /// </summary>
    Task<AZOAResult<PlatformReserveLedger>> DeclarePartitionsAsync(
        string chainType,
        string assetId,
        IReadOnlyDictionary<string, BigInteger> declaredByPartition,
        ReserveDrawAuthority authority,
        CancellationToken ct = default);

    /// <summary>
    /// Moves one partition's counters — the node-side oversubscription check. A draw that
    /// would take a partition past what it declared is REFUSED, and no value moves.
    /// </summary>
    Task<AZOAResult<ReserveDrawOutcome>> DrawAsync(
        ReserveDrawRequest request, CancellationToken ct = default);
}

/// <summary>Persistence seam for the reserve ledger; the Surreal implementation ships with the service.</summary>
public interface IPlatformReserveLedgerStore
{
    /// <summary>Reads one ledger row by its deterministic record id; null when absent.</summary>
    Task<AZOAResult<PlatformReserveLedger?>> GetAsync(string recordId, CancellationToken ct = default);

    /// <summary>Creates the row only if it is absent. A losing racer gets the winner's row and <c>Created = false</c>.</summary>
    Task<AZOAResult<PlatformReserveLedgerClaim>> CreateIfAbsentAsync(
        PlatformReserveLedger row, CancellationToken ct = default);

    /// <summary>
    /// Compare-and-swap write: applies <paramref name="row"/> only while the persisted
    /// <c>version</c> still equals <paramref name="expectedVersion"/>. A lost race is an
    /// error, never a silent overwrite.
    /// </summary>
    Task<AZOAResult<PlatformReserveLedger>> UpdateIfVersionAsync(
        PlatformReserveLedger row, long expectedVersion, CancellationToken ct = default);
}

/// <summary>Outcome of a create-if-absent claim: the row, plus whether this caller created it.</summary>
public sealed record PlatformReserveLedgerClaim(PlatformReserveLedger Row, bool Created);

/// <summary>The reserve's account on one chain.</summary>
/// <param name="ChainType">Chain the address belongs to.</param>
/// <param name="Address">Address derived from that chain's platform key.</param>
/// <param name="IsFeePayer">True while the reserve also carries the fee-payer role (it always does today).</param>
public sealed record ReserveAccount(string ChainType, string Address, bool IsFeePayer);

/// <summary>What the reserve is told about an asset it has started holding.</summary>
public sealed record ReserveAssetRegistration(
    string ChainType,
    string AssetId,
    string UnitName,
    int Decimals,
    BigInteger TotalSupply,
    Guid? TenantId = null);

/// <summary>How a draw moves a partition's counters.</summary>
public enum ReserveDrawKind
{
    /// <summary>Units leave the reserve for a final recipient.</summary>
    Issue = 0,

    /// <summary>Units are committed to a hold (W3.4) but have not yet left the reserve.</summary>
    Hold = 1,

    /// <summary>A hold is honoured: held units become issued units. Net-zero against availability.</summary>
    ReleaseHold = 2,

    /// <summary>A hold is abandoned: held units go back to the partition's availability.</summary>
    CancelHold = 3,

    /// <summary>Issued units come back to the reserve (a refund), restoring availability.</summary>
    Return = 4,
}

/// <summary>
/// One partition draw. <c>OperationKey</c> is required and is the replay anchor: the
/// ledger keeps a bounded window of recently applied keys so a retried draw cannot
/// double-count a partition. <c>Authority</c> is required and positional on purpose —
/// a new draw seam cannot be written without stating on whose authority it draws
/// (OI-20 / OI-21); see <see cref="ReserveDrawAuthority"/>.
/// </summary>
public sealed record ReserveDrawRequest(
    string ChainType,
    string AssetId,
    string Partition,
    ReserveDrawKind Kind,
    BigInteger Amount,
    string OperationKey,
    ReserveDrawAuthority Authority);

/// <summary>What a draw did, and the partition's counters afterwards.</summary>
public sealed record ReserveDrawOutcome(
    string AssetId,
    string Partition,
    ReserveDrawKind Kind,
    BigInteger Amount,
    ReservePartitionCounters Counters,
    long Version,
    bool Replayed);

/// <summary>
/// One named partition's counters, all in the asset's base units. Every draw is checked
/// against <c>Issued + Held - Returned &lt;= Declared</c>; the slack is <see cref="Available"/>.
/// </summary>
public sealed record ReservePartitionCounters(
    BigInteger Declared,
    BigInteger Issued,
    BigInteger Held,
    BigInteger Returned)
{
    /// <summary>A partition that has been declared but never drawn.</summary>
    public static ReservePartitionCounters Declaring(BigInteger declared) =>
        new(declared, BigInteger.Zero, BigInteger.Zero, BigInteger.Zero);

    /// <summary>Units still drawable from this partition.</summary>
    public BigInteger Available => Declared - Issued - Held + Returned;

    /// <summary>Units this partition has committed one way or another.</summary>
    public BigInteger Committed => Issued + Held - Returned;

    /// <summary>
    /// Applies a draw, or explains why it cannot be applied. Total and side-effect free:
    /// every refusal names the partition arithmetic that failed, so a caller never has to
    /// guess whether a draw was rejected for oversubscription or for a bad amount.
    /// </summary>
    public bool TryApply(
        ReserveDrawKind kind,
        BigInteger amount,
        string partitionName,
        out ReservePartitionCounters next,
        out string error)
    {
        next = this;
        error = string.Empty;

        if (amount <= BigInteger.Zero)
        {
            error = "A reserve draw amount must be positive.";
            return false;
        }

        switch (kind)
        {
            case ReserveDrawKind.Issue:
                if (Available < amount)
                {
                    error = OversubscribedMessage(partitionName, amount);
                    return false;
                }
                next = this with { Issued = Issued + amount };
                return true;

            case ReserveDrawKind.Hold:
                if (Available < amount)
                {
                    error = OversubscribedMessage(partitionName, amount);
                    return false;
                }
                next = this with { Held = Held + amount };
                return true;

            case ReserveDrawKind.ReleaseHold:
                if (Held < amount)
                {
                    error =
                        $"Partition '{partitionName}' holds only {Held} base units; " +
                        $"cannot release {amount}.";
                    return false;
                }
                next = this with { Held = Held - amount, Issued = Issued + amount };
                return true;

            case ReserveDrawKind.CancelHold:
                if (Held < amount)
                {
                    error =
                        $"Partition '{partitionName}' holds only {Held} base units; " +
                        $"cannot cancel a hold of {amount}.";
                    return false;
                }
                next = this with { Held = Held - amount };
                return true;

            case ReserveDrawKind.Return:
                if (Issued - Returned < amount)
                {
                    error =
                        $"Partition '{partitionName}' has {Issued - Returned} base units " +
                        $"outstanding; cannot return {amount}.";
                    return false;
                }
                next = this with { Returned = Returned + amount };
                return true;

            default:
                error = $"Unsupported reserve draw kind '{kind}'.";
                return false;
        }
    }

    private string OversubscribedMessage(string partitionName, BigInteger amount) =>
        $"Partition '{partitionName}' would be oversubscribed: {amount} base units requested, " +
        $"{Available} available (declared {Declared}, issued {Issued}, held {Held}, returned {Returned}).";
}
