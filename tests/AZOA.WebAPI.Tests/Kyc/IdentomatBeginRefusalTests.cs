// SPDX-License-Identifier: UNLICENSED

using System.Net;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Kyc;
using AZOA.WebAPI.Services.HostedSessions;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Moq;

namespace AZOA.WebAPI.Tests.Kyc;

/// <summary>
/// F-M1 through the real adapter, transport and durable flow (only HTTP is faked): a definitive vendor
/// refusal (4xx, the documented 529 block) releases the admission so the person can retry, while a 5xx
/// or timeout stays quarantined because the create may have happened.
/// </summary>
public sealed class IdentomatBeginRefusalTests
{
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "released")]
    [InlineData(HttpStatusCode.Unauthorized, "released")]
    [InlineData(HttpStatusCode.TooManyRequests, "released-circuit-open")]
    [InlineData((HttpStatusCode)IdentomatHttpTransport.TooManyRequestsStatusCode, "released-circuit-open")]
    [InlineData(HttpStatusCode.InternalServerError, "quarantined")]
    [InlineData(HttpStatusCode.GatewayTimeout, "quarantined")]
    public async Task Only_definitive_refusals_release_the_admission(HttpStatusCode firstAnswer, string expected)
    {
        var vendor = new ScriptedVendor(firstAnswer);
        var options = IdentomatTestFactory.Options();
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(factory => factory.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(vendor));
        var transport = new IdentomatHttpTransport(clients.Object, new IdentomatRateBudget(options), options);
        var store = new InMemoryHostedSessionStore();
        var flow = new HostedSessionFlow(store, Mock.Of<IHostedSessionOutcomeHandler>());
        var provider = IdentomatTestFactory.Create(options, transport, flow: flow);
        var user = Guid.NewGuid();

        var first = await provider.BeginSessionAsync(user);
        var retry = await provider.BeginSessionAsync(user);

        first.IsError.Should().BeTrue();
        switch (expected)
        {
            case "released":
                first.Code.Should().NotBe(AzoaErrorCodes.Conflict, "nothing was created, so this is not an operator case");
                retry.IsError.Should().BeFalse("the released admission lets the person retry");
                vendor.BeginCalls.Should().Be(2);
                break;
            case "released-circuit-open":
                // Released, but the observed ban parks the shared budget: the retry is refused locally, unsent.
                first.Code.Should().NotBe(AzoaErrorCodes.Conflict);
                retry.IsError.Should().BeTrue();
                retry.Code.Should().NotBe(AzoaErrorCodes.Conflict);
                vendor.BeginCalls.Should().Be(1);
                break;
            default:
                first.Code.Should().Be(AzoaErrorCodes.Conflict);
                retry.IsError.Should().BeTrue("an uncertain create is never repeated");
                retry.Code.Should().Be(AzoaErrorCodes.Conflict);
                vendor.BeginCalls.Should().Be(1);
                break;
        }
    }

    /// <summary>Answers the first <c>/begin</c> with a scripted status, then succeeds with the documented shape.</summary>
    private sealed class ScriptedVendor(HttpStatusCode firstAnswer) : HttpMessageHandler
    {
        public int BeginCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            BeginCalls++;
            if (BeginCalls == 1)
                return Task.FromResult(new HttpResponseMessage(firstAnswer));
            var id = "idm-" + Guid.NewGuid().ToString("N");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"id":"{{id}}","url":"https://widget.identomat.com/?session_token={{id}}"}""",
                    System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
