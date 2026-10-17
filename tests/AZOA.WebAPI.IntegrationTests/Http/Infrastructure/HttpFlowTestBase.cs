using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Http.Infrastructure;

/// <summary>A tenant onboarded the way production does it, holding its real API-key client.</summary>
public sealed record FlowTenant(Guid TenantId, HttpClient Client) : IDisposable
{
    public void Dispose() => Client.Dispose();
}

/// <summary>
/// Base for HTTP flow tests: real host + real SurrealDB namespace (from <see cref="IntegrationTestBase"/>)
/// plus onboarding and vendor-callback helpers that only use public routes. See <c>Http/AGENTS.md</c>.
/// </summary>
public abstract class HttpFlowTestBase : IntegrationTestBase, IClassFixture<HttpFlowWebApplicationFactory>
{
    protected HttpFlowTestBase(HttpFlowWebApplicationFactory factory) : base(factory)
    {
        Flow = factory;
        Operator = factory.CreateOperatorClient();
        Anonymous = factory.CreateClient();
    }

    protected HttpFlowWebApplicationFactory Flow { get; }

    /// <summary>Node operator with a fresh session (satisfies <c>RecentNodeOperatorSession</c>).</summary>
    protected HttpClient Operator { get; }

    /// <summary>No credentials at all: what a vendor callback or webhook delivery looks like.</summary>
    protected HttpClient Anonymous { get; }

    /// <summary>
    /// Registers a person, then has the operator issue the fixed-scope tenant key
    /// (<c>POST api/apikey/tenant</c>). The returned client authenticates through the real
    /// <c>ApiKeyAuthenticationHandler</c>, not the test principal.
    /// </summary>
    protected async Task<FlowTenant> OnboardTenantAsync()
    {
        var owner = await SeedUserAsync();
        using var issued = await Operator.PostAsJsonAsync("api/apikey/tenant",
            new CreateTenantApiKeyRequest { TenantUserId = owner.Id, Name = "flow-test-tenant" }, JsonOptions);
        var key = await ReadOkAsync<CreateApiKeyResponse>(issued);
        return new FlowTenant(owner.Id, Factory.CreateApiKeyClient(key.Key));
    }

    /// <summary>Operator enables the Identomat profile and assigns it to <paramref name="tenantId"/>.</summary>
    protected async Task AssignIdentomatToTenantAsync(Guid tenantId)
    {
        using var profile = await Operator.PutAsJsonAsync("api/operator/kyc/providers/identomat",
            new UpdateKycProviderProfileRequest
            {
                DisplayName = "Identomat",
                AdapterKey = "identomat",
                Enabled = true,
                PolicyVersion = "identomat-hosted-v1",
                AssuranceLevel = "document-and-liveness",
            }, JsonOptions);
        profile.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict);

        using var selection = await Operator.PutAsJsonAsync($"api/operator/tenants/{tenantId}/kyc-provider",
            new SelectTenantKycProviderRequest { ProviderKey = "identomat" }, JsonOptions);
        await ReadOkAsync<object>(selection);
    }

    /// <summary>Posts a vendor callback exactly as the vendor would: anonymous, raw JSON.</summary>
    protected Task<HttpResponseMessage> PostCallbackAsync(string route, string json)
        => Anonymous.PostAsync(route, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

    /// <summary>Signs and posts a Stripe webhook delivery.</summary>
    protected Task<HttpResponseMessage> PostStripeWebhookAsync(string payload, string? signatureHeader = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "api/fiat/webhook/stripe")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Stripe-Signature", signatureHeader ?? Flow.Stripe.SignatureHeader(payload));
        return Anonymous.SendAsync(request);
    }

    /// <summary>Asserts the status code, showing the response body and recent server warnings on failure.</summary>
    protected async Task ShouldReturnAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, $"{body}{Environment.NewLine}Server log:{Environment.NewLine}{Flow.ServerLog.Tail()}");
    }

    /// <summary>Asserts a 200 AZOAResult envelope and returns its payload, surfacing the body on failure.</summary>
    protected async Task<T> ReadOkAsync<T>(HttpResponseMessage response)
    {
        await ShouldReturnAsync(response, HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        var envelope = JsonSerializer.Deserialize<AZOAResult<T>>(body, JsonOptions);
        envelope.Should().NotBeNull(body);
        envelope!.IsError.Should().BeFalse(body);
        return envelope.Result!;
    }

    /// <summary>The <c>result</c> member of an AZOAResult body, for shape comparisons against frontend fixtures.</summary>
    protected static async Task<JsonElement> ResultElementAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("result").Clone();
    }

    /// <summary>Reads committed rows straight from this class's SurrealDB namespace.</summary>
    protected async Task<IReadOnlyList<T>> RowsAsync<T>(SurrealQuery<T> query) where T : class, ISurrealRecord, new()
    {
        var executor = await CreateExecutorAsync(TestNamespace);
        return (await executor.QueryAsync<T>(query)).ToArray();
    }

    /// <summary>Applies one typed write (e.g. <c>SurrealWriter.UpdateOnly</c>) to arrange state no public route can.</summary>
    protected async Task WriteAsync(SurrealQuery statement)
    {
        var executor = await CreateExecutorAsync(TestNamespace);
        (await executor.ExecuteAsync(statement)).EnsureAllOk();
    }
}
