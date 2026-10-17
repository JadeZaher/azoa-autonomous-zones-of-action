using System.Net;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Services.Fiat;
using AZOA.WebAPI.Services.Fiat.OffRamp;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Fiat;

public sealed class StripeBankPayoutGatewayTests
{
    [Fact]
    public async Task Account_without_nonexistent_livemode_field_uses_live_credentials_and_remains_available()
    {
        var subject = Guid.NewGuid();
        var options = new FiatOptions { Enabled = true, Environment = RailEnvironmentNames.Live };
        options.Live.IsEnabled = true;
        options.Live.SecretKey = "sk_live_unit_test_fixture";
        options.Live.WebhookSecret = "whsec_unit_test_fixture";
        options.Checkout.AllowedReturnOrigins.Add("https://example.test");
        var sessions = new Mock<IHostedSessionStore>();
        sessions.Setup(x => x.ClaimAdmissionAsync(It.IsAny<HostedSession>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<HostedSession>.Success(new HostedSession
            { SubjectUserId = subject.ToString("N"), ProviderSessionId = "acct_owner" }));
        var gateway = new Mock<IStripeConnectGateway>(MockBehavior.Strict);
        gateway.Setup(x => x.GetConnectedAccountAsync(
                It.Is<StripeCredentials>(c => c.SecretKey == options.Live.SecretKey), "acct_owner", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<StripeConnectedAccount>.Success(StripeConnectGateway.ParseConnectedAccount(
                """{"id":"acct_owner","payouts_enabled":true,"details_submitted":true}""")!));
        var service = new PayoutAccountService(options, new OffRampOptions
        {
            Enabled = true, OnboardingReturnUrl = "https://example.test/return",
            OnboardingRefreshUrl = "https://example.test/refresh",
        }, gateway.Object, sessions.Object, NullLogger<PayoutAccountService>.Instance);

        var result = await service.RequireReadyAccountAsync(subject);
        result.IsError.Should().BeFalse();
        result.Result.Should().Be("acct_owner");
        gateway.VerifyAll();
    }

    [Fact]
    public async Task Bank_payout_calls_use_connected_account_scope_and_stable_idempotency()
    {
        var transport = new Transport();
        using var client = new HttpClient(transport);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient(StripeGateway.HttpClientName)).Returns(client);
        var gateway = new StripeConnectGateway(factory.Object, NullLogger<StripeConnectGateway>.Instance);
        var credentials = new StripeCredentials("https://api.stripe.test", "sk_test_fixture", TimeSpan.FromSeconds(5));
        var manual = await gateway.ConfigureManualPayoutsAsync(credentials, "acct_owner");
        var created = await gateway.CreateBankPayoutAsync(credentials, new StripeTransferRequest
        {
            ConnectedAccountId = "acct_owner", PayoutId = "intent", AmountMinorUnits = 500,
            Currency = "USD", IdempotencyKey = "stable-request",
        });
        var fetched = await gateway.GetBankPayoutAsync(credentials, "acct_owner", "po_bank");

        manual.Result!.PayoutSchedule.Should().Be("manual");
        created.Result!.PayoutId.Should().Be("po_bank");
        fetched.Result!.Status.Should().Be("pending");
        transport.Calls[0].Path.Should().Be("/v1/accounts/acct_owner");
        transport.Calls[0].Body.Should().Contain("settings%5Bpayouts%5D%5Bschedule%5D%5Binterval%5D=manual");
        transport.Calls[1].Path.Should().Be("/v1/payouts");
        transport.Calls[1].Account.Should().Be("acct_owner");
        transport.Calls[1].Idempotency.Should().Be("stable-request");
        transport.Calls[1].Body.Should().Contain("metadata%5Bazoa_intent_id%5D=intent");
        transport.Calls[2].Path.Should().Be("/v1/payouts/po_bank");
        transport.Calls[2].Account.Should().Be("acct_owner");
    }

    [Fact]
    public void Connected_account_event_retains_account_and_payout_correlation()
    {
        var id = Guid.NewGuid().ToString("N");
        var envelope = StripeEventEnvelope.TryParse($$"""
            {"id":"evt_bank","type":"payout.paid","account":"acct_owner","data":{"object":{
              "id":"po_bank","object":"payout","metadata":{"azoa_intent_id":"{{id}}"}}}}
            """);
        envelope!.ProviderAccountId.Should().Be("acct_owner");
        envelope.ObjectId.Should().Be("po_bank");
        envelope.IntentId.Should().Be(Guid.Parse(id));
    }

    private sealed record Call(string Path, string? Account, string? Idempotency, string Body);

    private sealed class Transport : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add(new(request.RequestUri!.AbsolutePath,
                request.Headers.TryGetValues("Stripe-Account", out var accounts) ? accounts.Single() : null,
                request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            var json = request.RequestUri.AbsolutePath.StartsWith("/v1/accounts", StringComparison.Ordinal)
                ? """{"id":"acct_owner","settings":{"payouts":{"schedule":{"interval":"manual"}}}}"""
                : """{"id":"po_bank","status":"pending","amount":500,"currency":"usd","metadata":{"azoa_intent_id":"intent"}}""";
            return new(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
}
