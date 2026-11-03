using System.Net;
using System.Net.Http.Json;
using AZOA.WebAPI.IntegrationTests.Http.Infrastructure;
using FluentAssertions;

namespace AZOA.WebAPI.IntegrationTests.Http;

/// <summary>Checks the fake's per-target ingress accounting; see Http/AGENTS.md.</summary>
public sealed class IdentomatVendorAccountingTests
{
    [Fact]
    public async Task Result_ingress_counts_bad_auth_and_other_tokens_independently_but_not_deletion()
    {
        var edge = new VendorEdge();
        var vendor = new IdentomatVendor(edge, "company-key", "kyc-config", "kyb-config");
        using var client = new HttpClient(edge.CreateHandler())
        {
            BaseAddress = new Uri($"https://{IdentomatVendor.WidgetHost}")
        };
        const string first = "first-public-fixture-token";
        const string second = "second-public-fixture-token";
        vendor.IdentityResultRequestsFor(first).Should().Be(0);
        vendor.IdentityResultRequestsFor(second).Should().Be(0);

        using var denied = await client.PostAsJsonAsync("/external-api/result/",
            new { company_key = "wrong-key", session_token = first });
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        vendor.IdentityResultRequests.Should().Be(1);
        vendor.IdentityResultRequestsFor(first).Should().Be(1);
        vendor.IdentityResultRequestsFor(second).Should().Be(0);

        using var other = await client.PostAsJsonAsync("/external-api/result/",
            new { company_key = vendor.CompanyKey, session_token = second });
        other.StatusCode.Should().Be(HttpStatusCode.OK);
        vendor.IdentityResultRequests.Should().Be(2);
        vendor.IdentityResultRequestsFor(first).Should().Be(1);
        vendor.IdentityResultRequestsFor(second).Should().Be(1);

        using var deletion = await client.PostAsJsonAsync("/external-api/delete/",
            new { company_key = vendor.CompanyKey, session_token = first });
        deletion.StatusCode.Should().Be(HttpStatusCode.OK);
        vendor.IdentityResultRequests.Should().Be(2);
        vendor.IdentityResultRequestsFor(first).Should().Be(1);
        vendor.IdentityResultRequestsFor(second).Should().Be(1);

        using var accepted = await client.PostAsJsonAsync("/external-api/result/",
            new { company_key = vendor.CompanyKey, session_token = first });
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        vendor.IdentityResultRequests.Should().Be(3);
        vendor.IdentityResultRequestsFor(first).Should().Be(2);
        vendor.IdentityResultRequestsFor(second).Should().Be(1);
        var freshVendor = new IdentomatVendor(new VendorEdge(), "new-key", "new-kyc", "new-kyb");
        freshVendor.IdentityResultRequests.Should().Be(0);
        freshVendor.IdentityResultRequestsFor(first).Should().Be(0);
    }
}
