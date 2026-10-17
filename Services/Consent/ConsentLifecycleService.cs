using System.Security.Claims;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
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

    public async Task<AZOAResult<ConsentLifecycleReceipt>> GrantAsync(ClaimsPrincipal principal,
        ConsentLifecycleCommand command, CancellationToken ct)
    {
        ConsentLifecycleCommand normalized;
        try { normalized = ConsentLifecycleIdentity.Normalize(command); }
        catch (ArgumentException exception) { return AZOAResult<ConsentLifecycleReceipt>.Failure(exception.Message); }
        var credential = await authorities.CaptureCredentialAsync(principal, ct);
        if (credential.IsError || credential.Result is null)
            return AZOAResult<ConsentLifecycleReceipt>.Failure(credential.Message);
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
        var receipt = await store.RevokeUserExplicitAsync(grantId, credential.Result, ct);
        return receipt is null ? AZOAResult<ConsentRevocationReceipt>.Failure("Consent was not found or the current owner authority changed.")
            : AZOAResult<ConsentRevocationReceipt>.Success(receipt);
    }
}
