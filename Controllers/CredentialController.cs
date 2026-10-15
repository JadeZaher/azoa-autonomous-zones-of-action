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
/// The issuer-facing credential surface (D19/D41). Two routes: ISSUE a soulbound credential
/// to another user, and REVOKE one you issued by clawing the ASA back on chain.
///
/// <para>The two are one rail. D19 authorizes revocation to the credential's ISSUER, which
/// presupposes an issuer distinct from the holder — and until D41 no path produced one, so the
/// authorization rule was not enforced, it was VACUOUS. <see cref="Issue"/> is what feeds it.</para>
///
/// <para>IDOR: the credential is named by the route asset id and the issuer is read
/// from the credential's own record, so no body field can redirect the revocation or
/// nominate a different authority. The issuer check itself lives in
/// <see cref="ICredentialManager"/> — the same check the action-node path goes
/// through, so neither seam can reach a different answer.</para>
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CredentialController : ControllerBase
{
    private readonly ICredentialManager _credentialManager;
    private readonly IUserNFTService _userNfts;

    public CredentialController(ICredentialManager credentialManager, IUserNFTService userNfts)
    {
        _credentialManager = credentialManager;
        _userNfts = userNfts;
    }

    /// <summary>
    /// Issues a soulbound credential to <paramref name="recipientUserId"/> (D41). The caller
    /// becomes the issuer of record and is thereafter the ONLY party that may revoke it.
    /// </summary>
    /// <remarks>
    /// <para><b>D41 Phase 0 — who may issue, and why.</b> Issuance is <b>ROLE-GATED</b>: the caller
    /// must hold the <c>dapp:manager</c> role (<see cref="AzoaDappRoles.Manager"/>), and a scoped API
    /// key must additionally carry <see cref="AzoaScopes.NftMint"/>. The three candidates were:</para>
    /// <list type="bullet">
    /// <item><b>Open</b> — rejected. A credential is soulbound and only its ISSUER can revoke it, so
    /// an unwanted one is permanent from the recipient's side. Open issuance is therefore a griefing
    /// primitive with no cleanup path: anyone could staple arbitrary claims onto anyone's identity
    /// and be the only party able to remove them.</item>
    /// <item><b>Recipient consent grant</b> — the right long-term answer, deliberately NOT built
    /// here. It needs an offer/accept rail with its own store, expiry and revocation model, and it
    /// does not by itself stop griefing (unsolicited offers are still spam). Deferring it is recorded
    /// in the track plan rather than implied. Note the chain already enforces a hard backstop no
    /// AZOA code can weaken: an ASA cannot land in an account that has not signed its own opt-in, so
    /// no soulbound asset reaches a recipient who never consented — what an ungated route would
    /// pollute is AZOA's own ownership record.</item>
    /// <item><b>Role</b> — chosen. D41's use case is an ORGANIZATION issuing tiers to its members,
    /// which is an organizational capability, not a peer-to-peer one. <c>dapp:manager</c> is an
    /// existing server-stamped claim that an ordinary authenticated user does not have and cannot
    /// give themselves (<c>AzoaDappRoles.Normalize</c> defaults an absent role to <c>dapp:user</c>,
    /// and only an operator or an existing manager may assign one), so this is a real restriction
    /// rather than a scope check a JWT owner passes for free.</item>
    /// </list>
    /// <para><b>The issuer is never a body field.</b> It is stamped from the authenticated caller
    /// below and <see cref="UserNFTMintModel"/> has no issuer property at all: since the issuer is
    /// the single input the revoke decision reads, a caller-supplied one would let anyone nominate
    /// themselves the revocation authority over a credential they did not issue.</para>
    /// </remarks>
    /// <response code="200">The credential was issued; the caller is its issuer of record.</response>
    /// <response code="403">Caller is not a dApp manager, or a scoped key lacks nft:mint.</response>
    [HttpPost("issue/{recipientUserId:guid}")]
    [EnableRateLimiting("financial")]
    public async Task<ActionResult<AZOAResult<AZOA.WebAPI.Interfaces.IUserNFT>>> Issue(
        Guid recipientUserId, [FromBody] UserNFTMintModel request)
    {
        var callerUserId = GetUserIdFromClaims();
        if (callerUserId is null)
            return Unauthorized(AZOAResult<AZOA.WebAPI.Interfaces.IUserNFT>.Failure("Invalid token."));

        // The griefing gate. Ordered FIRST so an unauthorized caller never reaches the mint seam.
        if (!User.HasDappManagerRole())
            return StatusCode(StatusCodes.Status403Forbidden,
                AZOAResult<AZOA.WebAPI.Interfaces.IUserNFT>.FailureWithCode(
                    $"Issuing a credential to another user requires the '{AzoaDappRoles.Manager}' role. " +
                    "A credential is soulbound and revocable only by its issuer, so an unsolicited one " +
                    "cannot be removed by the person carrying it.",
                    AzoaErrorCodes.Forbidden));

        // A scoped credential must additionally carry the mint capability; a JWT owner who already
        // passed the role gate is unaffected, exactly as on the revoke route below.
        if (!User.HasSigningScope(AzoaScopes.NftMint))
            return StatusCode(StatusCodes.Status403Forbidden,
                AZOAResult<AZOA.WebAPI.Interfaces.IUserNFT>.FailureWithCode(
                    $"Caller lacks the '{AzoaScopes.NftMint}' scope required to issue a credential.",
                    AzoaErrorCodes.Forbidden));

        var model = request ?? new UserNFTMintModel();

        // This route issues CREDENTIALS, so the shape is fixed here rather than trusted from the
        // body: a transferable 'credential' would leave the issuer's clawback authority pointing at
        // an account the ownership row no longer names.
        model.IsSoulbound = true;
        model.IsTransferable = false;

        // Issuer = the authenticated caller, recipient = the route id. Neither comes from the body.
        // The mint seam refuses a self-issued credential and refuses one whose recipient has no
        // provable address on the chain, so no unrevocable row can be written from here.
        var result = await _userNfts.MintUserNFTAsync(callerUserId.Value, recipientUserId, model);
        return result.IsError ? BadRequest(result) : Ok(result);
    }

    /// <summary>
    /// Revokes the soulbound credential <paramref name="assetId"/>, returning it to the
    /// issuer's wallet. Idempotent on the <c>Idempotency-Key</c> header, so a redelivered
    /// request never submits a second clawback.
    ///
    /// <para><b>200 means revoked, and nothing else does.</b> A broadcast that has not
    /// confirmed comes back <c>202 Accepted</c> with <c>isError = true</c>, code
    /// <see cref="CredentialRevokeCodes.PendingConfirmation"/>, and its tx hash — so
    /// neither a status-code check nor an <c>isError</c> check (which is what AZOA client
    /// helpers actually branch on) can mistake it for a completed revocation. Do not
    /// re-submit a 202: the transaction is already on the network. Reconcile, or poll the
    /// operation named by <c>operationId</c>.</para>
    /// </summary>
    /// <response code="200">The clawback confirmed on chain; the credential is revoked.</response>
    /// <response code="202">Broadcast but NOT confirmed — outcome unknown, do not re-submit.</response>
    /// <response code="403">Caller lacks the revoke scope, is not the recorded issuer, or fails the KYC gate.</response>
    [HttpPost("{assetId}/revoke")]
    [EnableRateLimiting("financial")]
    public async Task<ActionResult<AZOAResult<CredentialRevokeResult>>> Revoke(
        string assetId, [FromBody] CredentialRevokeRequest request)
    {
        var callerUserId = GetUserIdFromClaims();
        if (callerUserId is null)
            return Unauthorized(AZOAResult<CredentialRevokeResult>.Failure("Invalid token."));

        // A scoped credential must carry the revoke capability to reach this path at
        // all; a JWT owner is unaffected. Entitlement to THIS credential is a separate
        // question, answered by the issuer check inside the manager.
        if (!User.HasSigningScope(AzoaScopes.CredentialRevokeSign))
            return StatusCode(StatusCodes.Status403Forbidden,
                AZOAResult<CredentialRevokeResult>.FailureWithCode(
                    $"Caller lacks the '{AzoaScopes.CredentialRevokeSign}' scope required to revoke a credential.",
                    AzoaErrorCodes.Forbidden));

        // AC4: a tenant-driven child credential carries an act_as_tenant claim; pass it
        // so the custody seam runs the live consent check before the clawback key decrypt.
        var actingTenantId = User.GetActingTenantId();

        var result = await _credentialManager.RevokeAsync(
            assetId,
            request ?? new CredentialRevokeRequest(),
            callerUserId.Value,
            ReadIdempotencyKey(),
            actingTenantId);

        if (!result.IsError) return Ok(result);

        // A broadcast that has not confirmed is neither success nor failure. 202 Accepted
        // says "taken, outcome not yet known" — and BadRequest would be an outright lie
        // that invites the client to re-submit a clawback already on the network.
        if (string.Equals(result.Code, CredentialRevokeCodes.PendingConfirmation, StringComparison.Ordinal))
            return Accepted(result);

        if (string.Equals(result.Code, AzoaErrorCodes.Forbidden, StringComparison.Ordinal))
            return StatusCode(StatusCodes.Status403Forbidden, result);

        // A key that already settled against a DIFFERENT credential is a conflict, not a
        // malformed request: the caller must use a fresh Idempotency-Key.
        if (string.Equals(result.Code, AzoaErrorCodes.Conflict, StringComparison.Ordinal))
            return Conflict(result);

        // Fail-closed KYC surfaces a KYC_FORBIDDEN: prefix → 403.
        if (result.Message?.StartsWith(KycAuthorizationError.Forbidden, StringComparison.Ordinal) == true)
            return StatusCode(StatusCodes.Status403Forbidden, result);

        return BadRequest(result);
    }

    /// <summary>
    /// Reads the optional client <c>Idempotency-Key</c> header (mirrors
    /// <c>AllocationController.ReadIdempotencyKey</c>). Null when absent/blank so the
    /// manager falls back to its deterministic content-addressed key.
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
}
