// SPDX-License-Identifier: UNLICENSED

using System.Numerics;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Reserve;

/// <summary>
/// Every project's own custodial account on the node — the account funding actually
/// moves TO. Under a treasury-rate exchange the platform unit leg moves sponsor →
/// platform reserve and the project asset leg moves reserve → sponsor, so without this
/// the project itself receives NOTHING on chain and its funding balance exists only as
/// rows in a tenant's own database. That is a ledger of record for real value, which is
/// exactly the shape D47 rejected for stakes.
/// <para>
/// A treasury account is created the way the platform reserve is: its key is DERIVED
/// inside <see cref="AZOA.WebAPI.Interfaces.Managers.IKeyCustodyService"/> from the
/// chain's platform key, so the node stores no new secret and a chain with no configured
/// platform key resolves NO treasury address rather than one nobody can sign for. It is
/// deliberately NOT a tenant child user: a treasury is not a person, so no person's KYC
/// decision and no person-uniqueness gate has anything to say about it.
/// </para>
/// <para>
/// ⚠ Not to be confused with <c>NodeTreasuryManager</c> / <c>NodeTreasuryDestination</c>,
/// which are where the NODE's own fees land. Those belong to the operator; these belong
/// to the projects.
/// </para>
/// </summary>
public interface IProjectTreasury
{
    /// <summary>
    /// The project's treasury account, creating the ledger row on first call. Idempotent:
    /// a second caller gets the row the first one created, never a second account.
    /// </summary>
    Task<AZOAResult<ProjectTreasuryAccount>> ResolveAsync(
        ProjectTreasuryReference reference, CancellationToken ct = default);

    /// <summary>
    /// The project's treasury account WITHOUT creating one. A read surface must never be
    /// able to bring an account into existence as a side effect of looking at it.
    /// </summary>
    Task<AZOAResult<ProjectTreasuryAccount>> FindAsync(
        ProjectTreasuryReference reference, CancellationToken ct = default);

    /// <summary>Treasury holding of one asset (null = the chain's own unit), in base units.</summary>
    Task<AZOAResult<string>> GetBalanceAsync(
        ProjectTreasuryReference reference, string? assetId = null, CancellationToken ct = default);

    /// <summary>
    /// Runs <paramref name="sign"/> against the treasury's key inside the custody seam's
    /// decrypt-derive-sign-zero scope. Neither the platform key nor the derived treasury
    /// key is ever returned or logged.
    /// </summary>
    Task<AZOAResult<T>> SignAsTreasuryAsync<T>(
        ProjectTreasuryReference reference, SigningContext signingContext, Func<byte[], Task<T>> sign);

    /// <summary>The treasury's ledger row, or a not-found failure. Pure read; derives no key.</summary>
    Task<AZOAResult<ProjectTreasuryLedger>> GetLedgerAsync(
        ProjectTreasuryReference reference, CancellationToken ct = default);

    /// <summary>Every treasury a tenant owns, newest first, capped at <paramref name="limit"/>.</summary>
    Task<AZOAResult<IReadOnlyList<ProjectTreasuryLedger>>> ListByTenantAsync(
        Guid tenantId, int limit = 100, CancellationToken ct = default);

    /// <summary>
    /// Moves one asset's counters — the node-side availability check. A spend or a hold
    /// that would take availability negative is REFUSED, and nothing moves.
    /// </summary>
    Task<AZOAResult<ProjectTreasuryMovementOutcome>> RecordMovementAsync(
        ProjectTreasuryMovementRequest request, CancellationToken ct = default);

    /// <summary>
    /// Closes a treasury. Refused while any asset still shows available or held units, so
    /// closing can never strand a project's money in an account nothing will spend from.
    /// </summary>
    Task<AZOAResult<ProjectTreasuryLedger>> CloseAsync(
        ProjectTreasuryCloseRequest request, CancellationToken ct = default);
}

/// <summary>Persistence seam for the treasury ledger; the Surreal implementation ships with the service.</summary>
public interface IProjectTreasuryLedgerStore
{
    /// <summary>Reads one ledger row by its deterministic record id; null when absent.</summary>
    Task<AZOAResult<ProjectTreasuryLedger?>> GetAsync(string recordId, CancellationToken ct = default);

    /// <summary>Creates the row only if it is absent. A losing racer gets the winner's row and <c>Created = false</c>.</summary>
    Task<AZOAResult<ProjectTreasuryLedgerClaim>> CreateIfAbsentAsync(
        ProjectTreasuryLedger row, CancellationToken ct = default);

    /// <summary>
    /// Compare-and-swap write: applies <paramref name="row"/> only while the persisted
    /// <c>version</c> still equals <paramref name="expectedVersion"/>. A lost race is an
    /// error, never a silent overwrite.
    /// </summary>
    Task<AZOAResult<ProjectTreasuryLedger>> UpdateIfVersionAsync(
        ProjectTreasuryLedger row, long expectedVersion, CancellationToken ct = default);

    /// <summary>Every treasury row owned by one tenant, newest first.</summary>
    Task<AZOAResult<IReadOnlyList<ProjectTreasuryLedger>>> ListByTenantAsync(
        string tenantId, CancellationToken ct = default);
}

/// <summary>Outcome of a create-if-absent claim: the row, plus whether this caller created it.</summary>
public sealed record ProjectTreasuryLedgerClaim(ProjectTreasuryLedger Row, bool Created);

/// <summary>
/// Names one project's treasury: the chain it lives on, the tenant that owns the project,
/// and the tenant's own opaque project id. The reference is the derivation input, so two
/// projects can never resolve to one account.
/// </summary>
public sealed record ProjectTreasuryReference(string ChainType, Guid TenantId, string ProjectReference)
{
    /// <summary>Canonical tenant id as stored on the ledger row (Guid "N", lower-case hex).</summary>
    public string TenantIdText => TenantId.ToString("N");

    /// <summary>Trimmed project id; the tenant's vocabulary, never parsed further.</summary>
    public string NormalizedProjectReference => ProjectReference?.Trim() ?? string.Empty;

    /// <summary>Trimmed chain name.</summary>
    public string NormalizedChainType => ChainType?.Trim() ?? string.Empty;

    /// <summary>True when every component needed to derive an account is present.</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(NormalizedChainType) &&
        TenantId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(NormalizedProjectReference);
}

/// <summary>One project's custodial account on one chain.</summary>
/// <param name="ChainType">Chain the address belongs to.</param>
/// <param name="TenantId">Tenant that owns the project.</param>
/// <param name="ProjectReference">The tenant's own project id.</param>
/// <param name="Address">Address derived from that chain's platform key for this project.</param>
/// <param name="DerivationVersion">Derivation rule that produced the address.</param>
/// <param name="Status">Whether the treasury still accepts movements.</param>
public sealed record ProjectTreasuryAccount(
    string ChainType,
    Guid TenantId,
    string ProjectReference,
    string Address,
    string DerivationVersion,
    ProjectTreasuryStatus Status);

/// <summary>Whether a treasury still accepts movements.</summary>
public enum ProjectTreasuryStatus
{
    /// <summary>Accepting credits, holds and spends.</summary>
    Open = 0,

    /// <summary>Settled and retired; every further movement is refused.</summary>
    Closed = 1,
}

/// <summary>How a movement changes an asset's counters.</summary>
public enum ProjectTreasuryMovementKind
{
    /// <summary>Units arrived at the treasury — funding, or a refund coming back.</summary>
    Credit = 0,

    /// <summary>Units are committed to a payout that has not settled yet.</summary>
    Hold = 1,

    /// <summary>A hold settled: held units become spent units. Net-zero against availability.</summary>
    ReleaseHold = 2,

    /// <summary>A hold was abandoned: held units go back to availability.</summary>
    CancelHold = 3,

    /// <summary>Units left the treasury without having been held first.</summary>
    Spend = 4,
}

/// <summary>
/// One movement on one asset. <c>OperationKey</c> is required and is the replay anchor:
/// the ledger keeps a bounded window of recently applied keys so a retried movement
/// cannot double-count an asset.
/// </summary>
public sealed record ProjectTreasuryMovementRequest(
    ProjectTreasuryReference Reference,
    string AssetId,
    ProjectTreasuryMovementKind Kind,
    BigInteger Amount,
    string OperationKey);

/// <summary>What a movement did, and the asset's counters afterwards.</summary>
public sealed record ProjectTreasuryMovementOutcome(
    string AssetId,
    ProjectTreasuryMovementKind Kind,
    BigInteger Amount,
    ProjectTreasuryAssetCounters Counters,
    long Version,
    bool Replayed);

/// <summary>An operator's request to retire a settled treasury.</summary>
public sealed record ProjectTreasuryCloseRequest(
    ProjectTreasuryReference Reference,
    string Reason,
    Guid ClosedByUserId);

/// <summary>
/// One asset's counters on one treasury, all in that asset's base units. They record what
/// the project has been CREDITED, what it has SPENT, and what is HELD against a payout
/// that has not settled; chain truth is still the balance.
/// </summary>
public sealed record ProjectTreasuryAssetCounters(
    BigInteger Credited,
    BigInteger Spent,
    BigInteger Held)
{
    /// <summary>An asset the treasury knows about but has never been credited with.</summary>
    public static ProjectTreasuryAssetCounters Empty { get; } =
        new(BigInteger.Zero, BigInteger.Zero, BigInteger.Zero);

    /// <summary>Units still spendable: credited, less what is already spent or held.</summary>
    public BigInteger Available => Credited - Spent - Held;

    /// <summary>True when nothing is left to spend and nothing is promised — the close precondition.</summary>
    public bool IsSettled => Available == BigInteger.Zero && Held == BigInteger.Zero;

    /// <summary>
    /// Applies a movement, or explains why it cannot be applied. Total and side-effect
    /// free: every refusal names the arithmetic that failed, so a caller never has to
    /// guess whether it was rejected for insufficient availability or for a bad amount.
    /// </summary>
    public bool TryApply(
        ProjectTreasuryMovementKind kind,
        BigInteger amount,
        string assetId,
        out ProjectTreasuryAssetCounters next,
        out string error)
    {
        next = this;
        error = string.Empty;

        if (amount <= BigInteger.Zero)
        {
            error = "A project treasury movement amount must be positive.";
            return false;
        }

        switch (kind)
        {
            case ProjectTreasuryMovementKind.Credit:
                next = this with { Credited = Credited + amount };
                return true;

            case ProjectTreasuryMovementKind.Spend:
                if (Available < amount)
                {
                    error = InsufficientMessage(assetId, amount, "spend");
                    return false;
                }
                next = this with { Spent = Spent + amount };
                return true;

            case ProjectTreasuryMovementKind.Hold:
                if (Available < amount)
                {
                    error = InsufficientMessage(assetId, amount, "hold");
                    return false;
                }
                next = this with { Held = Held + amount };
                return true;

            case ProjectTreasuryMovementKind.ReleaseHold:
                if (Held < amount)
                {
                    error =
                        $"Asset '{assetId}' holds only {Held} base units on this treasury; " +
                        $"cannot release {amount}.";
                    return false;
                }
                next = this with { Held = Held - amount, Spent = Spent + amount };
                return true;

            case ProjectTreasuryMovementKind.CancelHold:
                if (Held < amount)
                {
                    error =
                        $"Asset '{assetId}' holds only {Held} base units on this treasury; " +
                        $"cannot cancel a hold of {amount}.";
                    return false;
                }
                next = this with { Held = Held - amount };
                return true;

            default:
                error = $"Unsupported project treasury movement kind '{kind}'.";
                return false;
        }
    }

    private string InsufficientMessage(string assetId, BigInteger amount, string verb) =>
        $"This treasury cannot {verb} {amount} base units of asset '{assetId}': " +
        $"{Available} available (credited {Credited}, spent {Spent}, held {Held}).";
}
