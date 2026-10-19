using System.Security.Claims;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Services.Signing;

namespace AZOA.WebAPI.Services.Webhooks;

/// <summary>Authorizes registration management from authenticated principals before atomic store mutation.</summary>
public sealed class WebhookRegistrationManagement(SigningAdmissionAuthorityFactory authority, IWebhookRegistrationStore registrations)
{
    public async Task<AZOAResult<WebhookRegistrationStatus>> GetAsync(ClaimsPrincipal principal, Guid? operatorTenant, CancellationToken ct)
    {
        var bound = await BindAsync(principal, operatorTenant, ct);
        if (bound.IsError || bound.Result is null) return AZOAResult<WebhookRegistrationStatus>.Failure(bound.Message);
        var tenant = operatorTenant ?? bound.Result.Credential.ActorUserId;
        var saved = await registrations.GetByTenantAsync(tenant, ct);
        if (saved.IsError) return AZOAResult<WebhookRegistrationStatus>.Failure(saved.Message);
        var row = saved.Result;
        return row is null ? new() { Message = "No registration." }
            : AZOAResult<WebhookRegistrationStatus>.Success(new(row.Id, row.TenantId, row.Version, row.DestinationRevision,
                row.SecretRevision, row.Url, row.IsActive, row.CreatedAt, row.SecretRotatedAt, row.LegacyFingerprint));
    }

    public async Task<AZOAResult<WebhookRegistrationMutationResult>> MutateAsync(ClaimsPrincipal principal, Guid? operatorTenant,
        WebhookRegistrationCommand command, CancellationToken ct)
    {
        var bound = await BindAsync(principal, operatorTenant, ct);
        return bound.IsError || bound.Result is null ? AZOAResult<WebhookRegistrationMutationResult>.Failure(bound.Message)
            : await registrations.MutateAsync(operatorTenant ?? bound.Result.Credential.ActorUserId, command, bound.Result, ct);
    }

    public async Task<AZOAResult<WebhookRegistrationWithdrawal>> WithdrawAsync(ClaimsPrincipal principal, Guid? operatorTenant,
        WebhookRegistrationCommand command, CancellationToken ct)
    {
        var bound = await BindAsync(principal, operatorTenant, ct);
        return bound.IsError || bound.Result is null ? AZOAResult<WebhookRegistrationWithdrawal>.Failure(bound.Message)
            : await registrations.WithdrawAsync(operatorTenant ?? bound.Result.Credential.ActorUserId, command, bound.Result, ct);
    }

    internal async Task<AZOAResult<SigningConsentWitness>> BindAsync(ClaimsPrincipal principal, Guid? operatorTenant, CancellationToken ct)
    {
        var captured = await authority.CaptureCredentialAsync(principal, ct);
        if (captured.IsError || captured.Result is not { } credential)
            return AZOAResult<SigningConsentWitness>.Failure(captured.Message);
        if (credential.TenantId is not null || operatorTenant == Guid.Empty)
            return AZOAResult<SigningConsentWitness>.Failure("A direct tenant-management credential is required.");
        var isOperator = OperatorActor.IsOperatorCredential(credential);
        if (operatorTenant.HasValue != isOperator || (isOperator && credential.IssuedAt < DateTimeOffset.UtcNow.AddMinutes(-5)))
            return AZOAResult<SigningConsentWitness>.Failure("Operator access requires the operator route and authentication within five minutes.");
        if (!isOperator && credential.TokenUse != AzoaClaims.TokenUseLogin && credential.Kind != "ApiKey")
            return AZOAResult<SigningConsentWitness>.Failure("This credential cannot manage webhook registration.");
        return await authority.BindConsentAsync(credential, [isOperator ? AzoaScopes.Operator : AzoaScopes.TenantProvision], ct);
    }
}
