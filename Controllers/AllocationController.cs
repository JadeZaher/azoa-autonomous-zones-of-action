// SPDX-License-Identifier: UNLICENSED

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Controllers;

/// <summary>
/// The tenant-callable wallet-provision + asset-allocation seam. A
/// fiat-settlement tenant authenticates with its <c>X-Api-Key</c> (admitted by
/// the JWT-or-ApiKey multi-scheme policy via <c>[Authorize]</c>) and calls this
/// AFTER money has cleared on its own platform.
///
/// This controller holds NO payment-provider secret, runs NO checkout, and
/// exposes NO webhook handler — it trusts the tenant via the API key, not via any
/// payment-provider signature. Product economics stay in the tenant; AZOA
/// receives its gross amount and applies the node's configured fee policy.
///
/// <para><b>Consent (W3.6).</b> Every allocation here is driven by a tenant on
/// another person's behalf, so it needs that person's live consent. The check runs
/// TWICE by design: once here, at the edge, before any side effect (wallet
/// generation, holon write, idempotency claim), and again inside
/// <c>KeyCustodyService</c> immediately before the key decrypt. The deep seam alone
/// is not enough — it only fires if a provider actually reaches custody, so a
/// stubbed or short-circuiting provider would slip past it. The edge check alone is
/// not enough either — it cannot see what the provider ultimately signs. Both, or
/// neither is a control.</para>
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AllocationController : ControllerBase
{
    private readonly IAllocationManager _allocationManager;
    private readonly ITenantConsentGate? _consentGate;

    /// <param name="consentGate">The live consent check (registered in <c>Program.cs</c>).
    /// Optional only so the type still constructs without a container; a request that
    /// reaches the tenant-driven path with no gate is REFUSED, never waved through.</param>
    public AllocationController(IAllocationManager allocationManager, ITenantConsentGate? consentGate = null)
    {
        _allocationManager = allocationManager;
        _consentGate = consentGate;
    }

    /// <summary>
    /// Provision-if-absent + allocate an already-decided amount of an asset into
    /// the target user's custodial wallet. Idempotent on the
    /// <c>Idempotency-Key</c> header (partitioned by the caller's API key), so a
    /// redelivered fiat webhook never double-mints / double-transfers.
    ///
    /// IDOR: the target is the <paramref name="userId"/> route value; no body
    /// field can redirect the allocation to a different user.
    /// </summary>
    [HttpPost("{userId:guid}")]
    [EnableRateLimiting("financial")]
    public async Task<ActionResult<AZOAResult<AllocationResult>>> Allocate(
        Guid userId, [FromBody] AllocationRequest request)
    {
        if (request is null)
            return BadRequest(AZOAResult<AllocationResult>.FailureWithCode(
                "Allocation request is required.", AzoaErrorCodes.InvalidRequest));

        if (!string.Equals(User.FindFirst("AuthMethod")?.Value, "ApiKey", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                AZOAResult<AllocationResult>.FailureWithCode(
                    "Allocation requires an API-key principal.",
                    AzoaErrorCodes.Forbidden));
        }

        var callerUserId = GetUserIdFromClaims();
        if (callerUserId is null)
            return Unauthorized(new AZOAResult<AllocationResult> { IsError = true, Message = "Invalid token." });

        // The allocation is authorised by the API-key scope, not the body.
        var admittingKeyScopes = AdmittingKeyScopesFor(request.Kind);
        if (!admittingKeyScopes.Any(scope => User.HasScope(scope)))
            return StatusCode(StatusCodes.Status403Forbidden, new AZOAResult<AllocationResult>
            {
                IsError = true,
                Message =
                    $"Caller lacks a scope required to allocate '{request.Kind}' " +
                    $"(one of: {string.Join(", ", admittingKeyScopes)})."
            });

        var apiKeyId = GetApiKeyId();
        if (string.IsNullOrEmpty(apiKeyId))
            return Unauthorized(new AZOAResult<AllocationResult>
            {
                IsError = true,
                Message = "Allocation requires an API-key principal (missing ApiKeyId claim)."
            });

        // Client Idempotency-Key wins; blank ⇒ null ⇒ the manager derives a
        // deterministic content key (never a random per-request key).
        var idempotencyKey = ReadIdempotencyKey();

        var (actingTenantId, tenantError) = ResolveActingTenantId(callerUserId.Value);
        if (actingTenantId is null)
            return StatusCode(StatusCodes.Status403Forbidden,
                AZOAResult<AllocationResult>.FailureWithCode(tenantError!, AzoaErrorCodes.Forbidden));

        // W3.6: the person's live consent, checked BEFORE the manager does anything.
        var consentRefusal = await RequireLiveConsentAsync(userId, actingTenantId.Value, request.Kind);
        if (consentRefusal is not null) return consentRefusal;

        var result = await _allocationManager.AllocateAsync(
            userId, request, callerUserId.Value, idempotencyKey, apiKeyId, actingTenantId);

        if (!result.IsError) return Ok(result);

        // Fail-closed KYC surfaces a KYC_FORBIDDEN: prefix → 403.
        if (result.Message?.StartsWith(KycAuthorizationError.Forbidden, StringComparison.Ordinal) == true)
            return StatusCode(StatusCodes.Status403Forbidden, result);

        return BadRequest(result);
    }

    /// <summary>
    /// W3.6 — resolves the ACTING TENANT of an allocation from the principal alone; no
    /// request field participates. Returns <c>(null, reason)</c> when it cannot be
    /// resolved unambiguously, and the caller refuses.
    ///
    /// <para>Only an API-key principal reaches this method (the caller checks
    /// <c>AuthMethod</c> first), and an API key carries no <c>act_as_tenant</c> claim —
    /// <c>ApiKeyAuthenticationHandler</c> emits only the key's stored scopes. So the answer
    /// comes from the key's owner: AZOA has no separate Tenant entity (D1/Option B), a tenant
    /// IS the user that owns a <c>tenant:provision</c> key, which is why
    /// <c>TenantController</c> and <c>TenantConsentController</c> both read the tenant id
    /// straight off the key's subject. An API key is a machine credential allocating into
    /// somebody else's account, so it is ALWAYS treated as acting for the route-supplied
    /// user — the consent check can never be skipped by a principal that merely lacks a claim.</para>
    ///
    /// <para>The <c>act_as_tenant</c> branch is a tripwire, not a second supported path: a
    /// principal that somehow carried the claim AND disagreed with its own subject would
    /// present two candidate tenants, and that is a refusal rather than a coin flip.</para>
    /// </summary>
    private (Guid? TenantId, string? Error) ResolveActingTenantId(Guid callerUserId)
    {
        var claimedTenantId = User.GetActingTenantId();
        if (claimedTenantId is { } fromClaim)
        {
            if (fromClaim != callerUserId)
                return (null, "The allocation principal names two different acting tenants.");
            return (fromClaim, null);
        }

        if (callerUserId == Guid.Empty)
            return (null, "The allocation principal carries no resolvable tenant identity.");

        return (callerUserId, null);
    }

    /// <summary>
    /// W3.6 — refuses the allocation unless the target person has a LIVE consent grant to
    /// this tenant covering the scope the allocation will sign under. Returns null when the
    /// allocation may proceed, or the refusal to return otherwise.
    ///
    /// <para>The probe is the very same <see cref="ITenantConsentGate"/> the custody seam
    /// calls, fed the same <see cref="SigningContext"/> shape
    /// <c>BlockchainOperationManager.BuildSigningContext</c> will later build (grantor = the
    /// target person, tenant = the acting tenant, scope = the operation's signing scope), so
    /// the two checks cannot disagree about what "consented" means, and the refusal lands in
    /// the same consent audit trail.</para>
    /// </summary>
    private async Task<ActionResult<AZOAResult<AllocationResult>>?> RequireLiveConsentAsync(
        Guid targetUserId, Guid actingTenantId, AllocationKind kind)
    {
        var (operationType, signingScope) = SigningShapeFor(kind);

        // An allocation kind with no declared signing shape, or one whose scope is not
        // legitimate for its operation type (the S6 guardrail the dispatch seam also
        // applies), cannot be consent-checked at all — refuse rather than guess.
        if (signingScope is null || !AzoaScopes.IsScopeValidForOperation(operationType, signingScope))
            return StatusCode(StatusCodes.Status403Forbidden,
                AZOAResult<AllocationResult>.FailureWithCode(
                    $"Allocation kind '{kind}' has no consent-checkable signing scope.",
                    AzoaErrorCodes.Forbidden));

        if (_consentGate is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                AZOAResult<AllocationResult>.FailureWithCode(
                    "Consent cannot be verified; the allocation was refused.",
                    AzoaErrorCodes.DependencyUnavailable));

        var probe = new SigningContext(targetUserId, Guid.Empty, IsPlatform: false)
            .ActingAs(actingTenantId, signingScope, targetUserId);

        var consent = await _consentGate.EnsureAllowedAsync(probe, HttpContext.RequestAborted);
        if (consent.IsError)
            return StatusCode(StatusCodes.Status403Forbidden,
                AZOAResult<AllocationResult>.FailureWithCode(consent.Message, AzoaErrorCodes.Forbidden));

        return null;
    }

    /// <summary>
    /// The (operation type, signing scope) pair each allocation kind resolves to — <c>Mint</c>
    /// ⇒ <c>nft:mint</c>, <c>Transfer</c> ⇒ <c>transfer:sign</c>, <c>IssueUnits</c> ⇒
    /// <c>units:issue</c> — so the edge check asks about the same capability the custody seam
    /// will. An unrecognised kind yields no scope and is refused.
    ///
    /// <para><b>Parity is not yet compiler-enforced.</b> <c>AllocationManager</c> stamps the
    /// deep-seam scope on the operation row it builds, in a file this seam cannot reach, so
    /// the two constants have to be kept equal by hand. <c>AzoaScopes.OperationScopeMap</c> is
    /// the arbiter: a kind whose pair is not a legitimate (operation, scope) pair is refused
    /// here rather than probed, so a drift fails closed instead of checking the wrong
    /// capability.</para>
    /// </summary>
    public static (string OperationType, string? SigningScope) SigningShapeFor(AllocationKind kind) => kind switch
    {
        AllocationKind.Mint => ("Mint", AzoaScopes.NftMint),
        AllocationKind.Transfer => ("Transfer", AzoaScopes.TransferSign),
        // OI-23. An issuance draws units already sitting in the reserve and CREDITS them to
        // the recipient, signing with the PLATFORM key — nothing of the recipient's is spent.
        // Labelling that with transfer:sign asked every custodially onboarded person for an
        // outbound value-spending grant they can never make (ConsentManager rejects value
        // scopes under Participation origin), and IssueUnits is the DEFAULT AllocationKind,
        // so the default allocation 403'd for the people the product is for. It signs under
        // its own inbound-credit scope; transfer:sign keeps meaning money leaving an account.
        AllocationKind.IssueUnits => (AzoaScopes.IssueUnitsOperationType, AzoaScopes.UnitsIssue),
        _ => (kind.ToString(), null),
    };

    /// <summary>
    /// The API-key scopes ANY ONE of which admits this allocation kind at the door. An
    /// issuance is a reserve-funded credit, so <c>units:issue</c> names it honestly;
    /// <c>nft:mint</c> still admits it because that is what every tenant key already
    /// carries, and this door has never been the control that decides WHOSE units move —
    /// the target's live consent grant and the reserve partition guard are.
    /// </summary>
    private static IReadOnlyList<string> AdmittingKeyScopesFor(AllocationKind kind) => kind switch
    {
        AllocationKind.IssueUnits => new[] { AzoaScopes.UnitsIssue, AzoaScopes.NftMint },
        _ => new[] { AzoaScopes.NftMint },
    };

    /// <summary>
    /// Reads the optional client <c>Idempotency-Key</c> header (mirrors
    /// <c>WalletController.ReadIdempotencyKey</c>). Returns null when absent/blank
    /// so the manager falls back to its deterministic content-addressed key.
    /// </summary>
    private string? ReadIdempotencyKey()
    {
        if (Request.Headers.TryGetValue("Idempotency-Key", out var values))
        {
            var key = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(key))
                return key.Trim();
        }
        return null;
    }

    private Guid? GetUserIdFromClaims()
    {
        var sub = User.FindFirst("UserId")?.Value
                 ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    private string? GetApiKeyId()
    {
        var value = User.FindFirst("ApiKeyId")?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
