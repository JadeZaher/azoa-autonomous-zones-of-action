// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Numerics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Reserve;

namespace AZOA.WebAPI.Controllers;

/// <summary>
/// The declare-a-partition surface (OI-22) — the valve the whole issuance and hold rail
/// waits on. A reserve partition is a NAMED BUDGET carved out of one asset's supply
/// ("contributor", "investor", "on-ramp"); until a partition is declared,
/// <c>PlatformReserveService.DrawAsync</c> refuses every draw against it, so no allocation
/// can be issued and no worker's stake can be held.
///
/// <para><b>Two lanes, one predicate.</b> The node operator (a JWT carrying
/// <c>operator:admin</c>) declares partitions on the node's OWN unit; the tenant that
/// registered an asset (an API key carrying <c>token:create:sign</c>) declares partitions on
/// that asset. Which of them may touch which row is NOT decided here: the authority is
/// passed to <see cref="IPlatformReserve.DeclarePartitionsAsync"/>, which asks the same
/// <c>ReservePartitionScope</c> predicate the draw side asks. Enforcing at the door instead
/// is exactly what re-opened OI-20 twice.</para>
///
/// <para><b>The tenant id comes from the principal, never from the body.</b> There is no
/// tenant field on any request here — a body-supplied tenant would let one tenant carve
/// another's budget out of the shared reserve. The body carries partition names and budgets;
/// the only other field is <c>chain</c>, which is a LOCATOR for the ledger row (one row per
/// chain, network and asset) and carries no authority of its own.</para>
///
/// <para><b>Machine surface.</b> This is the node-integration seam a tenant's backend calls
/// at fungible-token-create time. Nothing here is rendered to a sponsor or a worker, and the
/// view deliberately omits the reserve ADDRESS: a caller needs to know what a budget is, not
/// which account holds it.</para>
/// </summary>
[ApiController]
[Route("api/reserve")]
[Authorize]
public sealed class PlatformReserveController : ControllerBase
{
    /// <summary>Most partitions one call may name; a budget list is small by nature.</summary>
    private const int MaximumPartitionsPerCall = 64;

    /// <summary>Longest partition name accepted. The vocabulary is open; its length is not.</summary>
    private const int MaximumPartitionNameLength = 64;

    private readonly IPlatformReserve _reserve;
    private readonly ReserveOptions _options;

    public PlatformReserveController(IPlatformReserve reserve, ReserveOptions options)
    {
        _reserve = reserve ?? throw new ArgumentNullException(nameof(reserve));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Declares (or widens) named partitions on one asset. Idempotent on the amounts: a
    /// re-declaration SETS a partition's budget rather than adding to it, so a redelivered
    /// call leaves the budget exactly where it was.
    /// </summary>
    [HttpPost("{assetId}/partitions")]
    [EnableRateLimiting("financial")]
    [ProducesResponseType(typeof(AZOAResult<ReservePartitionsView>), 200)]
    public async Task<ActionResult<AZOAResult<ReservePartitionsView>>> Declare(
        string assetId,
        [FromQuery] string? chain,
        [FromBody] ReservePartitionDeclarationBody body,
        CancellationToken ct)
    {
        if (body is null)
            return BadRequest(Refuse("A request body is required.", AzoaErrorCodes.InvalidRequest));

        var authority = RequireDeclarationAuthority();
        if (authority.Refusal is { } refusal) return refusal;

        var rail = ResolveRail(chain, body.Chain);
        if (rail.Error is { } railError)
            return BadRequest(Refuse(railError, AzoaErrorCodes.InvalidRequest));

        var budgets = ReadBudgets(body.Partitions, out var budgetError);
        if (budgets is null)
            return BadRequest(Refuse(budgetError, AzoaErrorCodes.InvalidRequest));

        var declared = await _reserve.DeclarePartitionsAsync(
            rail.Chain!, assetId, budgets, authority.Authority!, ct);

        if (declared.IsError || declared.Result is null)
            return Reject(declared);

        return Render(declared.Result, declared.Message);
    }

    /// <summary>
    /// What an asset's partitions currently stand at. Scoped by the same predicate the
    /// declaration is: a budget is only visible to whoever may carve it.
    /// </summary>
    [HttpGet("{assetId}/partitions")]
    [ProducesResponseType(typeof(AZOAResult<ReservePartitionsView>), 200)]
    public async Task<ActionResult<AZOAResult<ReservePartitionsView>>> Get(
        string assetId, [FromQuery] string? chain, CancellationToken ct)
    {
        var authority = RequireDeclarationAuthority();
        if (authority.Refusal is { } refusal) return refusal;

        var rail = ResolveRail(chain, null);
        if (rail.Error is { } railError)
            return BadRequest(Refuse(railError, AzoaErrorCodes.InvalidRequest));

        var ledger = await _reserve.GetLedgerAsync(rail.Chain!, assetId, ct);
        if (ledger.IsError || ledger.Result is null)
            return Reject(ledger);

        // The read asks the write's question — "is this budget yours?" — rather than a looser
        // one of its own, so a caller can never see a partition it could not have declared.
        var scopeRefusal = ReservePartitionScope.RefuseDeclaration(
            ledger.Result, authority.Authority, ledger.Result.AssetId, _options);
        if (scopeRefusal is not null)
            return StatusCode(
                StatusCodes.Status403Forbidden, Refuse(scopeRefusal, AzoaErrorCodes.Forbidden));

        return Render(ledger.Result, "Success");
    }

    // ── Authority ─────────────────────────────────────────────────────────────

    /// <summary>The authority a declaration runs on, or the refusal that stops it.</summary>
    private sealed record DeclarationAuthority(
        ReserveDrawAuthority? Authority,
        ActionResult<AZOAResult<ReservePartitionsView>>? Refusal);

    /// <summary>
    /// Resolves who is declaring, from the PRINCIPAL alone. Two lanes and nothing else: the
    /// node operator on a JWT, or the tenant that owns the API key. A plain signed-in user
    /// matches neither and is refused.
    /// </summary>
    private DeclarationAuthority RequireDeclarationAuthority()
    {
        var isApiKey = string.Equals(
            User.FindFirst("AuthMethod")?.Value, "ApiKey", StringComparison.OrdinalIgnoreCase);

        if (!isApiKey)
        {
            // Lane 1 — the node operator. These are the three axes of the "Operator"
            // authorization policy (Program.cs), asked inline because this route also has a
            // tenant lane and attaching the policy would close it. An API-key principal can
            // never arrive here: it was excluded above, and ApiKeyAuthenticationHandler
            // strips operator:admin from a key's claims regardless of its stored CSV.
            var isNodeOperator =
                AzoaClaims.IsNodeOperator(User) &&
                User.HasScope(AzoaScopes.Operator) &&
                User.GetActingTenantId() is null;

            if (isNodeOperator)
                return new DeclarationAuthority(ReserveDrawAuthority.NodeOperator, null);

            return new DeclarationAuthority(null, StatusCode(
                StatusCodes.Status403Forbidden,
                Refuse(
                    "Declaring a reserve partition needs either the node operator, or the tenant that " +
                    $"registered the asset presenting an API key carrying the '{AzoaScopes.TokenCreateSign}' " +
                    "scope.",
                    AzoaErrorCodes.Forbidden)));
        }

        // Lane 2 — a tenant. AZOA has no separate Tenant entity: a tenant IS the user that
        // owns the key, which is why the id is read off the key's subject and no request
        // field participates.
        var caller = GetUserIdFromClaims();
        if (caller is null)
            return new DeclarationAuthority(null, Unauthorized(
                Refuse("Invalid token.", AzoaErrorCodes.Forbidden)));

        if (!User.HasScope(AzoaScopes.TokenCreateSign))
            return new DeclarationAuthority(null, StatusCode(
                StatusCodes.Status403Forbidden,
                Refuse(
                    $"Caller lacks the '{AzoaScopes.TokenCreateSign}' scope required to declare a reserve " +
                    "partition.",
                    AzoaErrorCodes.Forbidden)));

        // The allocation and hold tripwire: a principal carrying an acting-tenant claim that
        // disagrees with its own subject presents two candidates, and that is a refusal
        // rather than a coin flip.
        var claimedTenantId = User.GetActingTenantId();
        if (claimedTenantId is { } fromClaim && fromClaim != caller.Value)
            return new DeclarationAuthority(null, StatusCode(
                StatusCodes.Status403Forbidden,
                Refuse(
                    "The partition-declaration principal names two different acting tenants.",
                    AzoaErrorCodes.Forbidden)));

        return new DeclarationAuthority(ReserveDrawAuthority.ForTenant(caller.Value), null);
    }

    // ── Request reading ───────────────────────────────────────────────────────

    /// <summary>Which ledger row a request addresses, or why it could not be decided.</summary>
    private sealed record RailChoice(string? Chain, string? Error);

    /// <summary>
    /// The rail the asset lives on. It is a LOCATOR — one ledger row exists per chain,
    /// network and asset — so it may come from the query string or the body, but the two
    /// disagreeing is a refusal rather than a preference. With neither supplied, only a node
    /// whose operator declared exactly one chain can answer, and it answers from that
    /// configuration rather than from a guess.
    /// </summary>
    private RailChoice ResolveRail(string? fromQuery, string? fromBody)
    {
        var queryChain = fromQuery?.Trim();
        var bodyChain = fromBody?.Trim();

        if (!string.IsNullOrEmpty(queryChain) && !string.IsNullOrEmpty(bodyChain) &&
            !string.Equals(queryChain, bodyChain, StringComparison.OrdinalIgnoreCase))
        {
            return new RailChoice(null,
                "This request names two different rails; remove one of them so the reserve ledger row " +
                "it addresses is unambiguous.");
        }

        var chosen = !string.IsNullOrEmpty(queryChain) ? queryChain : bodyChain;
        if (!string.IsNullOrEmpty(chosen))
            return new RailChoice(chosen, null);

        // OI-74: the same effective set the reserve counter enforces, never Chains raw --
        // otherwise this door and PlatformReserveService.PermitsChain disagree about what
        // "exactly one chain" means on a node that declared none.
        var approved = _options.EffectiveChains;

        if (approved.Count == 1)
            return new RailChoice(approved[0], null);

        return new RailChoice(null,
            "This request must name the rail the asset lives on (the 'chain' query parameter): this node " +
            "does not hold reserve assets on exactly one chain, so the ledger row cannot be chosen for " +
            "the caller.");
    }

    /// <summary>
    /// Reads the declared budgets, or explains what is wrong with them. Amounts are whole
    /// base-unit decimal strings and nothing else: <c>NumberStyles.None</c> refuses a sign,
    /// a separator and surrounding whitespace, so a negative budget cannot reach the ledger.
    /// </summary>
    internal static Dictionary<string, BigInteger>? ReadBudgets(
        IDictionary<string, string>? partitions, out string error)
    {
        error = string.Empty;

        if (partitions is null || partitions.Count == 0)
        {
            error = "Name at least one partition and the budget to declare for it.";
            return null;
        }

        if (partitions.Count > MaximumPartitionsPerCall)
        {
            error = $"A declaration may name at most {MaximumPartitionsPerCall} partitions at once.";
            return null;
        }

        var budgets = new Dictionary<string, BigInteger>(StringComparer.Ordinal);
        foreach (var (rawName, rawBudget) in partitions)
        {
            var name = ReservePartitionCodec.Normalize(rawName);

            if (string.IsNullOrEmpty(name))
            {
                error = "A partition name cannot be blank.";
                return null;
            }

            if (name.Length > MaximumPartitionNameLength)
            {
                error =
                    $"Partition name '{name}' is longer than {MaximumPartitionNameLength} characters.";
                return null;
            }

            // Partition names are normalised in ONE place, so "Contributor" and "contributor"
            // are the SAME budget. Two spellings of one partition in a single call would
            // silently keep whichever the dictionary iterated last, so refuse both.
            if (budgets.ContainsKey(name))
            {
                error =
                    $"This request names partition '{name}' more than once. Partition names are " +
                    "case-insensitive, so one of the budgets would have been discarded.";
                return null;
            }

            if (!BigInteger.TryParse(
                    rawBudget, NumberStyles.None, CultureInfo.InvariantCulture, out var budget))
            {
                error =
                    $"Partition '{name}' needs its budget as a whole, non-negative base-unit count in a " +
                    "decimal string.";
                return null;
            }

            budgets[name] = budget;
        }

        return budgets;
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Projects a ledger row. A partitions blob that will not decode is an error, never an
    /// empty budget list — rendering unreadable partitions as "nothing declared" is how a
    /// caller would be told the whole supply is free.
    /// </summary>
    private ActionResult<AZOAResult<ReservePartitionsView>> Render(
        PlatformReserveLedger row, string message)
    {
        var partitions = ReservePartitionCodec.Decode(row.Partitions, out var decodeError);
        if (partitions is null)
            return BadRequest(Refuse(decodeError, AzoaErrorCodes.DependencyUnavailable));

        return Ok(AZOAResult<ReservePartitionsView>.Success(
            ReservePartitionsView.From(row, partitions), message));
    }

    /// <summary>
    /// Maps a reserve failure onto its status. The service stamps the code, so the status
    /// never depends on matching an error message. An absent asset answers 404 rather than
    /// the collapsed "no such thing" a hold id gets: an on-chain asset id is public state,
    /// not a caller's private reference, so there is nothing to leak by admitting it is gone.
    /// </summary>
    private ActionResult<AZOAResult<ReservePartitionsView>> Reject(
        AZOAResult<PlatformReserveLedger> outcome)
    {
        var failure = AZOAResult<ReservePartitionsView>.FailureWithCode(
            outcome.Message, outcome.Code ?? AzoaErrorCodes.InvalidRequest);

        if (string.Equals(outcome.Code, AzoaErrorCodes.Forbidden, StringComparison.Ordinal))
            return StatusCode(StatusCodes.Status403Forbidden, failure);

        if (string.Equals(outcome.Code, AzoaErrorCodes.NotFound, StringComparison.Ordinal))
            return NotFound(failure);

        return BadRequest(failure);
    }

    private static AZOAResult<ReservePartitionsView> Refuse(string message, string code) =>
        AZOAResult<ReservePartitionsView>.FailureWithCode(message, code);

    private Guid? GetUserIdFromClaims()
    {
        var raw = User.FindFirst("UserId")?.Value
                  ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                  ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}

/// <summary>
/// Body of a partition declaration. There is deliberately NO tenant field: the acting tenant
/// is the API key's own subject, and a body that could name one would let a tenant carve
/// another tenant's budget.
/// </summary>
public sealed class ReservePartitionDeclarationBody
{
    /// <summary>Partition name to its budget, as a whole base-unit decimal string.</summary>
    public Dictionary<string, string>? Partitions { get; set; }

    /// <summary>
    /// Rail the asset lives on, when it is not supplied as the <c>chain</c> query parameter.
    /// A locator for the ledger row and nothing more — it carries no authority.
    /// </summary>
    public string? Chain { get; set; }
}

/// <summary>
/// Where an asset's partitions stand. It carries NO reserve address and no key material: a
/// caller needs to know what a budget is, not which account holds it.
/// </summary>
public sealed record ReservePartitionsView(
    string AssetId,
    string Chain,
    string Network,
    string UnitName,
    int Decimals,
    string TotalSupply,
    long Version,
    IReadOnlyList<ReservePartitionView> Partitions)
{
    /// <summary>Projects a ledger row and its already-decoded partitions onto the wire shape.</summary>
    public static ReservePartitionsView From(
        PlatformReserveLedger row, IReadOnlyDictionary<string, ReservePartitionCounters> partitions) =>
        new(row.AssetId,
            row.Chain,
            row.Network,
            row.UnitName,
            row.Decimals,
            row.TotalSupply,
            row.Version,
            partitions
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => ReservePartitionView.From(entry.Key, entry.Value))
                .ToList());
}

/// <summary>One partition's budget and what it has committed, all as exact decimal strings.</summary>
public sealed record ReservePartitionView(
    string Name,
    string Declared,
    string Issued,
    string Held,
    string Returned,
    string Available)
{
    /// <summary>Projects one partition's counters onto the wire shape.</summary>
    public static ReservePartitionView From(string name, ReservePartitionCounters counters) =>
        new(name,
            counters.Declared.ToString(CultureInfo.InvariantCulture),
            counters.Issued.ToString(CultureInfo.InvariantCulture),
            counters.Held.ToString(CultureInfo.InvariantCulture),
            counters.Returned.ToString(CultureInfo.InvariantCulture),
            counters.Available.ToString(CultureInfo.InvariantCulture));
}
