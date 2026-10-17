namespace AZOA.WebAPI.Services.Webhooks;

/// <summary>The undelivered state an audited operator/tenant retry restarts from; see AGENTS.md §operator-retry.</summary>
public enum WebhookRetryOrigin
{
    DeadLettered,
    AttemptBudgetExhausted,
    AbandonedAttempt,
}

/// <summary>Classifies whether an undelivered event may start a fresh audited attempt cycle.</summary>
public static class WebhookRetryEligibility
{
    /// <summary>Null when delivered, an attempt is still in flight, or the event is a healthy pending backoff.</summary>
    public static WebhookRetryOrigin? Classify(string status, string? leaseId, DateTimeOffset? leaseExpiresAt,
        long cycleAttemptCount, int maximumAttempts, DateTimeOffset now)
    {
        if (leaseId is not null && (leaseExpiresAt is null || leaseExpiresAt > now)) return null;
        return status switch
        {
            "DeadLettered" => WebhookRetryOrigin.DeadLettered,
            "Pending" when cycleAttemptCount >= maximumAttempts => WebhookRetryOrigin.AttemptBudgetExhausted,
            "Pending" when leaseId is not null => WebhookRetryOrigin.AbandonedAttempt,
            _ => null,
        };
    }
}
