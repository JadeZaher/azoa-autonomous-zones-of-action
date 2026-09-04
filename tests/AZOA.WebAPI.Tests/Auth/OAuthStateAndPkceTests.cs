// SPDX-License-Identifier: UNLICENSED
//
// `state` correlation and PKCE are enforced by the framework OAuth handler that
// Program.cs registers for Google and GitHub. These tests drive that REAL handler in
// an in-memory host — the token and userinfo endpoints are a stub message handler, so
// there is no database, no container, and no network.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AZOA.WebAPI.Tests.Auth;

public class OAuthStateAndPkceTests
{
    private const string Scheme = "test-provider";
    private const string CallbackPath = "/auth/oauth/test-provider/signin";
    private const string AuthorizationEndpoint = "https://provider.test/authorize";

    // ── PKCE is actually requested ────────────────────────────────────────────

    /// <summary>
    /// The authorize redirect must carry a PKCE challenge using S256. Without this the
    /// authorization code is bearer-only and an interception replays it.
    /// </summary>
    [Fact]
    public async Task The_authorize_redirect_carries_an_S256_pkce_challenge()
    {
        using var host = await BuildHostAsync();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/challenge");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var query = HttpUtility.ParseQueryString(response.Headers.Location!.Query);

        query["code_challenge"].Should().NotBeNullOrWhiteSpace();
        query["code_challenge_method"].Should().Be("S256");
        query["state"].Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The verifier presented at the token endpoint must be the pre-image of the
    /// challenge sent at authorize time. This is the whole point of PKCE, and asserting
    /// it here proves the handler is genuinely binding the two legs together.
    /// </summary>
    [Fact]
    public async Task The_token_request_presents_the_verifier_matching_the_challenge()
    {
        var stub = new ProviderStub();
        using var host = await BuildHostAsync(stub);
        var client = host.GetTestClient();

        var challenge = await client.GetAsync("/challenge");
        var challengeQuery = HttpUtility.ParseQueryString(challenge.Headers.Location!.Query);
        var codeChallenge = challengeQuery["code_challenge"]!;

        var callback = await FollowCallbackAsync(client, challenge, code: "provider-auth-code");

        callback.StatusCode.Should().Be(HttpStatusCode.Redirect);
        stub.ReceivedCodeVerifier.Should().NotBeNullOrWhiteSpace(
            "the handler must send the verifier it generated at challenge time");

        S256(stub.ReceivedCodeVerifier!).Should().Be(codeChallenge,
            "the verifier must be the pre-image of the challenge");
    }

    /// <summary>
    /// A provider that rejects the code exchange — which is exactly how a real provider
    /// reports a PKCE verifier mismatch — must fail the sign-in, not fall through to a
    /// session. Google enforces the verifier server-side; the handler must honour a
    /// rejection rather than proceeding.
    /// </summary>
    [Fact]
    public async Task A_rejected_code_exchange_fails_the_sign_in()
    {
        var stub = new ProviderStub { RejectTokenExchange = true };
        using var host = await BuildHostAsync(stub);
        var client = host.GetTestClient();

        var challenge = await client.GetAsync("/challenge");
        var callback = await FollowCallbackAsync(client, challenge, code: "provider-auth-code");

        callback.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await callback.Content.ReadAsStringAsync()).Should().StartWith("remote-failure:");
    }

    // ── state correlation ─────────────────────────────────────────────────────

    /// <summary>
    /// A callback whose correlation cookie is missing must be refused. That cookie is
    /// what proves this callback belongs to the browser that started the flow; without
    /// the check, an attacker can complete a login in a victim's browser (login CSRF).
    /// </summary>
    [Fact]
    public async Task A_callback_without_the_correlation_cookie_is_rejected()
    {
        using var host = await BuildHostAsync();
        var client = host.GetTestClient();

        var challenge = await client.GetAsync("/challenge");
        var state = HttpUtility.ParseQueryString(challenge.Headers.Location!.Query)["state"]!;

        // Deliberately send NO cookies back.
        var callback = await client.GetAsync(
            $"{CallbackPath}?code=provider-auth-code&state={Uri.EscapeDataString(state)}");

        callback.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await callback.Content.ReadAsStringAsync()).Should().Contain("Correlation failed");
    }

    /// <summary>A tampered <c>state</c> cannot be unprotected and is refused.</summary>
    [Fact]
    public async Task A_callback_with_a_tampered_state_is_rejected()
    {
        using var host = await BuildHostAsync();
        var client = host.GetTestClient();

        var challenge = await client.GetAsync("/challenge");

        var callback = await FollowCallbackAsync(
            client, challenge, code: "provider-auth-code", overrideState: "not-the-issued-state");

        callback.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await callback.Content.ReadAsStringAsync()).Should().StartWith("remote-failure:");
    }

    /// <summary>A callback carrying no <c>state</c> at all is refused.</summary>
    [Fact]
    public async Task A_callback_with_no_state_is_rejected()
    {
        using var host = await BuildHostAsync();
        var client = host.GetTestClient();

        var challenge = await client.GetAsync("/challenge");
        var callback = await FollowCallbackAsync(
            client, challenge, code: "provider-auth-code", overrideState: string.Empty);

        callback.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// The state issued for one browser must not authenticate a DIFFERENT browser: the
    /// correlation cookie from session A plus the state from session B must not combine.
    /// </summary>
    [Fact]
    public async Task A_state_from_one_session_cannot_be_used_with_another_sessions_cookie()
    {
        using var host = await BuildHostAsync();
        var client = host.GetTestClient();

        var sessionA = await client.GetAsync("/challenge");
        var sessionB = await client.GetAsync("/challenge");

        var stateFromB = HttpUtility.ParseQueryString(sessionB.Headers.Location!.Query)["state"]!;

        // Session A's correlation cookie, session B's state.
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{CallbackPath}?code=provider-auth-code&state={Uri.EscapeDataString(stateFromB)}");
        foreach (var cookie in CookiesFrom(sessionA))
            request.Headers.Add("Cookie", cookie);

        var callback = await client.SendAsync(request);

        callback.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await callback.Content.ReadAsStringAsync()).Should().Contain("Correlation failed");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<HttpResponseMessage> FollowCallbackAsync(
        HttpClient client,
        HttpResponseMessage challenge,
        string code,
        string? overrideState = null)
    {
        var issuedState = HttpUtility.ParseQueryString(challenge.Headers.Location!.Query)["state"]!;
        var state = overrideState ?? issuedState;

        var url = string.IsNullOrEmpty(state)
            ? $"{CallbackPath}?code={Uri.EscapeDataString(code)}"
            : $"{CallbackPath}?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var cookie in CookiesFrom(challenge))
            request.Headers.Add("Cookie", cookie);

        return await client.SendAsync(request);
    }

    /// <summary>The correlation cookie the challenge set, replayed as a browser would.</summary>
    private static IEnumerable<string> CookiesFrom(HttpResponseMessage response)
        => response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(value => value.Split(';')[0])
            : [];

    private static string S256(string verifier)
        => Base64Url(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<IHost> BuildHostAsync(ProviderStub? stub = null)
    {
        stub ??= new ProviderStub();

        var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddAuthentication(options =>
                            {
                                options.DefaultScheme = "external-cookie";
                                options.DefaultChallengeScheme = Scheme;
                            })
                            .AddCookie("external-cookie")
                            .AddOAuth(Scheme, options =>
                            {
                                // Mirrors the Google/GitHub registration in Program.cs.
                                options.ClientId = "test-client-id";
                                options.ClientSecret = "test-client-secret";
                                options.SignInScheme = "external-cookie";
                                options.CallbackPath = CallbackPath;
                                options.AuthorizationEndpoint = AuthorizationEndpoint;
                                options.TokenEndpoint = "https://provider.test/token";
                                options.UserInformationEndpoint = "https://provider.test/userinfo";
                                options.UsePkce = true;
                                options.BackchannelHttpHandler = stub;

                                options.ClaimActions.MapJsonKey(
                                    System.Security.Claims.ClaimTypes.NameIdentifier, "id");

                                options.Events = new OAuthEvents
                                {
                                    // Surface the handler's rejection as a readable 400
                                    // instead of letting it bubble as an exception.
                                    OnRemoteFailure = context =>
                                    {
                                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                        context.HandleResponse();
                                        return context.Response.WriteAsync(
                                            "remote-failure: " + context.Failure?.Message);
                                    }
                                };
                            });
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseAuthentication();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapGet("/challenge", async context =>
                                await context.ChallengeAsync(
                                    Scheme,
                                    new AuthenticationProperties { RedirectUri = "/done" }));

                            endpoints.MapGet("/done", async context =>
                                await context.Response.WriteAsync("signed-in"));
                        });
                    });
            })
            .StartAsync();

        return host;
    }

    /// <summary>Stands in for the provider's token + userinfo endpoints.</summary>
    private sealed class ProviderStub : HttpMessageHandler
    {
        /// <summary>How a real provider reports a PKCE verifier mismatch.</summary>
        public bool RejectTokenExchange { get; init; }

        public string? ReceivedCodeVerifier { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("/token", StringComparison.Ordinal))
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var form = HttpUtility.ParseQueryString(body);
                ReceivedCodeVerifier = form["code_verifier"];

                if (RejectTokenExchange)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent(
                            """{"error":"invalid_grant","error_description":"code_verifier mismatch"}""",
                            System.Text.Encoding.UTF8,
                            "application/json"),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"access_token":"stub-access-token","token_type":"Bearer","expires_in":3600}""",
                        System.Text.Encoding.UTF8,
                        "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"provider-subject-1","login":"octocat"}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
