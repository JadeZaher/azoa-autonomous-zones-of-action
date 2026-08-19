using AZOA.WebAPI.Extensions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Tests.Extensions;

/// <summary>
/// Registration contract for the SurrealForge 1.0.0 SDK/CBOR path.
///
/// <para>
/// Every provider here is disposed with <c>await using</c>, not <c>using</c>:
/// the SDK's <c>SurrealDbClient</c> singleton implements <c>IAsyncDisposable</c>
/// and <b>not</b> <c>IDisposable</c>, and Microsoft's container throws
/// "…only implements IAsyncDisposable. Use DisposeAsync to dispose the
/// container." on a synchronous <c>Dispose()</c>.
/// </para>
/// </summary>
public sealed class SurrealDbServiceCollectionExtensionsTests
{
    private const string Endpoint = "http://surrealdb.internal:8000";

    [Fact]
    public async Task AddSurrealForge_DatabaseAuthenticationScope_AddsScopeHeaders()
    {
        var services = new ServiceCollection();
        var configuration = ConnectionConfiguration(
            ("AuthenticationScope", "Database"),
            ("User", "runtimeuser"),
            ("Password", "runtimepass"),
            // Database-scoped credentials cannot Basic-Auth over HTTP, so the
            // package mints a scoped JWT and needs a root issuer to do it.
            ("TokenIssuerUser", "root"),
            ("TokenIssuerPassword", "root"));

        services.AddSurrealForge(configuration, "SurrealRuntime");

        await using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(AZOA.WebAPI.Extensions.SurrealDbServiceCollectionExtensions.ResolveSurrealHttpClientName(Endpoint));
        client.DefaultRequestHeaders.GetValues("Surreal-Auth-NS").Should().Equal("azoa");
        client.DefaultRequestHeaders.GetValues("Surreal-Auth-DB").Should().Equal("runtime");
    }

    [Fact]
    public async Task AddSurrealForge_AuthenticationScopeOmitted_PreservesRootCompatibility()
    {
        var services = new ServiceCollection();
        var configuration = ConnectionConfiguration();

        services.AddSurrealForge(configuration, "SurrealRuntime");

        await using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(AZOA.WebAPI.Extensions.SurrealDbServiceCollectionExtensions.ResolveSurrealHttpClientName(Endpoint));
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

    [Fact]
    public void AddSurrealForge_ExplicitRootScope_IsAccepted()
    {
        var services = new ServiceCollection();
        var configuration = ConnectionConfiguration(("AuthenticationScope", "Root"));

        var act = () => services.AddSurrealForge(configuration, "SurrealRuntime");

        act.Should().NotThrow();
    }

    /// <summary>
    /// The connection the container hands out must be the SDK/CBOR one. A
    /// regression here is exactly the failure mode this migration exists to
    /// prevent: the suite would keep passing on the legacy JSON transport, where
    /// SurrealDB coerces text into typed columns and nothing is proven.
    /// </summary>
    [Fact]
    public async Task AddSurrealForge_ResolvesTheSdkCborConnection()
    {
        var services = new ServiceCollection();
        services.AddSurrealForge(ConnectionConfiguration(), "SurrealRuntime");

        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ISurrealConnection>()
            .Should().BeOfType<SurrealDbNetConnection>();
    }

    /// <summary>
    /// Program.cs decorates the executor by reading
    /// <c>ServiceDescriptor.ImplementationType</c> and re-creating the
    /// implementation through <c>ActivatorUtilities</c>. A factory registration
    /// would leave that null and break boot, so the shape is asserted here.
    /// </summary>
    [Fact]
    public void AddSurrealForge_RegistersExactlyOneExecutor_ByImplementationType()
    {
        var services = new ServiceCollection();
        services.AddSurrealForge(ConnectionConfiguration(), "SurrealRuntime");

        var descriptor = services.Should().ContainSingle(d => d.ServiceType == typeof(ISurrealExecutor)).Subject;
        descriptor.ImplementationType.Should().NotBeNull();
    }

    private static IConfiguration ConnectionConfiguration(params (string Key, string Value)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["SurrealRuntime:Endpoint"] = Endpoint,
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
