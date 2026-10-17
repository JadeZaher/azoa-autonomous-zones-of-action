using AZOA.WebAPI.Helpers;
using SurrealForge.Client.Query;
using Event = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentWebhookEvent;
using Transition = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentLifecycleTransition;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealConsentWebhookDeliveryStore
{
    private async Task<bool> HasEvidenceAsync(Event notification, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(notification.TransitionId)) return false;
        var proof = await executor.QuerySingleAsync<Transition>(SurrealQuery<Transition>.Key(notification.TransitionId), ct);
        return proof is not null && ConsentNotificationEvidence.Matches(notification, proof);
    }
}
