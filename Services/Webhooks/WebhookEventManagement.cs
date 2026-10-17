using System.Security.Claims;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Webhooks;

/// <summary>Uses the same actual-principal tenant/operator boundary as registration management.</summary>
public sealed class WebhookEventManagement(WebhookRegistrationManagement authorization, IActionWebhookDeliveryStore events)
{
    public async Task<AZOAResult<IReadOnlyList<WebhookEventStatus>>> ListAsync(ClaimsPrincipal principal, Guid? operatorTenant,
        int offset, int limit, CancellationToken ct)
    {
        var bound = await authorization.BindAsync(principal, operatorTenant, ct);
        return bound.IsError || bound.Result is null ? AZOAResult<IReadOnlyList<WebhookEventStatus>>.Failure(bound.Message)
            : AZOAResult<IReadOnlyList<WebhookEventStatus>>.Success(await events.ListAsync(
                operatorTenant ?? bound.Result.Credential.ActorUserId, offset, limit, ct));
    }

    public async Task<AZOAResult<WebhookEventStatus>> GetAsync(ClaimsPrincipal principal, Guid? operatorTenant,
        Guid eventId, CancellationToken ct)
    {
        var bound = await authorization.BindAsync(principal, operatorTenant, ct);
        if (bound.IsError || bound.Result is null) return AZOAResult<WebhookEventStatus>.Failure(bound.Message);
        var status = await events.GetAsync(operatorTenant ?? bound.Result.Credential.ActorUserId, eventId, ct);
        return status is null ? AZOAResult<WebhookEventStatus>.Failure("Webhook event was not found.")
            : AZOAResult<WebhookEventStatus>.Success(status);
    }

    public async Task<AZOAResult<WebhookEventRetryReceipt>> RetryAsync(ClaimsPrincipal principal, Guid? operatorTenant,
        Guid eventId, WebhookEventRetryCommand command, CancellationToken ct)
    {
        var bound = await authorization.BindAsync(principal, operatorTenant, ct);
        if (bound.IsError || bound.Result is null) return AZOAResult<WebhookEventRetryReceipt>.Failure(bound.Message);
        var receipt = await events.RetryAsync(operatorTenant ?? bound.Result.Credential.ActorUserId, eventId, command, bound.Result, ct);
        return receipt is null ? AZOAResult<WebhookEventRetryReceipt>.Failure("Retry requires the exact version of an undelivered event with no live attempt (dead-lettered, attempt budget exhausted or abandoned lease), current authority and the original active destination.")
            : AZOAResult<WebhookEventRetryReceipt>.Success(receipt);
    }
}
