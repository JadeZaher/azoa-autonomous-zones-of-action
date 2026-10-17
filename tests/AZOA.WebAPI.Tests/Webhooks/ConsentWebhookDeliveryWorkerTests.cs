using System.Net;
using System.Reflection;
using AZOA.WebAPI.Core.Webhooks;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Services.Signing;
using AZOA.WebAPI.Services.Webhooks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Event = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentWebhookEvent;

namespace AZOA.WebAPI.Tests.Webhooks;

public sealed class ConsentWebhookDeliveryWorkerTests
{
    [Theory]
    [InlineData(204, false)]
    [InlineData(503, false)]
    [InlineData(204, true)]
    public async Task Sends_exact_claimed_body_and_revision_without_reading_response_body(int status, bool finishFails)
    {
        var store = new Mock<IConsentWebhookDeliveryStore>();
        var protector = new WebhookSecretProtector(new WalletKeyService(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["AZOA:WalletEncryptionKey"] = "worker-fixture-key" }).Build()));
        var tenant = Guid.NewGuid(); var registration = Guid.NewGuid(); var id = Guid.NewGuid().ToString("N");
        var secret = WebhookSecretProtector.GenerateSecret();
        var lease = new WebhookDeliveryLease(id, 1, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddMinutes(1),
            tenant, registration, 1, "https://receiver.example/events", "{\"exact\":true}", id, 1, 7,
            protector.Protect(tenant, registration, 7, secret));
        store.Setup(item => item.ListCandidatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Event { Id = id, Version = 0 } });
        store.Setup(item => item.TryClaimAsync(id, 0, It.IsAny<TimeSpan>(), 8, It.IsAny<CancellationToken>())).ReturnsAsync(lease);
        var responseBody = new UnreadableContent();
        var handler = new InspectingHandler(async request =>
        {
            request.RequestUri!.AbsoluteUri.Should().Be(lease.Url);
            (await request.Content!.ReadAsStringAsync()).Should().Be(lease.BodyJson);
            request.Headers.GetValues("X-Azoa-Secret-Revision").Single().Should().Be("7");
            request.Headers.GetValues("X-Azoa-Idempotency-Id").Single().Should().Be(id);
            var timestamp = request.Headers.GetValues("X-Azoa-Timestamp").Single();
            request.Headers.GetValues("X-Azoa-Signature").Single().Should().Be(new WebhookHmacSigner().Sign(lease.BodyJson, timestamp, secret));
            return new HttpResponseMessage((HttpStatusCode)status) { Content = responseBody };
        });
        if (finishFails)
        {
            store.Setup(item => item.FinishAsync(lease, WebhookAttemptOutcome.Delivered, TimeSpan.Zero, null,
                It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("Database transport unavailable."));
            Func<Task> tick = () => Tick(store.Object, protector, handler);
            await tick.Should().ThrowAsync<HttpRequestException>().WithMessage("Database transport unavailable.");
            store.Verify(item => item.FinishAsync(lease, WebhookAttemptOutcome.Retry, It.IsAny<TimeSpan>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        else await Tick(store.Object, protector, handler);
        responseBody.ReadAttempted.Should().BeFalse();
        var outcome = status == 204 ? WebhookAttemptOutcome.Delivered : WebhookAttemptOutcome.Retry;
        store.Verify(item => item.FinishAsync(lease, outcome, It.IsAny<TimeSpan>(),
            status == 204 ? null : "HTTP_503", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Losing_claim_never_sends_http()
    {
        var store = new Mock<IConsentWebhookDeliveryStore>();
        var id = Guid.NewGuid().ToString("N");
        store.Setup(item => item.ListCandidatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Event { Id = id, Version = 0 } });
        var protector = new WebhookSecretProtector(new WalletKeyService(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["AZOA:WalletEncryptionKey"] = "worker-fixture-key" }).Build()));
        await Tick(store.Object, protector, new InspectingHandler(_ => throw new InvalidOperationException("No lease, no HTTP.")));
        store.Verify(item => item.SuspendUnavailableAsync(id, 0, 8, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(item => item.FinishAsync(It.IsAny<WebhookDeliveryLease>(), It.IsAny<WebhookAttemptOutcome>(),
            It.IsAny<TimeSpan>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Insufficient_lease_lifetime_never_starts_http_or_finishes_an_attempt()
    {
        var store = new Mock<IConsentWebhookDeliveryStore>();
        var protector = new WebhookSecretProtector(new WalletKeyService(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["AZOA:WalletEncryptionKey"] = "worker-fixture-key" }).Build()));
        var tenant = Guid.NewGuid(); var registration = Guid.NewGuid(); var id = Guid.NewGuid().ToString("N");
        var lease = new WebhookDeliveryLease(id, 1, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddMilliseconds(100),
            tenant, registration, 1, "https://receiver.example/events", "{}", id, 1, 1,
            protector.Protect(tenant, registration, 1, WebhookSecretProtector.GenerateSecret()));
        store.Setup(item => item.ListCandidatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Event { Id = id, Version = 0 } });
        store.Setup(item => item.TryClaimAsync(id, 0, It.IsAny<TimeSpan>(), 8, It.IsAny<CancellationToken>())).ReturnsAsync(lease);
        await Tick(store.Object, protector, new InspectingHandler(_ => throw new InvalidOperationException("Expired budget must not start HTTP.")));
        store.Verify(item => item.FinishAsync(It.IsAny<WebhookDeliveryLease>(), It.IsAny<WebhookAttemptOutcome>(),
            It.IsAny<TimeSpan>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static async Task Tick(IConsentWebhookDeliveryStore store, WebhookSecretProtector protector, HttpMessageHandler handler)
    {
        using var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(WebhookOptions.HttpClientName)).Returns(() => new HttpClient(handler));
        var guard = new WebhookSsrfGuard(); guard.UseResolver(_ => [IPAddress.Parse("8.8.8.8")]);
        using var worker = new ConsentWebhookDeliveryWorker(services.GetRequiredService<IServiceScopeFactory>(), clients.Object,
            guard, new WebhookHmacSigner(), protector, NullLogger<ConsentWebhookDeliveryWorker>.Instance,
            Options.Create(new WebhookOptions { Enabled = true }));
        await (Task)typeof(ConsentWebhookDeliveryWorker).GetMethod("RunTickAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(worker, [CancellationToken.None])!;
    }

    private sealed class InspectingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> inspect) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => inspect(request);
    }

    private sealed class UnreadableContent : HttpContent
    {
        public bool ReadAttempted { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            ReadAttempted = true;
            throw new InvalidOperationException("Receiver response bodies must not be buffered.");
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
