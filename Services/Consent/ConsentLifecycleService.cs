using System.Security.Claims;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Signing;

namespace AZOA.WebAPI.Services.Consent;

/// <summary>Admits immutable consent commands using the actual authenticated credential.</summary>
public sealed class ConsentLifecycleService(SigningAdmissionAuthorityFactory authorities, SurrealConsentLifecycleStore store)
{
    public async Task<AZOAResult<ConsentCommandWithdrawal>> WithdrawAsync(ClaimsPrincipal principal,
        ConsentLifecycleCommand command, CancellationToken ct)
    {
        ConsentLifecycleCommand normalized;
        try { normalized = ConsentLifecycleIdentity.Normalize(command); }
        catch (ArgumentException exception) { return AZOAResult<ConsentCommandWithdrawal>.Failure(exception.Message); }
        var credential = await authorities.CaptureCredentialAsync(principal, ct);
        if (credential.IsError || credential.Result is null)
            return AZOAResult<ConsentCommandWithdrawal>.Failure(credential.Message);
        if (!HasCurrentOwnerLogin(principal, credential.Result))
            return AZOAResult<ConsentCommandWithdrawal>.Failure("Sign in again before changing permission.");
        var receipt = await store.WithdrawAsync(normalized, credential.Result, ct);
        return receipt is null ? AZOAResult<ConsentCommandWithdrawal>.Failure("Withdrawal requires current owner authority and the exact original request.")
            : AZOAResult<ConsentCommandWithdrawal>.Success(receipt);
    }

    public async Task<AZOAResult<ConsentOffboardReceipt>> OffboardAsync(ClaimsPrincipal principal,
        string reference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Trim().Length > 1024)
            return AZOAResult<ConsentOffboardReceipt>.Failure("A bounded participation reference is required.");
        var credential = await authorities.CaptureCredentialAsync(principal, ct);
        if (credential.IsError || credential.Result is null)
            return AZOAResult<ConsentOffboardReceipt>.Failure(credential.Message);
        var authority = await authorities.BindConsentAsync(credential.Result, [AzoaScopes.TenantProvision], ct);
        if (authority.IsError || authority.Result is null)
            return AZOAResult<ConsentOffboardReceipt>.Failure(authority.Message);
        var receipt = await store.OffboardAsync(credential.Result.ActorUserId, reference, authority.Result, ct);
        return receipt is null ? AZOAResult<ConsentOffboardReceipt>.Failure("Participation changed or current tenant authority is unavailable; retry the same reference.")
            : AZOAResult<ConsentOffboardReceipt>.Success(receipt);
    }

    /// <summary>The authenticated tenant gives up every live UserExplicit grant from its claimed member; see AGENTS.md §relinquish.</summary>
    public async Task<AZOAResult<ConsentRelinquishReceipt>> RelinquishAsync(ClaimsPrincipal principal,
        string externalSubject, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(externalSubject) || externalSubject.Trim().Length > 128 || externalSubject.Any(char.IsControl))
            return AZOAResult<ConsentRelinquishReceipt>.FailureWithCode(
                "externalSubject must be a non-empty value of at most 128 characters.", AzoaErrorCodes.InvalidRequest);
        var credential = await authorities.CaptureCredentialAsync(principal, ct);
        if (credential.IsError || credential.Result is null)
            return AZOAResult<ConsentRelinquishReceipt>.FailureWithCode(credential.Message, AzoaErrorCodes.Forbidden);
        var authority = await authorities.BindConsentAsync(credential.Result, [AzoaScopes.TenantProvision], ct);
        if (authority.IsError || authority.Result is null)
            return AZOAResult<ConsentRelinquishReceipt>.FailureWithCode(authority.Message, AzoaErrorCodes.Forbidden);
        return await store.RelinquishAsync(credential.Result.ActorUserId, externalSubject, authority.Result, ct);
    }

    public async Task<AZOAResult<ConsentLifecycleReceipt>> GrantHandoffAsync(ClaimsPrincipal principal,
        ConsentLifecycleCommand command, AZOA.WebAPI.Persistence.SurrealDb.Models.MemberHandoff handoff, CancellationToken ct)
    {
        var credential = await authorities.CaptureCredentialAsync(principal, ct);
        if (credential.IsError || credential.Result is null || credential.Result.AuthNotBefore is null
            || !HasCurrentOwnerLogin(principal, credential.Result))
            return AZOAResult<ConsentLifecycleReceipt>.Failure("Sign in again with your claimed account before granting permission.");
        var receipt = await store.AdmitHandoffAsync(ConsentLifecycleIdentity.Normalize(command), credential.Result, handoff, ct);
        return receipt is null ? AZOAResult<ConsentLifecycleReceipt>.Failure("The handoff or account authority changed; retry the same request.")
            : AZOAResult<ConsentLifecycleReceipt>.Success(receipt);
    }

    public async Task<AZOAResult<ConsentLifecycleReceipt>> GrantAsync(ClaimsPrincipal principal,
        ConsentLifecycleCommand command, CancellationToken ct)
    {
        ConsentLifecycleCommand normalized;
        try { normalized = ConsentLifecycleIdentity.Normalize(command); }
        catch (ArgumentException exception) { return AZOAResult<ConsentLifecycleReceipt>.Failure(exception.Message); }
        var credential = await authorities.CaptureCredentialAsync(principal, ct);
        if (credential.IsError || credential.Result is null)
            return AZOAResult<ConsentLifecycleReceipt>.Failure(credential.Message);
        if (!HasCurrentOwnerLogin(principal, credential.Result))
            return AZOAResult<ConsentLifecycleReceipt>.Failure("Sign in again before granting permission.");
        var receipt = normalized.Origin == GrantOrigin.Participation
            ? await store.AdmitParticipationAsync(normalized, credential.Result, ct)
            : await store.AdmitUserExplicitAsync(normalized, credential.Result, ct);
        return receipt is null
            ? AZOAResult<ConsentLifecycleReceipt>.Failure("Consent requires current owner authority, an unchanged request and an active tenant; participation also requires approved enrollment.")
            : AZOAResult<ConsentLifecycleReceipt>.Success(receipt);
    }

    public async Task<AZOAResult<ConsentRevocationReceipt>> RevokeAsync(ClaimsPrincipal principal, Guid grantId, CancellationToken ct)
    {
        var credential = await authorities.CaptureCredentialAsync(principal, ct);
        if (credential.IsError || credential.Result is null)
            return AZOAResult<ConsentRevocationReceipt>.Failure(credential.Message);
        if (!HasCurrentOwnerLogin(principal, credential.Result))
            return AZOAResult<ConsentRevocationReceipt>.Failure("Sign in again before changing permission.");
        var receipt = await store.RevokeUserExplicitAsync(grantId, credential.Result, ct);
        return receipt is null ? AZOAResult<ConsentRevocationReceipt>.Failure("Consent was not found or the current owner authority changed.")
            : AZOAResult<ConsentRevocationReceipt>.Success(receipt);
    }

    // Exact login-cut proof complements the transaction's unchanged-watermark fence; see AGENTS.md.
    private static bool HasCurrentOwnerLogin(ClaimsPrincipal principal, SigningCredentialWitness credential)
        => credential.AuthNotBefore is not { } cut || (credential.Kind == "Jwt"
            && credential.TokenUse == AzoaClaims.TokenUseLogin && credential.TenantId is null
            && principal.FindAll(AzoaClaims.AuthCut).Count() == 1
            && string.Equals(principal.FindFirst(AzoaClaims.AuthCut)?.Value,
                AuthWatermark.FormatCut(cut.UtcDateTime), StringComparison.Ordinal));
}
