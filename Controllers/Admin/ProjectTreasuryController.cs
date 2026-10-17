// SPDX-License-Identifier: UNLICENSED

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Reserve;

namespace AZOA.WebAPI.Controllers.Admin;

/// <summary>
/// Operator read/close surface for per-project custodial treasuries — the accounts a
/// project's funding actually lands in. Guarded by the hardened <c>Operator</c> policy
/// (JWT floor plus an explicit operator capability; an API key can NEVER reach this),
/// the same gate the saga dead-letter board uses.
/// <para>
/// It deliberately exposes NO way to move units. Resolve creates the account, read shows
/// what the node believes against what the chain reports, and close retires a settled
/// account; crediting and spending belong to the value paths that actually move money, so
/// no operator can conjure a project balance from an admin console.
/// </para>
/// <para>
/// ⚠ This is not <c>NodeGovernanceController</c>'s treasury surface. That one routes the
/// NODE's own fees (<c>node_treasury_destination</c>); this one is the projects' money.
/// </para>
/// </summary>
[ApiController]
[Route("api/admin/project-treasuries")]
[Authorize(Policy = "Operator")]
public sealed class ProjectTreasuryController : ControllerBase
{
    private const int DefaultListLimit = 100;

    private readonly IProjectTreasury _treasuries;

    public ProjectTreasuryController(IProjectTreasury treasuries)
    {
        _treasuries = treasuries ?? throw new ArgumentNullException(nameof(treasuries));
    }

    /// <summary>
    /// One project's treasury: the account, its ledger counters, and what the chain says
    /// the address actually holds. A provider that cannot be reached degrades to a named
    /// balance error rather than failing the whole read — the ledger is still worth seeing.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(AZOAResult<ProjectTreasuryView>), 200)]
    public async Task<ActionResult<AZOAResult<ProjectTreasuryView>>> Get(
        [FromQuery] string chain,
        [FromQuery] Guid tenantId,
        [FromQuery] string projectReference,
        [FromQuery] string? assetId,
        CancellationToken ct)
    {
        var reference = new ProjectTreasuryReference(chain, tenantId, projectReference);

        var ledger = await _treasuries.GetLedgerAsync(reference, ct);
        if (ledger.IsError || ledger.Result is null)
            return NotFound(AZOAResult<ProjectTreasuryView>.Failure(ledger.Message));

        var account = await _treasuries.FindAsync(reference, ct);
        if (account.IsError || account.Result is null)
            return BadRequest(AZOAResult<ProjectTreasuryView>.Failure(account.Message));

        var balance = await _treasuries.GetBalanceAsync(reference, assetId, ct);

        return Ok(AZOAResult<ProjectTreasuryView>.Success(
            ProjectTreasuryView.From(
                account.Result,
                ledger.Result,
                balance.IsError ? null : balance.Result,
                balance.IsError ? balance.Message : null),
            "Success"));
    }

    /// <summary>Every treasury one tenant owns, newest first.</summary>
    [HttpGet("tenant/{tenantId:guid}")]
    [ProducesResponseType(typeof(AZOAResult<IReadOnlyList<ProjectTreasuryLedgerView>>), 200)]
    public async Task<ActionResult<AZOAResult<IReadOnlyList<ProjectTreasuryLedgerView>>>> ListByTenant(
        Guid tenantId,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var rows = await _treasuries.ListByTenantAsync(tenantId, limit ?? DefaultListLimit, ct);
        if (rows.IsError || rows.Result is null)
            return BadRequest(AZOAResult<IReadOnlyList<ProjectTreasuryLedgerView>>.Failure(rows.Message));

        return Ok(AZOAResult<IReadOnlyList<ProjectTreasuryLedgerView>>.Success(
            rows.Result.Select(ProjectTreasuryLedgerView.From).ToList(), "Success"));
    }

    /// <summary>
    /// Resolves (creating on first call) a project's treasury account. Idempotent: a
    /// second call returns the same account rather than a second one.
    /// </summary>
    [HttpPost("resolve")]
    [ProducesResponseType(typeof(AZOAResult<ProjectTreasuryAccountView>), 200)]
    public async Task<ActionResult<AZOAResult<ProjectTreasuryAccountView>>> Resolve(
        [FromBody] ProjectTreasuryResolveBody body,
        CancellationToken ct)
    {
        if (body is null)
            return BadRequest(AZOAResult<ProjectTreasuryAccountView>.Failure("A request body is required."));

        var reference = new ProjectTreasuryReference(body.Chain, body.TenantId, body.ProjectReference);
        var account = await _treasuries.ResolveAsync(reference, ct);
        if (account.IsError || account.Result is null)
            return BadRequest(AZOAResult<ProjectTreasuryAccountView>.Failure(account.Message));

        return Ok(AZOAResult<ProjectTreasuryAccountView>.Success(
            ProjectTreasuryAccountView.From(account.Result), account.Message));
    }

    /// <summary>
    /// Retires a settled treasury. Refused while any asset still shows available or held
    /// units, so a close can never strand a project's money.
    /// </summary>
    [HttpPost("close")]
    [ProducesResponseType(typeof(AZOAResult<ProjectTreasuryLedgerView>), 200)]
    public async Task<ActionResult<AZOAResult<ProjectTreasuryLedgerView>>> Close(
        [FromBody] ProjectTreasuryCloseBody body,
        CancellationToken ct)
    {
        if (body is null)
            return BadRequest(AZOAResult<ProjectTreasuryLedgerView>.Failure("A request body is required."));

        var operatorUserId = GetUserIdFromClaims();
        if (operatorUserId is null)
            return Unauthorized();

        var request = new ProjectTreasuryCloseRequest(
            new ProjectTreasuryReference(body.Chain, body.TenantId, body.ProjectReference),
            body.Reason ?? string.Empty,
            operatorUserId.Value);

        var closed = await _treasuries.CloseAsync(request, ct);
        if (closed.IsError || closed.Result is null)
            return BadRequest(AZOAResult<ProjectTreasuryLedgerView>.Failure(closed.Message));

        return Ok(AZOAResult<ProjectTreasuryLedgerView>.Success(
            ProjectTreasuryLedgerView.From(closed.Result), closed.Message));
    }

    private Guid? GetUserIdFromClaims()
    {
        var raw = User.FindFirst("UserId")?.Value
                  ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                  ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}

/// <summary>Body of a resolve-or-create call. NOT sealed: the close body is the same
/// three-field address plus a reason, and sharing the base keeps the two bodies from
/// drifting apart field by field.</summary>
public class ProjectTreasuryResolveBody
{
    /// <summary>Chain the treasury lives on.</summary>
    public string Chain { get; set; } = string.Empty;

    /// <summary>Tenant that owns the project.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The tenant's own opaque project id.</summary>
    public string ProjectReference { get; set; } = string.Empty;
}

/// <summary>Body of a close call; the closing operator comes from the token, never the body.</summary>
public sealed class ProjectTreasuryCloseBody : ProjectTreasuryResolveBody
{
    /// <summary>Why the treasury is being retired. Required — a close is always attributable.</summary>
    public string? Reason { get; set; }
}

/// <summary>Wire view of a treasury account.</summary>
public sealed record ProjectTreasuryAccountView(
    string Chain,
    Guid TenantId,
    string ProjectReference,
    string Address,
    string DerivationVersion,
    string Status)
{
    /// <summary>Projects the domain account onto the wire shape.</summary>
    public static ProjectTreasuryAccountView From(ProjectTreasuryAccount account) =>
        new(account.ChainType,
            account.TenantId,
            account.ProjectReference,
            account.Address,
            account.DerivationVersion,
            account.Status.ToString());
}

/// <summary>Wire view of one asset's counters on a treasury.</summary>
public sealed record ProjectTreasuryAssetView(
    string AssetId,
    string Credited,
    string Spent,
    string Held,
    string Available);

/// <summary>Wire view of a treasury ledger row.</summary>
public sealed record ProjectTreasuryLedgerView(
    string Chain,
    string Network,
    string TenantId,
    string ProjectReference,
    string Address,
    string DerivationVersion,
    string Status,
    DateTimeOffset? ClosedAt,
    string? CloseReason,
    IReadOnlyList<ProjectTreasuryAssetView> Assets,
    string? AssetsError,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Projects a persisted row onto the wire shape. An unreadable assets blob surfaces
    /// as <c>AssetsError</c> with an EMPTY asset list rather than as "no commitments" —
    /// an operator must never read a decode failure as a settled treasury.
    /// </summary>
    public static ProjectTreasuryLedgerView From(ProjectTreasuryLedger row)
    {
        var decoded = ProjectTreasuryAssetCodec.Decode(row.Assets, out var decodeError);
        var assets = decoded is null
            ? new List<ProjectTreasuryAssetView>()
            : decoded
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new ProjectTreasuryAssetView(
                    entry.Key,
                    entry.Value.Credited.ToString(),
                    entry.Value.Spent.ToString(),
                    entry.Value.Held.ToString(),
                    entry.Value.Available.ToString()))
                .ToList();

        return new ProjectTreasuryLedgerView(
            row.Chain,
            row.Network,
            string.IsNullOrWhiteSpace(row.TenantId) ? row.TenantId : SurrealRecordGuid.BareId(row.TenantId),
            row.ProjectReference,
            row.TreasuryAddress,
            row.DerivationVersion,
            row.Status,
            row.ClosedAt,
            row.CloseReason,
            assets,
            decoded is null ? decodeError : null,
            row.Version,
            row.CreatedAt,
            row.UpdatedAt);
    }
}

/// <summary>Wire view combining the account, its ledger, and chain truth for one asset.</summary>
public sealed record ProjectTreasuryView(
    ProjectTreasuryAccountView Account,
    ProjectTreasuryLedgerView Ledger,
    string? ChainBalance,
    string? ChainBalanceError)
{
    /// <summary>Combines the three reads an operator needs to see drift in one place.</summary>
    public static ProjectTreasuryView From(
        ProjectTreasuryAccount account,
        ProjectTreasuryLedger row,
        string? chainBalance,
        string? chainBalanceError) =>
        new(ProjectTreasuryAccountView.From(account),
            ProjectTreasuryLedgerView.From(row),
            chainBalance,
            chainBalanceError);
}
