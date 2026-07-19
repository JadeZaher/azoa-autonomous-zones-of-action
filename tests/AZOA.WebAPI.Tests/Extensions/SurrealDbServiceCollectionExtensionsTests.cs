using AZOA.WebAPI.Extensions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AZOA.WebAPI.Tests.Extensions;

public sealed class SurrealDbServiceCollectionExtensionsTests
{
    [Fact]
    public void AddSurrealForge_DatabaseAuthenticationScope_AddsScopeHeaders()
    {
        var services = new ServiceCollection();
        var configuration = ConnectionConfiguration(("AuthenticationScope", "Database"));

        services.AddSurrealForge(configuration, "SurrealRuntime");

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient("SurrealForge.Client");
        client.DefaultRequestHeaders.GetValues("Surreal-Auth-NS").Should().Equal("azoa");
        client.DefaultRequestHeaders.GetValues("Surreal-Auth-DB").Should().Equal("runtime");
    }

    [Fact]
    public void AddSurrealForge_AuthenticationScopeOmitted_PreservesRootCompatibility()
    {
        var services = new ServiceCollection();
        var configuration = ConnectionConfiguration();

        services.AddSurrealForge(configuration, "SurrealRuntime");

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient("SurrealForge.Client");
        client.DefaultRequestHeaders.Contains("Surreal-Auth-NS").Should().BeFalse();
        client.DefaultRequestHeaders.Contains("Surreal-Auth-DB").Should().BeFalse();
    }

    [Fact]
    public void AddSurrealForge_UnsupportedAuthenticationScope_RejectsRegistration()
    {
        var services = new ServiceCollection();
        var configuration = ConnectionConfiguration(("AuthenticationScope", "Namespace"));

        var act = () => services.AddSurrealForge(configuration, "SurrealRuntime");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AuthenticationScope 'Namespace' is unsupported*");
    }

    private static IConfiguration ConnectionConfiguration(params (string Key, string Value)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["SurrealRuntime:Endpoint"] = "http://surrealdb.internal:8000",
            ["SurrealRuntime:Namespace"] = "azoa",
            ["SurrealRuntime:Database"] = "runtime",
            ["SurrealRuntime:User"] = "root",
            ["SurrealRuntime:Password"] = "root",
        };
        foreach (var (key, value) in overrides)
            values[$"SurrealRuntime:{key}"] = value;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
