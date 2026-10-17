// SPDX-License-Identifier: UNLICENSED
//
// OI-20 / OI-21. The reserve is SHARED: one ledger row per asset, one set of named
// partitions on it, and every seam in the node draws out of the same counters. So the
// question "on whose authority is this draw happening?" has to be answered at the place
// that MOVES the counter, not at each of the doors that reach it — there are seven draw
// seams today and the reviewer found the third one uncovered after two were fixed.
//
// See Services/Reserve/AGENTS.md §draw-authority.

using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Reserve;

/// <summary>
/// On whose authority a reserve partition is drawn. Required on every
/// <see cref="ReserveDrawRequest"/>, so a new draw seam cannot be written without
/// answering the question — the compiler refuses the call otherwise.
/// <para>
/// There is no "unknown" authority and no default. The absence of an acting tenant is NOT
/// an operator signal: a user-driven action run carries a null acting tenant
/// (<c>ActionNodeStepHandler</c>), and reading that as "the operator" would hand every
/// holder of a value-signing key the whole of this node's own supply.
/// </para>
/// </summary>
public sealed record ReserveDrawAuthority
{
    private ReserveDrawAuthority(Guid? actingTenantId, bool isNodeOperator, bool isSettlement)
    {
        ActingTenantId = actingTenantId;
        IsNodeOperator = isNodeOperator;
        IsSettlingGuardedCommitment = isSettlement;
    }

    /// <summary>The tenant driving this draw, or null when no tenant could be resolved.</summary>
    public Guid? ActingTenantId { get; }

    /// <summary>True only for the node's own internal rails, which carry a positive operator signal.</summary>
    public bool IsNodeOperator { get; }

    /// <summary>True only when settling a commitment whose asset and partition come off a guarded row.</summary>
    public bool IsSettlingGuardedCommitment { get; }

    /// <summary>
    /// The node's own rails: on-ramp, off-ramp, chargeback recovery and genesis. Valid ONLY
    /// where neither the asset nor the partition can be named by a caller — both come from
    /// node configuration or a node-written row. This is the positive operator signal OI-21
    /// asks for; nothing derives it from a missing tenant.
    /// </summary>
    public static ReserveDrawAuthority NodeOperator { get; } = new(null, true, false);

    /// <summary>
    /// A tenant-driven draw. On a tenant-registered asset the tenant must be the one that
    /// registered it; on the node's own unit the operator's
    /// <see cref="ReserveOptions.PlatformUnitDrawPolicy"/> decides. A null or empty tenant is
    /// a refusal under every policy, never a pass.
    /// </summary>
    public static ReserveDrawAuthority ForTenant(Guid? actingTenantId) => new(actingTenantId, false, false);

    /// <summary>
    /// Settling a commitment that was already scope-checked when it was opened — a hold's
    /// release, cancel or refund draw. Valid ONLY where the chain, asset and partition are
    /// read off the persisted hold row rather than off the caller, so there is nothing for a
    /// caller to name and nothing to steal. Never valid on a path that accepts a
    /// client-supplied asset id or partition name.
    /// </summary>
    public static ReserveDrawAuthority SettlingGuardedCommitment { get; } = new(null, false, true);
}

/// <summary>
/// Accepted values for <see cref="ReserveOptions.PlatformUnitDrawPolicy"/> — who, besides
/// the node operator, may draw a declared partition of the node's OWN unit. An
/// unrecognised OR BLANK value fails closed, so a typo — or an env var set to "" — narrows
/// the node rather than opening it.
/// </summary>
public static class ReservePlatformUnitDrawPolicies
{
    /// <summary>Nobody but the operator's own rails. The narrowest setting, and the DEFAULT.</summary>
    public const string OperatorOnly = "OperatorOnly";

    /// <summary>Only the tenants listed in <see cref="ReserveOptions.PlatformUnitDrawTenants"/>.</summary>
    public const string DeclaredTenants = "DeclaredTenants";

    /// <summary>
    /// Any tenant this node resolved from an operator-issued, value-scoped principal:
    /// admission IS the key-issuance decision. The widest setting, and never the default —
    /// it still refuses a caller with no resolvable tenant at all, which is the hole OI-21
    /// names, but it does hand every admitted tenant the node's own declared partitions.
    /// </summary>
    public const string AnyAdmittedTenant = "AnyAdmittedTenant";
}

/// <summary>
/// Who may draw a declared reserve partition. The ONE predicate, asked by
/// <see cref="PlatformReserveService.DrawAsync"/> itself — the single place a partition
/// counter moves — so no draw seam can reach the counters without it.
/// </summary>
internal static class ReservePartitionScope
{
    /// <summary>
    /// Null when this authority may draw the asset's declared partitions; the refusal
    /// otherwise. An asset registered by a tenant is that tenant's budget, so a draw driven
    /// by another tenant would consume it out of the shared reserve.
    /// </summary>
    /// <param name="reserveRow">
    /// The reserve's own row for the asset, or null when this seam could not read one. A row
    /// that is absent carries no tenant and is therefore treated exactly like the platform's
    /// own unit.
    /// </param>
    internal static string? Refuse(
        PlatformReserveLedger? reserveRow,
        ReserveDrawAuthority? authority,
        string assetId,
        ReserveOptions? options = null)
    {
        // No authority at all is a refusal. There is no default, and the null case is here
        // only because a nullable reference can still arrive from a non-nullable context.
        if (authority is null)
            return $"A draw on asset '{assetId}' arrived with no stated authority. Nothing was drawn.";

        // Settling an already-guarded commitment: the asset and partition came off the row
        // the opening draw was checked against, so re-asking the ownership question would
        // only be able to strand value that is already committed.
        if (authority.IsSettlingGuardedCommitment)
            return null;

        var owner = ReserveRowTenant(reserveRow);

        // An untenanted row is NOT ownerless. It is the platform's own unit —
        // PlatformTokenGenesis mints it with no acting tenant — so who may draw it is the
        // operator's decision, and NEVER inferred from the absence of a tenant on the
        // request. That inference is the hole OI-21 names: a user-driven action run reaches
        // the hold seam with exactly that missing tenant.
        if (owner == Guid.Empty)
            return RefusePlatformUnitDraw(authority, assetId, options ?? DefaultOptions);

        // A tenant's registered asset. The node operator's own rails never name a tenant's
        // asset (their asset comes from a node-written row on the platform unit), so an
        // operator-signalled draw here is a miswired seam and is refused rather than trusted.
        var acting = authority.ActingTenantId ?? Guid.Empty;
        if (acting == Guid.Empty)
            return $"Asset '{assetId}' was registered with the reserve by a tenant, so drawing its " +
                   "declared partitions needs a resolvable acting tenant. Nothing was drawn.";

        return acting == owner
            ? null
            : $"Asset '{assetId}' was registered with the reserve by another tenant, so its declared " +
              "partitions are not this caller's to draw from. Nothing was drawn.";
    }

    private static readonly ReserveOptions DefaultOptions = new();

    /// <summary>
    /// The node's OWN unit. Its ledger row carries no tenant because PlatformTokenGenesis
    /// mints it with no acting tenant, so "no tenant on the row" means "the operator's",
    /// never "anybody's". Who else may draw it is an operator setting, and a caller with no
    /// resolvable tenant at all is refused under every setting — that is the shape a
    /// user-driven action run arrives in (ActionNodeStepHandler builds its node context with
    /// ActingTenantId = null, HoldNodeHandler forwards it), with the asset id and the
    /// partition both taken from author-supplied action config.
    /// </summary>
    private static string? RefusePlatformUnitDraw(
        ReserveDrawAuthority authority, string assetId, ReserveOptions options)
    {
        if (authority.IsNodeOperator)
            return null;

        var acting = authority.ActingTenantId ?? Guid.Empty;
        if (acting == Guid.Empty)
        {
            return $"Asset '{assetId}' is this node's own unit. Drawing its declared partitions " +
                   "needs the operator, or a caller this node resolved a tenant for; this one has " +
                   "neither. Nothing was drawn.";
        }

        // A BLANK policy is the shape a Railway env var set to "" produces, and ReserveOptions
        // is bound with .Get<T>() so that blank REPLACES the C# default instead of leaving it.
        // It resolves to the NARROWEST setting, never the widest: a node that says nothing keeps
        // its own supply to its own rails, which is what both doc-comments already promise.
        var policy = string.IsNullOrWhiteSpace(options.PlatformUnitDrawPolicy)
            ? ReservePlatformUnitDrawPolicies.OperatorOnly
            : options.PlatformUnitDrawPolicy.Trim();

        if (string.Equals(policy, ReservePlatformUnitDrawPolicies.AnyAdmittedTenant, StringComparison.OrdinalIgnoreCase))
            return null;

        if (string.Equals(policy, ReservePlatformUnitDrawPolicies.DeclaredTenants, StringComparison.OrdinalIgnoreCase))
        {
            var admitted = options.PlatformUnitDrawTenants.Any(candidate =>
                Guid.TryParse(candidate?.Trim(), out var declared) && declared == acting);
            return admitted
                ? null
                : $"Asset '{assetId}' is this node's own unit and this tenant is not one the operator " +
                  $"admitted to draw it (Reserve:PlatformUnitDrawTenants). Nothing was drawn.";
        }

        if (string.Equals(policy, ReservePlatformUnitDrawPolicies.OperatorOnly, StringComparison.OrdinalIgnoreCase))
        {
            return $"Asset '{assetId}' is this node's own unit, so its declared partitions are the " +
                   "operator's to draw from and not a tenant's. Nothing was drawn.";
        }

        // An unrecognised policy narrows the node rather than opening it.
        return $"Asset '{assetId}' is this node's own unit and Reserve:PlatformUnitDrawPolicy is set to " +
               $"'{policy}', which this node does not recognise. Nothing was drawn.";
    }

    /// <summary>The tenant that registered an asset with the reserve; empty for the node's own unit.</summary>
    internal static Guid ReserveRowTenant(PlatformReserveLedger? reserveRow) =>
        ParseRowUserId(reserveRow?.TenantId);

    /// <summary>A user id off a row; a record link keeps its <c>user:</c> prefix, so strip it.</summary>
    internal static Guid ParseRowUserId(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Guid.Empty;

        var text = userId.Trim();
        var colon = text.LastIndexOf(':');
        if (colon >= 0 && colon < text.Length - 1)
            text = text[(colon + 1)..];

        return Guid.TryParseExact(text, "N", out var parsed) ? parsed : Guid.Empty;
    }
}
