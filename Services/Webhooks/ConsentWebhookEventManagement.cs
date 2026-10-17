using System.Security.Claims;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Webhooks;

/// <summary>Uses the same actual-principal tenant/operator boundary as registration management.</summary>
public sealed class ConsentWebhookEventManagement(WebhookRegistrationManagement authorization, IConsentWebhookDeliveryStore events)
{
    public async Task<AZOAResult<IReadOnlyList<ConsentWebhookEventStatus>>> ListAsync(ClaimsPrincipal principal, Guid? operatorTenant,
        int offset, int limit, CancellationToken ct)
    {
        var bound = await authorization.BindAsync(principal, operatorTenant, ct);
        return bound.IsError || bound.Result is null ? AZOAResult<IReadOnlyList<ConsentWebhookEventStatus>>.Failure(bound.Message)
            : AZOAResult<IReadOnlyList<ConsentWebhookEventStatus>>.Success(await events.ListAsync(
                operatorTenant ?? bound.Result.Credential.ActorUserId, offset, limit, ct));
    }

    public async Task<AZOAResult<ConsentWebhookEventStatus>> GetAsync(ClaimsPrincipal principal, Guid? operatorTenant,
        Guid eventId, CancellationToken ct)
    {
        var bound = await authorization.BindAsync(principal, operatorTenant, ct);
        if (bound.IsError || bound.Result is null) return AZOAResult<ConsentWebhookEventStatus>.Failure(bound.Message);
        var status = await events.GetAsync(operatorTenant ?? bound.Result.Credential.ActorUserId, eventId, ct);
        return status is null ? AZOAResult<ConsentWebhookEventStatus>.Failure("Webhook event was not found.")
            : AZOAResult<ConsentWebhookEventStatus>.Success(status);
    }

    public async Task<AZOAResult<WebhookEventRetryReceipt>> RetryAsync(ClaimsPrincipal principal, Guid? operatorTenant,
        Guid eventId, WebhookEventRetryCommand command, CancellationToken ct)
    {
        var bound = await authorization.BindAsync(principal, operatorTenant, ct);
        if (bound.IsError || bound.Result is null) return AZOAResult<WebhookEventRetryReceipt>.Failure(bound.Message);
        var receipt = await events.RetryAsync(operatorTenant ?? bound.Result.Credential.ActorUserId, eventId, command, bound.Result, ct);
        return receipt is null ? AZOAResult<WebhookEventRetryReceipt>.Failure("Retry requires the exact dead-letter version, current authority and original active destination.")
            : AZOAResult<WebhookEventRetryReceipt>.Success(receipt);
    }
}
