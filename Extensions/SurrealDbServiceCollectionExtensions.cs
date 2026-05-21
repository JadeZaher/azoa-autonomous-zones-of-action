using System;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Connection;
using Oasis.SurrealDb.Client.Query;

namespace OASIS.WebAPI.Extensions;

/// <summary>
/// DI registration helper for the homebake <c>Oasis.SurrealDb.Client</c>
/// package (surrealdb-client-package Phase 6, sub-wave 1.5a).
///
/// Replaces the previous direct registration of <c>SurrealDb.Net</c>'s
/// <c>ISurrealDbClient</c>. The package owns the engine boundary:
/// <list type="bullet">
///   <item><see cref="ISurrealConnection"/> -- HTTP transport (default).
///         WebSocket transport is reserved for sub-wave 1.5b LIVE queries.</item>
///   <item><see cref="ISurrealExecutor"/> -- parameterized query builder
///         execution surface that the safe-layer query types compose against.</item>
///   <item><see cref="SurrealConnectionOptions"/> -- endpoint / credentials /
///         pool sizing. Bound from the <c>SurrealDb</c> configuration section
///         by default. Override the section name with the
///         <paramref name="configSectionName"/> argument.</item>
/// </list>
///
/// <para>
/// The registration is intentionally light: a single options object, one
/// HTTP <see cref="ISurrealConnection"/> per scope, and one
/// <see cref="ISurrealExecutor"/> proxy. Tests substitute either side via
/// the standard <see cref="IServiceCollection"/> replace pattern; no extra
/// abstractions are introduced because the package interfaces are already
/// the seam.
/// </para>
/// </summary>
public static class SurrealDbServiceCollectionExtensions
{
    /// <summary>
    /// Register the homebake SurrealDB client (<c>Oasis.SurrealDb.Client</c>)
    /// with the application's DI container. Reads connection settings from
    /// the <c>SurrealDb</c> configuration section by default.
    /// </summary>
    /// <param name="services">The DI container to extend.</param>
    /// <param name="configuration">Application configuration root.</param>
    /// <param name="configSectionName">
    /// Configuration section to bind <see cref="SurrealConnectionOptions"/> from
    /// (default: <c>"SurrealDb"</c>).
    /// </param>
    public static IServiceCollection AddOasisSurrealDb(
        this IServiceCollection services,
        IConfiguration configuration,
        string configSectionName = "SurrealDb")
    {
        if (services      is null) throw new ArgumentNullException(nameof(services));
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));

        // Bind options from configuration. Missing section uses property defaults
        // (host http://localhost:8442, namespace/database "oasis"); that is
        // intentional for local dev — production deployments override every field.
        services.Configure<SurrealConnectionOptions>(
            configuration.GetSection(configSectionName));

        // Register IHttpClientFactory so the HTTP transport gets a properly-managed
        // HttpClient instance (DNS refresh, connection pooling, no socket exhaustion).
        services.AddHttpClient();

        services.AddScoped<ISurrealConnection>(sp =>
        {
            var optionsAccessor = sp.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<SurrealConnectionOptions>>();
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            var http        = httpFactory.CreateClient("Oasis.SurrealDb.Client");
            return new HttpSurrealConnection(http, optionsAccessor.Value);
        });

        return services;
    }
}
