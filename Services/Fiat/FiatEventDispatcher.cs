using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;

namespace AZOA.WebAPI.Services.Fiat;

/// <summary>Runs idempotent handlers from the durable, signature-verified inbox facts.</summary>
public static class FiatEventDispatcher
{
    public static async Task<AZOAResult<FiatEvent>> ApplyAsync(
        FiatEvent row, RailEnvironment environment, IStripeWebhookInbox inbox,
        IEnumerable<IFiatEventHandler> handlers, ILogger logger, CancellationToken ct)
    {
        if (row.Livemode != (environment.Kind == RailEnvironmentKind.Live)
            || !string.Equals(row.Environment, environment.Name, StringComparison.OrdinalIgnoreCase))
            return AZOAResult<FiatEvent>.Failure("This event belongs to a different rail environment.");
        if (row.Status is FiatEventStatuses.Applied or FiatEventStatuses.Ignored)
            return AZOAResult<FiatEvent>.Success(row);
        var context = new FiatEventContext(row, row.EventType, row.CheckoutSessionId,
            row.PaymentIntentId, row.ChargeId,
            Guid.TryParse(SurrealLink.FromLink(row.IntentId), out var intent) ? intent : null,
            Guid.TryParse(SurrealLink.FromLink(row.SubjectUserId), out var subject) ? subject : null,
            environment, row.ProviderAccountId, row.ProviderObjectId);
        var outcomes = new List<FiatEventOutcome>();
        string? error = null;
        foreach (var handler in handlers.Where(handler => handler.Handles(row.EventType)))
        {
            try { outcomes.Add(await handler.ApplyAsync(context, ct)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Fiat handler {Handler} failed for event {EventId}.", handler.Name, row.Id);
                outcomes.Add(FiatEventOutcome.Failed);
                error ??= handler.Name + " failed while applying this event.";
            }
        }
        return await inbox.RecordOutcomeAsync(row.Id,
            FiatEventDispositions.ToRowStatus(FiatEventDispositions.Aggregate(outcomes)), error, ct);
    }
}
