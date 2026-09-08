// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Numerics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Hold;

namespace AZOA.WebAPI.Controllers;

/// <summary>
/// The tenant-callable HOLD surface (D14 / W3.4): open a hold, release it to its
/// beneficiary, give it back, or continue it as a second hold.
///
/// <para>A hold is how "this is yours in N days" becomes true rather than promised. The
/// units are committed out of a declared reserve partition and sit in the node's
/// custodial holding account until the window elapses; only then can anything pay them
/// out. Nothing here exposes an address, a key, a fee or a chain name to the person the
/// units are held for.</para>
///
/// <para><b>Authority.</b> The write endpoints move — or commit — real value, so they
/// require an API-key principal carrying the transfer-signing scope, exactly as an
/// allocation does. The acting tenant comes from the PRINCIPAL, never from the body, and
/// a release additionally re-checks the beneficiary's live consent at the edge before the
/// custody seam checks it again at the key.</para>
///
/// <para><b>Reads are owner-scoped.</b> A hold is only visible to the person it is held
/// for, the person who funded it, or the tenant that drove it — a hold id is a caller's
/// own reference and would otherwise be an enumerable window onto other people's money.</para>
/// </summary>
[ApiController]
[Route("api/holds")]
[Authorize]
public sealed class HoldController : ControllerBase
{
    private const int DefaultListLimit = 100;

    private readonly IPlatformHold _holds;
    private readonly ITenantConsentGate? _consentGate;

    /// <param name="consentGate">The live consent check (registered in <c>Program.cs</c>).
    /// Optional only so the type still constructs without a container; a release that
    /// reaches the tenant-driven path with no gate is REFUSED, never waved through.</param>
    public HoldController(IPlatformHold holds, ITenantConsentGate? consentGate = null)
    {
        _holds = holds ?? throw new ArgumentNullException(nameof(holds));
        _consentGate = consentGate;
    }

    /// <summary>
    /// Opens a hold, or returns the one this hold id already opened. Idempotent on the
    /// caller's own hold id: a redelivered request returns the existing hold and commits
    /// nothing twice, and the same id with different terms is refused rather than
    /// silently reinterpreted.
    /// </summary>
    [HttpPost]
    [EnableRateLimiting("financial")]
    [ProducesResponseType(typeof(AZOAResult<HoldView>), 200)]
    public async Task<ActionResult<AZOAResult<HoldView>>> Open(
        [FromBody] HoldOpenBody body, CancellationToken ct)
    {
        if (body is null)
            return BadRequest(Refuse("A request body is required."));

        var authority = RequireValueAuthority();
        if (authority.Refusal is { } refusal) return refusal;

        if (!BigInteger.TryParse(
                body.Amount, NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
        {
            return BadRequest(Refuse("Amount must be a whole base-unit count as a decimal string."));
        }

        var opened = await _holds.OpenAsync(
            new HoldOpenRequest(
                new HoldReference(body.Chain ?? string.Empty, body.HoldId ?? string.Empty),
                body.AssetId ?? string.Empty,
                amount,
                HoldFundingSource.Reserve,
                body.BeneficiaryUserId,
                ReservePartition: body.ReservePartition,
                FunderUserId: Guid.Empty,
                ReleaseNotBefore: body.ReleaseNotBefore,
                AutoRelease: body.AutoRelease,
                // The acting tenant is the principal's, never the body's: a body-supplied
                // tenant would let one tenant draw another's declared partition.
                ActingTenantId: authority.ActingTenantId,
                Purpose: body.Purpose),
            ct);

        return Project(opened);
    }

    /// <summary>
    /// Releases a due hold to its beneficiary. Refused before the window elapses, and
    /// refused when the beneficiary has not consented to this tenant acting for them.
    /// </summary>
    [HttpPost("{chain}/{holdId}/release")]
    [EnableRateLimiting("financial")]
    [ProducesResponseType(typeof(AZOAResult<HoldView>), 200)]
    public async Task<ActionResult<AZOAResult<HoldView>>> Release(
        string chain, string holdId, [FromBody] HoldSettlementBody? body, CancellationToken ct)
    {
        var authority = RequireValueAuthority();
        if (authority.Refusal is { } refusal) return refusal;

        var reference = new HoldReference(chain, holdId);

        var existing = await _holds.GetAsync(reference, ct);
        if (existing.IsError || existing.Result is null)
            return NotFound(Refuse(existing.Message));

        var visibility = GuardVisibility(existing.Result, authority);
        if (visibility is { } denied) return denied;

        // Belt and braces, the allocation precedent: the release signs the beneficiary's
        // acceptance with the beneficiary's own key, so their live grant is checked here
        // before anything happens AND again inside the custody seam at the decrypt.
        var consentRefusal = await RequireLiveConsentAsync(existing.Result, authority.ActingTenantId, ct);
        if (consentRefusal is { } denial) return denial;

        var released = await _holds.ReleaseAsync(
            new HoldSettlementRequest(reference, authority.ActingTenantId, body?.Reason), ct);

        return Project(released);
    }

    /// <summary>
    /// Gives a hold back: the commitment returns to the partition it was drawn from and
    /// nothing moves, because the units never left the holding account.
    /// </summary>
    [HttpPost("{chain}/{holdId}/refund")]
    [EnableRateLimiting("financial")]
    [ProducesResponseType(typeof(AZOAResult<HoldView>), 200)]
    public async Task<ActionResult<AZOAResult<HoldView>>> Refund(
        string chain, string holdId, [FromBody] HoldSettlementBody? body, CancellationToken ct)
    {
        var authority = RequireValueAuthority();
        if (authority.Refusal is { } refusal) return refusal;

        var reference = new HoldReference(chain, holdId);

        var existing = await _holds.GetAsync(reference, ct);
        if (existing.IsError || existing.Result is null)
            return NotFound(Refuse(existing.Message));

        var visibility = GuardVisibility(existing.Result, authority);
        if (visibility is { } denied) return denied;

        var refunded = await _holds.RefundAsync(
            new HoldSettlementRequest(reference, authority.ActingTenantId, body?.Reason), ct);

        return Project(refunded);
    }

    /// <summary>
    /// Continues a hold as a second hold over the same units — what approving completed
    /// work is. The next window may never be earlier than the one it continues.
    /// </summary>
    [HttpPost("{chain}/{holdId}/release-into-hold")]
    [EnableRateLimiting("financial")]
    [ProducesResponseType(typeof(AZOAResult<HoldView>), 200)]
    public async Task<ActionResult<AZOAResult<HoldView>>> ReleaseIntoHold(
        string chain, string holdId, [FromBody] HoldChainBody body, CancellationToken ct)
    {
        if (body is null)
            return BadRequest(Refuse("A request body is required."));

        var authority = RequireValueAuthority();
        if (authority.Refusal is { } refusal) return refusal;

        var reference = new HoldReference(chain, holdId);

        var existing = await _holds.GetAsync(reference, ct);
        if (existing.IsError || existing.Result is null)
            return NotFound(Refuse(existing.Message));

        var visibility = GuardVisibility(existing.Result, authority);
        if (visibility is { } denied) return denied;

        var continued = await _holds.ReleaseIntoHoldAsync(
            new HoldChainRequest(
                reference,
                body.NextHoldId ?? string.Empty,
                body.NextBeneficiaryUserId,
                ReleaseNotBefore: body.ReleaseNotBefore,
                AutoRelease: body.AutoRelease,
                ActingTenantId: authority.ActingTenantId,
                Purpose: body.Purpose),
            ct);

        return Project(continued);
    }

    /// <summary>One hold, visible only to the people and the tenant it concerns.</summary>
    [HttpGet("{chain}/{holdId}")]
    [ProducesResponseType(typeof(AZOAResult<HoldView>), 200)]
    public async Task<ActionResult<AZOAResult<HoldView>>> Get(
        string chain, string holdId, CancellationToken ct)
    {
        var caller = GetUserIdFromClaims();
        if (caller is null) return Unauthorized(Refuse("Invalid token."));

        var hold = await _holds.GetAsync(new HoldReference(chain, holdId), ct);
        if (hold.IsError || hold.Result is null)
            return NotFound(Refuse(hold.Message));

        var visibility = GuardVisibility(
            hold.Result, new ValueAuthority(caller.Value, User.GetActingTenantId(), null));
        if (visibility is { } denied) return denied;

        return Ok(AZOAResult<HoldView>.Success(HoldView.From(hold.Result), "Success"));
    }

    /// <summary>Every hold the authenticated person funds or benefits from, newest first.</summary>
    [HttpGet("mine")]
    [ProducesResponseType(typeof(AZOAResult<IReadOnlyList<HoldView>>), 200)]
    public async Task<ActionResult<AZOAResult<IReadOnlyList<HoldView>>>> Mine(
        [FromQuery] int? limit, CancellationToken ct)
    {
        var caller = GetUserIdFromClaims();
        if (caller is null)
            return Unauthorized(AZOAResult<IReadOnlyList<HoldView>>.Failure("Invalid token."));

        // Scoped to the token's own subject; no query parameter can widen it.
        var rows = await _holds.ListForUserAsync(caller.Value, limit ?? DefaultListLimit, ct);
        if (rows.IsError || rows.Result is null)
            return BadRequest(AZOAResult<IReadOnlyList<HoldView>>.Failure(rows.Message));

        return Ok(AZOAResult<IReadOnlyList<HoldView>>.Success(
            rows.Result.Select(HoldView.From).ToList(), "Success"));
    }

    // ── Authority + visibility ────────────────────────────────────────────────

    /// <summary>The caller, their acting tenant, and the refusal when they may not move value.</summary>
    private sealed record ValueAuthority(
        Guid CallerUserId, Guid? ActingTenantId, ActionResult<AZOAResult<HoldView>>? Refusal);

    /// <summary>
    /// Committing or moving held value needs the same authority an allocation needs: an
    /// API-key principal carrying the transfer-signing scope. The acting tenant is derived
    /// from the principal alone (a tenant IS the user that owns the key), so no request
    /// field can nominate a different one.
    /// </summary>
    private ValueAuthority RequireValueAuthority()
    {
        if (!string.Equals(User.FindFirst("AuthMethod")?.Value, "ApiKey", StringComparison.OrdinalIgnoreCase))
        {
            return new ValueAuthority(Guid.Empty, null, StatusCode(
                StatusCodes.Status403Forbidden,
                Refuse("Holding value requires an API-key principal.")));
        }

        var caller = GetUserIdFromClaims();
        if (caller is null)
            return new ValueAuthority(Guid.Empty, null, Unauthorized(Refuse("Invalid token.")));

        if (!User.HasScope(AzoaScopes.TransferSign))
        {
            return new ValueAuthority(Guid.Empty, null, StatusCode(
                StatusCodes.Status403Forbidden,
                Refuse($"Caller lacks the '{AzoaScopes.TransferSign}' scope required to hold value.")));
        }

        // The tripwire the allocation surface uses: a principal that carries an acting
        // tenant claim disagreeing with its own subject presents two candidates, and that
        // is a refusal rather than a coin flip.
        var claimed = User.GetActingTenantId();
        if (claimed is { } fromClaim && fromClaim != caller.Value)
        {
            return new ValueAuthority(Guid.Empty, null, StatusCode(
                StatusCodes.Status403Forbidden,
                Refuse("The hold principal names two different acting tenants.")));
        }

        return new ValueAuthority(caller.Value, caller.Value, null);
    }

    /// <summary>
    /// Null when this caller may see and act on the hold. A hold id is the caller's own
    /// reference, so without this an id would be an enumerable window onto whatever
    /// another tenant is holding for whom.
    /// </summary>
    private ActionResult<AZOAResult<HoldView>>? GuardVisibility(HoldLedger hold, ValueAuthority authority)
    {
        var beneficiary = ParseUserId(hold.BeneficiaryUserId);
        var funder = ParseUserId(hold.FunderUserId);
        var owningTenant = ParseUserId(hold.TenantId);

        var permitted =
            (authority.CallerUserId != Guid.Empty &&
             (authority.CallerUserId == beneficiary || authority.CallerUserId == funder)) ||
            (owningTenant != Guid.Empty && authority.ActingTenantId == owningTenant);

        if (permitted) return null;

        // Deliberately the same shape as "no such hold": confirming a hold exists to
        // someone with no relationship to it is itself the leak.
        return NotFound(Refuse($"No hold '{hold.HoldId}' exists on '{hold.Chain}'."));
    }

    /// <summary>
    /// Refuses a tenant-driven release unless the beneficiary has a LIVE grant to this
    /// tenant covering the scope the release will sign under. A user-driven release needs
    /// no grant and passes straight through.
    /// </summary>
    private async Task<ActionResult<AZOAResult<HoldView>>?> RequireLiveConsentAsync(
        HoldLedger hold, Guid? actingTenantId, CancellationToken ct)
    {
        if (actingTenantId is not { } tenantId || tenantId == Guid.Empty)
            return null;

        var beneficiary = ParseUserId(hold.BeneficiaryUserId);
        if (beneficiary == Guid.Empty)
            return BadRequest(Refuse("This hold names no readable beneficiary."));

        if (_consentGate is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                Refuse("Consent cannot be verified; the release was refused."));

        var probe = new SigningContext(beneficiary, Guid.Empty, IsPlatform: false)
            .ActingAs(tenantId, AzoaScopes.TransferSign, beneficiary);

        var consent = await _consentGate.EnsureAllowedAsync(probe, ct);
        if (consent.IsError)
            return StatusCode(StatusCodes.Status403Forbidden, Refuse(consent.Message));

        return null;
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private ActionResult<AZOAResult<HoldView>> Project(AZOAResult<HoldOutcome> outcome)
    {
        if (outcome.IsError || outcome.Result is null)
            return BadRequest(AZOAResult<HoldView>.Failure(outcome.Message));

        return Ok(AZOAResult<HoldView>.Success(HoldView.From(outcome.Result), outcome.Message));
    }

    private static AZOAResult<HoldView> Refuse(string message) => AZOAResult<HoldView>.Failure(message);

    private Guid? GetUserIdFromClaims()
    {
        var raw = User.FindFirst("UserId")?.Value
                  ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                  ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    /// <summary>A user id off a row; a record link keeps its <c>user:</c> prefix, so strip it.</summary>
    private static Guid ParseUserId(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return Guid.Empty;

        var text = userId.Trim();
        var colon = text.LastIndexOf(':');
        if (colon >= 0 && colon < text.Length - 1)
            text = text[(colon + 1)..];

        return Guid.TryParseExact(text, "N", out var parsed) ? parsed : Guid.Empty;
    }
}

/// <summary>Body of an open call. The acting tenant is never a body field.</summary>
public sealed class HoldOpenBody
{
    /// <summary>Rail the hold settles on.</summary>
    public string? Chain { get; set; }

    /// <summary>The caller's own hold id — the idempotency anchor.</summary>
    public string? HoldId { get; set; }

    /// <summary>Asset being held.</summary>
    public string? AssetId { get; set; }

    /// <summary>Amount in the asset's base units, as a whole decimal string.</summary>
    public string? Amount { get; set; }

    /// <summary>Declared reserve partition the commitment is drawn from.</summary>
    public string? ReservePartition { get; set; }

    /// <summary>Who the units are held for.</summary>
    public Guid BeneficiaryUserId { get; set; }

    /// <summary>The hold window; absent means releasable immediately.</summary>
    public DateTimeOffset? ReleaseNotBefore { get; set; }

    /// <summary>Whether the hosted sweep may release it once due.</summary>
    public bool AutoRelease { get; set; } = true;

    /// <summary>The caller's own reason for the hold.</summary>
    public string? Purpose { get; set; }
}

/// <summary>Body of a release or refund call.</summary>
public sealed class HoldSettlementBody
{
    /// <summary>Why the hold is being settled; recorded on the ledger row.</summary>
    public string? Reason { get; set; }
}

/// <summary>Body of a release-into-hold call.</summary>
public sealed class HoldChainBody
{
    /// <summary>Id of the hold that continues this one.</summary>
    public string? NextHoldId { get; set; }

    /// <summary>Who the units are held for next.</summary>
    public Guid NextBeneficiaryUserId { get; set; }

    /// <summary>The next window; may never be earlier than the one it continues.</summary>
    public DateTimeOffset? ReleaseNotBefore { get; set; }

    /// <summary>Whether the hosted sweep may release the next hold once it is due.</summary>
    public bool AutoRelease { get; set; } = true;

    /// <summary>The caller's own reason for the next hold.</summary>
    public string? Purpose { get; set; }
}

/// <summary>
/// Wire view of a hold. It deliberately carries NO address and NO rail reference: the
/// person a hold is for needs to know how much is theirs and when, not which account it
/// is parked in or what the chain called the payment.
/// </summary>
public sealed record HoldView(
    string HoldId,
    string Chain,
    string AssetId,
    string Amount,
    string ReservePartition,
    string FundingSource,
    string BeneficiaryUserId,
    string? FunderUserId,
    string State,
    DateTimeOffset? ReleaseNotBefore,
    bool AutoRelease,
    string? ParentHoldId,
    string? ChildHoldId,
    string? Purpose,
    DateTimeOffset? SettledAt,
    string? SettlementReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Projects a persisted row onto the wire shape.</summary>
    public static HoldView From(HoldLedger row) =>
        new(row.HoldId,
            row.Chain,
            row.AssetId,
            row.Amount,
            row.ReservePartition,
            row.FundingSource,
            row.BeneficiaryUserId,
            row.FunderUserId,
            row.State,
            row.ReleaseNotBefore,
            row.AutoRelease,
            row.ParentHoldId,
            row.ChildHoldId,
            row.Purpose,
            row.SettledAt,
            row.SettlementReason,
            row.CreatedAt,
            row.UpdatedAt);

    /// <summary>Projects an outcome, preferring the hold the call addressed.</summary>
    public static HoldView From(HoldOutcome outcome) => From(outcome.Hold);
}
