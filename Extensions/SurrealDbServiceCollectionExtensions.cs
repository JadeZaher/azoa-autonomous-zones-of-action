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
/// DI registration helper for the <c>SurrealForge.Client</c> package.
///
/// <para>
/// As of SurrealForge 1.0.0 this delegates to the package's own
/// <c>AddSurrealForgeSdk</c>, which runs on the official <c>SurrealDb.Net</c>
/// SDK and therefore on the <b>CBOR</b> wire format. The previous registration
/// hand-built <c>HttpSurrealConnection</c> (SurrealForge's own JSON transport)
/// and is gone: JSON let SurrealDB coerce text into typed columns, CBOR does
/// not, so the two are not interchangeable and only one of them can be the
/// tested path.
/// </para>
///
/// What the package registers:
/// <list type="bullet">
///   <item><c>ISurrealDbClient</c> (SDK) + <see cref="ISurrealConnection"/>
///         (<c>SurrealDbNetConnection</c>) -- singletons.</item>
///   <item><see cref="ISurrealExecutor"/> -- registered <b>by implementation
///         type</b>, which <c>Program.cs</c> relies on when it swaps in the
///         OTEL-instrumented decorator.</item>
///   <item><see cref="SurrealConnectionOptions"/> -- bound eagerly from the
///         supplied configuration section (the package takes an instance, not
///         an <c>IOptions&lt;&gt;</c>). The <c>IOptions&lt;&gt;</c> surface is
///         still registered for anything that reads it.</item>
/// </list>
///
/// <para>
/// <b>Container teardown must be asynchronous.</b> The SDK's
/// <c>SurrealDbClient</c> implements <c>IAsyncDisposable</c> and not
/// <c>IDisposable</c>; Microsoft's container throws on a synchronous
/// <c>Dispose()</c> of such a singleton. Generic-host and ASP.NET Core apps
/// already shut down asynchronously -- hand-built providers need
/// <c>await using</c>.
/// </para>
/// </summary>
public static class SurrealDbServiceCollectionExtensions
{
    /// <summary>
    /// Register the SurrealForge SDK/CBOR client with the application's DI
    /// container, reading connection settings from the <c>SurrealDb</c>
    /// configuration section by default.
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
        var configuredScope   = connectionSection["AuthenticationScope"]?.Trim();

        // Root is now an explicitly supported value, not merely an omission: the
        // package's SurrealAuthenticationScope defaults to Root, and root Basic
        // Auth is the one credential shape SurrealDB accepts over HTTP. Namespace
        // scope stays unsupported -- nothing here is provisioned for a
        // namespace-scoped identity.
        var isDatabaseScope = string.Equals(
            configuredScope,
            SurrealRuntimeConfigurationGuard.DatabaseAuthenticationScope,
            StringComparison.OrdinalIgnoreCase);
        var isRootScope = string.Equals(configuredScope, "Root", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(configuredScope) && !isDatabaseScope && !isRootScope)
        {
            throw new InvalidOperationException(
                $"{configSectionName}:AuthenticationScope '{configuredScope}' is unsupported. " +
                $"Omit it for root/development compatibility, or use Root or " +
                $"{SurrealRuntimeConfigurationGuard.DatabaseAuthenticationScope}.");
        }

        // Keep the IOptions<> surface (diagnostics, tests, anything binding late).
        services.Configure<SurrealConnectionOptions>(connectionSection);

        // AddSurrealForgeSdk takes a concrete options instance because it reads
        // Endpoint/AuthenticationScope at *registration* time to pick the transport
        // and the auth strategy -- so bind eagerly rather than deferring to
        // IOptions. A missing section falls back to property defaults
        // (http://localhost:8442, namespace/database "test"); production overrides
        // every field.
        var options = new SurrealConnectionOptions();
        connectionSection.Bind(options);

        if (isDatabaseScope)
        {
            // Preserved from the pre-1.0.0 registration. These are this
            // application's own audit headers; the SDK independently sends the
            // native surreal-NS / surreal-DB scoping headers on every request, so
            // they are informational rather than load-bearing. Attaching them
            // through ConfigureHttpClient is what puts them on the client the SDK
            // actually uses -- its HttpClient name is derived from the endpoint,
            // so a separately-named AddHttpClient registration would never be seen.
            var authenticationNamespace = RequireAuthenticationSetting(
                connectionSection, configSectionName, "Namespace");
            var authenticationDatabase = RequireAuthenticationSetting(
                connectionSection, configSectionName, "Database");

            options.ConfigureHttpClient = http =>
            {
                http.DefaultRequestHeaders.Remove("Surreal-Auth-NS");
                http.DefaultRequestHeaders.Remove("Surreal-Auth-DB");
                http.DefaultRequestHeaders.Add("Surreal-Auth-NS", authenticationNamespace);
                http.DefaultRequestHeaders.Add("Surreal-Auth-DB", authenticationDatabase);
            };
        }

        services.AddSurrealForgeSdk(options);

        return services;
    }

    /// <summary>
    /// Name of the <see cref="HttpClient"/> the SDK resolves for
    /// <paramref name="endpoint"/> -- the one the registration above decorates.
    /// Exposed so tests assert against the real client rather than a name this
    /// application invented.
    /// </summary>
    public static string ResolveSurrealHttpClientName(string endpoint)
        => SurrealForge.Client.SurrealDbServiceCollectionExtensions
            .ResolveSdkHttpClientName(endpoint);

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
