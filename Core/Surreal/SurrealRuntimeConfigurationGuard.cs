using Microsoft.Extensions.Configuration;

namespace AZOA.WebAPI.Core.Surreal;

/// <summary>Protects the production API from database-root and schema-runner configuration.</summary>
public static class SurrealRuntimeConfigurationGuard
{
    public const string RuntimeSectionName = "SurrealRuntime";
    public const string DatabaseAuthenticationScope = "Database";

    /// <summary>Chooses the isolated runtime section, with legacy development compatibility only.</summary>
    public static string ResolveRuntimeSectionName(IConfiguration configuration, bool isProduction)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.GetSection(RuntimeSectionName).Exists())
            return RuntimeSectionName;

        if (!isProduction)
            return "SurrealDb";

        throw new InvalidOperationException(
            "SurrealRuntime configuration is required in Production. The API must not bind its database " +
            "connection from the legacy SurrealDb section.");
    }

    /// <summary>Fails production startup when the API can receive schema or root authority.</summary>
    public static void GuardProduction(IConfiguration configuration, bool isProduction)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!isProduction)
            return;

        var runtime = configuration.GetSection(RuntimeSectionName);
        Require(runtime, "Endpoint");
        Require(runtime, "Namespace");
        Require(runtime, "Database");
        var user = Require(runtime, "User");
        Require(runtime, "Password");
        var authenticationScope = Require(runtime, "AuthenticationScope");

        if (!string.Equals(authenticationScope.Trim(), DatabaseAuthenticationScope, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"SurrealRuntime:AuthenticationScope must be {DatabaseAuthenticationScope} in Production.");

        if (string.Equals(user.Trim(), "root", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "SurrealRuntime:User must be a database-scoped runtime user, never root.");

        if (HasValue(configuration, "SurrealDb:User") || HasValue(configuration, "SurrealDb:Password"))
            throw new InvalidOperationException(
                "Production API configuration must not contain legacy SurrealDb credentials. " +
                "Keep schema credentials in the separate migration job.");

        if (!string.Equals(configuration["AZOA_SKIP_MIGRATIONS"], "1", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "AZOA_SKIP_MIGRATIONS=1 is required in Production. Apply schema changes from the " +
                "separate schema job; the API process must not run migrations.");
    }

    private static string Require(IConfigurationSection section, string key)
    {
        var value = section[key];
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        throw new InvalidOperationException(
            $"{RuntimeSectionName}:{key} is required in Production for the database-scoped runtime identity.");
    }

    private static bool HasValue(IConfiguration configuration, string key)
        => !string.IsNullOrWhiteSpace(configuration[key]);
}
