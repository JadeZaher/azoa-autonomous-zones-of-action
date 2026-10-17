// SPDX-License-Identifier: UNLICENSED

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Reserve;

namespace AZOA.WebAPI.Controllers;

/// <summary>What a tenant posts to get one of its projects an account for funds to land in.</summary>
/// <remarks>
/// There is deliberately NO tenant field on this body. The tenant is the calling credential's
/// own identity and nothing a caller writes can name a different one; adding a tenant property
/// here would turn an idempotent provisioning call into a way to reach every other project's
/// treasury on the node.
/// </remarks>
public sealed class TenantProjectTreasuryResolveRequest
{
    /// <summary>Chain the treasury lives on, as the provider spells it.</summary>
    public string ChainType { get; set; } = string.Empty;

    /// <summary>The tenant's own opaque project id. Never parsed, only scoped.</summary>
    public string ProjectReference { get; set; } = string.Empty;
}

/// <summary>The account a project's funding lands in, as the tenant that owns it sees it.</summary>
/// <remarks>
/// <c>TreasuryAddress</c> is a raw chain address and is MACHINE-FACING ONLY — it exists so a
/// node client can record where it sent funds. It must never be forwarded to a surface a
/// sponsor reads: OI-89 strips the address from the consumer-facing figures entirely, and a
/// person is shown amounts and dates, never an address. Maps onto the node-gateway contract
/// as <c>chainType / projectReference / treasuryAddress / status</c>.
/// </remarks>
public sealed record TenantProjectTreasuryResponse(
    string ChainType,
    string ProjectReference,
    string TreasuryAddress,
    string Status);

/// <summary>
/// The tenant-scoped half of project treasury setup: a tenant resolves its OWN project's
/// custodial account without an operator credential. This is what a node client calls at
/// publish time, between creating a project's stake and declaring its allowances.
/// <para>
/// It is deliberately a separate controller from <c>Admin/ProjectTreasuryController</c>, which
/// keeps its <c>Operator</c> policy and its read/close surface. Widening that one would have
/// handed an API key the operator console; this one can only ever address the caller's own
/// tenant, and exposes no list, no close, and no way to move a unit.
/// </para>
/// <para>
/// Ownership is enforced in two places and neither is prose: the reference is built from the
/// PRINCIPAL's tenant id (<c>TryResolveTenant</c>), and <c>ProjectTreasuryService</c>'s
/// <c>GuardRowOwnership</c> refuses any row whose recorded tenant disagrees — at the read, so
/// this door is not the only thing standing between two tenants' money.
/// </para>
/// </summary>
[ApiController]
[Route("api/projects/treasury")]
[Authorize(Policy = "TenantScope")]
public sealed class ProjectTreasuryTenantController : ControllerBase
{
    private readonly IProjectTreasury _treasuries;

    public ProjectTreasuryTenantController(IProjectTreasury treasuries)
    {
        _treasuries = treasuries ?? throw new ArgumentNullException(nameof(treasuries));
    }

    /// <summary>
    /// Resolves — deriving and recording it on the first call — the treasury account for one
    /// of the calling tenant's projects. Idempotent: a second call returns the same account.
    /// </summary>
    [HttpPost("resolve")]
    [EnableRateLimiting("financial")]
    [ProducesResponseType(typeof(AZOAResult<TenantProjectTreasuryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AZOAResult<TenantProjectTreasuryResponse>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AZOAResult<TenantProjectTreasuryResponse>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(AZOAResult<TenantProjectTreasuryResponse>), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AZOAResult<TenantProjectTreasuryResponse>>> Resolve(
        [FromBody] TenantProjectTreasuryResolveRequest request, CancellationToken ct)
    {
        if (request is null)
            return BadRequest(AZOAResult<TenantProjectTreasuryResponse>.FailureWithCode(
                "A treasury resolve request is required.", AzoaErrorCodes.InvalidRequest));

        if (!TryResolveTenant(out var tenantId, out var refusal))
            return refusal!;

        if (string.IsNullOrWhiteSpace(request.ChainType))
            return BadRequest(AZOAResult<TenantProjectTreasuryResponse>.FailureWithCode(
                "A chain is required to resolve a project treasury.", AzoaErrorCodes.InvalidRequest));

        if (string.IsNullOrWhiteSpace(request.ProjectReference))
            return BadRequest(AZOAResult<TenantProjectTreasuryResponse>.FailureWithCode(
                "A project reference is required to resolve a project treasury.",
                AzoaErrorCodes.InvalidRequest));

        // The tenant comes from the principal; the body contributed only the chain and the
        // tenant's own project id. There is no third value a caller could have supplied.
        var reference = new ProjectTreasuryReference(request.ChainType, tenantId, request.ProjectReference);

        var account = await _treasuries.ResolveAsync(reference, ct);
        if (account.IsError || account.Result is null)
            return BadRequest(AZOAResult<TenantProjectTreasuryResponse>.FailureWithCode(
                account.Message, AzoaErrorCodes.InvalidRequest));

        // Second of the two ownership checks (the first is inside the service, at the read). A
        // resolve that somehow returned another tenant's row must produce nothing at all, not
        // a body a caller can read.
        if (account.Result.TenantId != tenantId)
            return StatusCode(StatusCodes.Status403Forbidden,
                AZOAResult<TenantProjectTreasuryResponse>.FailureWithCode(
                    "That project treasury is not this tenant's.", AzoaErrorCodes.Forbidden));

        // A retired treasury refuses every movement. Handing one back would let a caller record
        // it as the account this project's funding lands in, and the funding would then have
        // nowhere to go while the project reads as fully set up.
        if (account.Result.Status != ProjectTreasuryStatus.Open)
            return Conflict(AZOAResult<TenantProjectTreasuryResponse>.FailureWithCode(
                $"This project's treasury is {account.Result.Status} and accepts no funds. "
                + "An operator must retire the project or replace it before funding setup continues.",
                AzoaErrorCodes.Conflict));

        return Ok(AZOAResult<TenantProjectTreasuryResponse>.Success(
            new TenantProjectTreasuryResponse(
                account.Result.ChainType,
                account.Result.ProjectReference,
                account.Result.Address,
                account.Result.Status.ToString()),
            account.Message));
    }

    // ─── authorisation ───

    /// <summary>
    /// Resolves the tenant this call acts for, or refuses. The tenant is the credential's own
    /// subject: an API key acts as its owner, and a principal that names a DIFFERENT acting
    /// tenant is refused rather than resolved to one of the two.
    /// </summary>
    private bool TryResolveTenant(
        out Guid tenantId,
        out ActionResult<AZOAResult<TenantProjectTreasuryResponse>>? refusal)
    {
        tenantId = Guid.Empty;
        refusal = null;

        var caller = GetUserIdFromClaims();
        if (caller is null || caller.Value == Guid.Empty)
        {
            refusal = Unauthorized(AZOAResult<TenantProjectTreasuryResponse>.FailureWithCode(
                "The principal carries no resolvable tenant identity.", AzoaErrorCodes.Forbidden));
            return false;
        }

        // Restated in code rather than left to the TenantScope policy alone: the method that
        // builds the reference cannot see an attribute, and this action must stay refused if it
        // is ever re-hosted under a different policy.
        if (!User.HasScope(AzoaScopes.TenantProvision))
        {
            refusal = StatusCode(StatusCodes.Status403Forbidden,
                AZOAResult<TenantProjectTreasuryResponse>.FailureWithCode(
                    $"Caller lacks the '{AzoaScopes.TenantProvision}' scope required to set up a project.",
                    AzoaErrorCodes.Forbidden));
            return false;
        }

        // An API key carries no act_as_tenant claim, so the tenant IS the key's owner — the same
        // resolution FiatCheckoutController and TenantPriceScheduleController perform. A
        // principal that somehow names a DIFFERENT tenant is a refusal, never a coin flip.
        var claimedTenantId = User.GetActingTenantId();
        if (claimedTenantId is { } fromClaim && fromClaim != caller.Value)
        {
            refusal = StatusCode(StatusCodes.Status403Forbidden,
                AZOAResult<TenantProjectTreasuryResponse>.FailureWithCode(
                    "The principal names two different acting tenants.", AzoaErrorCodes.Forbidden));
            return false;
        }

        tenantId = caller.Value;
        return true;
    }

    private Guid? GetUserIdFromClaims()
    {
        var raw = User.FindFirst("UserId")?.Value
                  ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                  ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}
