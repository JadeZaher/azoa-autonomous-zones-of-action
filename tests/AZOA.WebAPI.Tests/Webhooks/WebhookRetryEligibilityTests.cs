using AZOA.WebAPI.Services.Webhooks;
using FluentAssertions;
using Xunit;

namespace AZOA.WebAPI.Tests.Webhooks;

/// <summary>Which undelivered events may start an audited retry cycle; see Services/Webhooks/AGENTS.md §operator-retry.</summary>
public sealed class WebhookRetryEligibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string, string, string?, int?, long, WebhookRetryOrigin?> Cases => new()
    {
        { "dead letter", "DeadLettered", null, null, 1, WebhookRetryOrigin.DeadLettered },
        { "delivered", "Delivered", null, null, 1, null },
        { "pending within budget waiting on backoff", "Pending", null, null, 7, null },
        { "pending at the attempt budget", "Pending", null, null, 8, WebhookRetryOrigin.AttemptBudgetExhausted },
        { "pending with a live lease", "Pending", "lease", 60, 3, null },
        { "pending with an expired lease", "Pending", "lease", -1, 3, WebhookRetryOrigin.AbandonedAttempt },
        { "lease expiring exactly now is no longer live", "Pending", "lease", 0, 3, WebhookRetryOrigin.AbandonedAttempt },
        { "exhausted budget but the attempt is still live", "Pending", "lease", 60, 8, null },
        { "lease id without an expiry is treated as live", "Pending", "lease", null, 3, null },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Classifies_undelivered_states(string scenario, string status, string? leaseId, int? leaseSecondsFromNow,
        long cycleAttempts, WebhookRetryOrigin? expected)
    {
        var expiry = leaseSecondsFromNow is { } seconds ? Now.AddSeconds(seconds) : (DateTimeOffset?)null;

        WebhookRetryEligibility.Classify(status, leaseId, expiry, cycleAttempts, maximumAttempts: 8, Now)
            .Should().Be(expected, scenario);
    }
}
