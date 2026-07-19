using System;
using System.Net.Http;
using AZOA.WebAPI.Core.Surreal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Extensions;

/// <summary>
/// DI registration helper for the homebake <c>SurrealForge.Client</c>
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
    private const string ClientName = "SurrealForge.Client";

    /// <summary>
    /// Register the homebake SurrealDB client (<c>SurrealForge.Client</c>)
    /// with the application's DI container. Reads connection settings from
    /// the <c>SurrealDb</c> configuration section by default.
    /// </summary>
    /// <param name="services">The DI container to extend.</param>
    /// <param name="configuration">Application configuration root.</param>
    /// <param name="configSectionName">
    /// Configuration section to bind <see cref="SurrealConnectionOptions"/> from
    /// (default: <c>"SurrealDb"</c>).
    /// </param>
    public static IServiceCollection AddSurrealForge(
        this IServiceCollection services,
        IConfiguration configuration,
        string configSectionName = "SurrealDb")
    {
        if (services      is null) throw new ArgumentNullException(nameof(services));
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));

        var connectionSection = configuration.GetSection(configSectionName);
        var authenticationScope = connectionSection["AuthenticationScope"];

        if (!string.IsNullOrWhiteSpace(authenticationScope) &&
            !string.Equals(
                authenticationScope.Trim(),
                SurrealRuntimeConfigurationGuard.DatabaseAuthenticationScope,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{configSectionName}:AuthenticationScope '{authenticationScope}' is unsupported. " +
                $"Omit it for root/development compatibility or use " +
                $"{SurrealRuntimeConfigurationGuard.DatabaseAuthenticationScope}.");
        }

        // Bind options from configuration. Missing section uses property defaults
        // (host http://localhost:8442, namespace/database "azoa"); that is
        // intentional for local dev — production deployments override every field.
        services.Configure<SurrealConnectionOptions>(
            connectionSection);

        // Register IHttpClientFactory so the HTTP transport gets a properly-managed
        // HttpClient instance (DNS refresh, connection pooling, no socket exhaustion).
        if (string.Equals(
                authenticationScope?.Trim(),
                SurrealRuntimeConfigurationGuard.DatabaseAuthenticationScope,
                StringComparison.OrdinalIgnoreCase))
        {
            var authenticationNamespace = RequireAuthenticationSetting(
                connectionSection, configSectionName, "Namespace");
            var authenticationDatabase = RequireAuthenticationSetting(
                connectionSection, configSectionName, "Database");

            services.AddHttpClient(ClientName, http =>
            {
                http.DefaultRequestHeaders.Add("Surreal-Auth-NS", authenticationNamespace);
                http.DefaultRequestHeaders.Add("Surreal-Auth-DB", authenticationDatabase);
            });
        }
        else
        {
            services.AddHttpClient(ClientName);
        }

        services.AddScoped<ISurrealConnection>(sp =>
        {
            var optionsAccessor = sp.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<SurrealConnectionOptions>>();
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            var http        = httpFactory.CreateClient(ClientName);
            return new HttpSurrealConnection(http, optionsAccessor.Value);
        });

        // HIGH#4 — ship the executor implementation the XML docs already
        // advertised. DefaultSurrealExecutor is stateless apart from its
        // ISurrealConnection reference, so the lifetime is bound to the
        // connection's (Scoped). Resolves through the same DI surface
        // safe-layer query types depend on.
        services.AddScoped<ISurrealExecutor, DefaultSurrealExecutor>();

        return services;
    }

    private static string RequireAuthenticationSetting(
        IConfigurationSection section,
        string sectionName,
        string key)
    {
        var value = section[key];
        if (!string.IsNullOrWhiteSpace(value))
            return value.Trim();

        throw new InvalidOperationException(
            $"{sectionName}:{key} is required when " +
            $"{sectionName}:AuthenticationScope is " +
            $"{SurrealRuntimeConfigurationGuard.DatabaseAuthenticationScope}.");
    }
}
