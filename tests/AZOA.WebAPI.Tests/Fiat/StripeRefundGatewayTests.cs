using System.Net;
using AZOA.WebAPI.Providers.Fiat;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AZOA.WebAPI.Tests.Fiat;

public sealed class StripeRefundGatewayTests
{
    [Fact]
    public async Task RefundUsesOriginalPaymentAmountAndStableProviderKey()
    {
        using var handler = new RefundHttp();
        using var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient(StripeGateway.HttpClientName)).Returns(client);
        var gateway = new StripeGateway(factory.Object, NullLogger<StripeGateway>.Instance);
        var credentials = new StripeCredentials("https://stripe.test", "sk_test_fixture", TimeSpan.FromSeconds(5));
        (await gateway.CreateRefundAsync(credentials, "pi_original", 1234, "intent", "stable-refund-key")).Result!.Id.Should().Be("re_original");
        handler.Form.Should().Contain("payment_intent=pi_original").And.Contain("amount=1234")
            .And.Contain("metadata%5Bazoa_intent_id%5D=intent");
        handler.IdempotencyKey.Should().Be("stable-refund-key");
        (await gateway.GetRefundAsync(credentials, "re_original")).Result!.Status.Should().Be("succeeded");
        handler.LastPath.Should().Be("/v1/refunds/re_original");
    }

    private sealed class RefundHttp : HttpMessageHandler
    {
        public string? Form { get; private set; }
        public string? IdempotencyKey { get; private set; }
        public string? LastPath { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri!.AbsolutePath;
            if (request.Content is not null) Form = await request.Content.ReadAsStringAsync(cancellationToken);
            if (request.Headers.TryGetValues("Idempotency-Key", out var keys)) IdempotencyKey = keys.Single();
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"id":"re_original","payment_intent":"pi_original","amount":1234,"currency":"usd","status":"succeeded","metadata":{"azoa_intent_id":"intent"}}""") };
        }
    }
}
