using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AZOA.WebAPI.IntegrationTests.Http.Infrastructure;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using FluentAssertions;
using SurrealForge.Client.Query;
using Event = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentWebhookEvent;

namespace AZOA.WebAPI.IntegrationTests.Http;

/// <summary>
/// Outbox drain through the running delivery worker: a real consent transition, the tenant's real
/// registration, an in-process receiver, and the tenant retry route. See <c>Http/AGENTS.md</c> §webhook-drain.
/// </summary>
public sealed class WebhookOutboxDrainFlowTests(WebhookDrainWebApplicationFactory factory)
    : HttpFlowTestBase(factory), IClassFixture<WebhookDrainWebApplicationFactory>
{
    private const string Events = "api/tenant/consent-webhook-events";

    [Fact]
    public async Task A_consent_transition_drains_once_to_the_receiver_signed_with_the_disclosed_secret()
    {
        var receiver = Receiver(HttpStatusCode.OK);
        using var tenant = await OnboardTenantAsync();
        var secret = await RegisterAsync(tenant, receiver.Host);

        await ProvisionChildAsync(tenant);
        var delivered = await WaitForAsync(tenant, status => status.Status == "Delivered");

        var request = receiver.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Headers["X-Azoa-Idempotency-Id"].Should().Be(delivered.EventId.ToString("N"));
        request.Headers["X-Azoa-Secret-Revision"].Should().Be("1");
        request.Headers["X-Azoa-Signature"].Should().Be(Signature(secret, request.Headers["X-Azoa-Timestamp"], request.Body),
            "the receiver verifies with the secret disclosed once at registration");
        delivered.AttemptCount.Should().Be(1);
        delivered.Retryable.Should().BeFalse();
    }

    [Fact]
    public async Task A_dead_lettered_event_retried_by_the_tenant_is_delivered_with_the_same_identity()
    {
        var receiver = Receiver(HttpStatusCode.ServiceUnavailable);
        using var tenant = await OnboardTenantAsync();
        await RegisterAsync(tenant, receiver.Host);
        await ProvisionChildAsync(tenant);
        var dead = await WaitForAsync(tenant, status => status.Status == "DeadLettered");
        dead.ErrorCode.Should().Be("HTTP_503");
        dead.Retryable.Should().BeTrue();

        receiver.Status = HttpStatusCode.OK;
        var command = new WebhookEventRetryCommand(Guid.NewGuid(), dead.Version, "Receiver repaired after the outage.");
        using var retried = await tenant.Client.PostAsJsonAsync($"{Events}/{dead.EventId}/retry", command, JsonOptions);
        var receipt = await ReadOkAsync<WebhookEventRetryReceipt>(retried);
        receipt.RetriedFrom.Should().Be("DeadLettered");

        var delivered = await WaitForAsync(tenant, status => status.Status == "Delivered");
        delivered.EventId.Should().Be(dead.EventId);
        delivered.AttemptCount.Should().Be(2);
        delivered.RetryGeneration.Should().Be(1);
        receiver.Requests.Should().HaveCount(2).And.OnlyContain(request =>
            request.Headers["X-Azoa-Idempotency-Id"] == dead.EventId.ToString("N"));

        using var replay = await tenant.Client.PostAsJsonAsync($"{Events}/{dead.EventId}/retry", command, JsonOptions);
        (await ReadOkAsync<WebhookEventRetryReceipt>(replay)).Should().BeEquivalentTo(receipt,
            "the lost-response replay returns the original receipt and starts no new cycle");
        receiver.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_abandoned_attempt_is_retryable_only_after_its_lease_expires()
    {
        var receiver = Receiver(HttpStatusCode.ServiceUnavailable);
        using var tenant = await OnboardTenantAsync();
        await RegisterAsync(tenant, receiver.Host);
        await ProvisionChildAsync(tenant);
        var dead = await WaitForAsync(tenant, status => status.Status == "DeadLettered");
        receiver.Status = HttpStatusCode.OK;

        // A worker crashed mid-attempt: Pending, holding a lease, not due again for an hour.
        var id = dead.EventId.ToString("N");
        await WriteAsync(SurrealWriter.UpdateOnly<Event>(id).Where(seed => true)
            .Set(row => row.Status, Event.DeliveryStatus.Pending).Set(row => row.Version, dead.Version + 1)
            .Set(row => row.LeaseId, Guid.NewGuid().ToString("N")).Set(row => row.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(5))
            .Set(row => row.NextAttemptAt, DateTimeOffset.UtcNow.AddHours(1)).Build());
        var live = await StatusAsync(tenant, dead.EventId);
        live.AttemptInFlight.Should().BeTrue();
        live.Retryable.Should().BeFalse();
        using (var refused = await tenant.Client.PostAsJsonAsync($"{Events}/{dead.EventId}/retry",
                   new WebhookEventRetryCommand(Guid.NewGuid(), live.Version, "Retry while the attempt is in flight."), JsonOptions))
            await ShouldReturnAsync(refused, HttpStatusCode.Conflict);

        await WriteAsync(SurrealWriter.UpdateOnly<Event>(id).Where(seed => true).Set(row => row.LeaseExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)).Build());
        var abandoned = await StatusAsync(tenant, dead.EventId);
        abandoned.Retryable.Should().BeTrue();
        using var retried = await tenant.Client.PostAsJsonAsync($"{Events}/{dead.EventId}/retry",
            new WebhookEventRetryCommand(Guid.NewGuid(), abandoned.Version, "Worker crashed; restart the attempt."), JsonOptions);
        (await ReadOkAsync<WebhookEventRetryReceipt>(retried)).RetriedFrom.Should().Be("AbandonedAttempt");
        (await WaitForAsync(tenant, status => status.Status == "Delivered")).EventId.Should().Be(dead.EventId);
    }

    private FakeReceiver Receiver(HttpStatusCode status)
    {
        var receiver = new FakeReceiver($"receiver-{Guid.NewGuid():N}.flow.test") { Status = status };
        Flow.Edge.Register(receiver.Host, request =>
        {
            receiver.Requests.Enqueue(request);
            return new HttpResponseMessage(receiver.Status);
        });
        return receiver;
    }

    private async Task<string> RegisterAsync(FlowTenant tenant, string host)
    {
        var command = new WebhookRegistrationCommand(Guid.NewGuid(), Guid.NewGuid(), 0, $"https://{host}/azoa",
            IsActive: true, RotateSecret: false, "Register the drain flow receiver.");
        using var response = await tenant.Client.PutAsJsonAsync("api/tenant/webhook-registration", command, JsonOptions);
        return (await ReadOkAsync<WebhookRegistrationMutationResult>(response)).NewSecret!;
    }

    /// <summary>Provisioning a child admits its participation grant, which records one consent notification.</summary>
    private async Task ProvisionChildAsync(FlowTenant tenant)
    {
        using var response = await tenant.Client.PostAsJsonAsync("api/tenant/users",
            new ProvisionChildModel { ExternalUserId = "drain-" + Guid.NewGuid().ToString("N") }, JsonOptions);
        await ReadOkAsync<ChildUserResponse>(response);
    }

    private async Task<ConsentWebhookEventStatus> StatusAsync(FlowTenant tenant, Guid eventId)
    {
        using var response = await tenant.Client.GetAsync($"{Events}/{eventId}");
        return await ReadOkAsync<ConsentWebhookEventStatus>(response);
    }

    private async Task<ConsentWebhookEventStatus> WaitForAsync(FlowTenant tenant, Func<ConsentWebhookEventStatus, bool> reached)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        IReadOnlyList<ConsentWebhookEventStatus> rows = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await tenant.Client.GetAsync($"{Events}?offset=0&limit=25");
            rows = await ReadOkAsync<List<ConsentWebhookEventStatus>>(response);
            var match = rows.FirstOrDefault(reached);
            if (match is not null) return match;
            await Task.Delay(500);
        }
        var seen = string.Join(", ", rows.Select(row => $"{row.Status}/{row.ErrorCode}/v{row.Version}"));
        throw new TimeoutException($"No consent webhook event reached the expected state in 30 s; saw [{seen}].{Environment.NewLine}{Flow.ServerLog.Tail()}");
    }

    /// <summary>Receiver-side verification written from the documented wire contract, independent of the node's signer.</summary>
    private static string Signature(string secret, string timestamp, string body)
    {
        var stamp = Encoding.UTF8.GetBytes(timestamp);
        var payload = Encoding.UTF8.GetBytes(body);
        var preimage = new byte[4 + stamp.Length + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(preimage, (uint)stamp.Length);
        stamp.CopyTo(preimage, 4);
        payload.CopyTo(preimage, 4 + stamp.Length);
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), preimage));
    }

    private sealed class FakeReceiver(string host)
    {
        public string Host { get; } = host;
        public volatile HttpStatusCode Status;
        public ConcurrentQueue<VendorRequest> Requests { get; } = new();
    }
}
