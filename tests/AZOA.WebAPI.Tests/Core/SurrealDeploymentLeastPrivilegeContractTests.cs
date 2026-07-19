using System.Text.Json;
using System.Security.Cryptography;
using AZOA.WebAPI.Core.Surreal;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace AZOA.WebAPI.Tests.Core;

public sealed class SurrealDeploymentLeastPrivilegeContractTests
{
    [Fact]
    public void KycCompatibilityBaseline_RetainsTheShippedChecksum()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "Persistence",
            "SurrealDb",
            "CompatibilityBaselines",
            "kyc_submission.surql");

        var checksum = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            .ToLowerInvariant();

        checksum.Should().Be("aad029ce96cc6009b147b5022d275abb42ae4d0a7011261f6e63a6dc633e7ac2");
    }

    [Fact]
    public void ProductionEntrypoint_RequiresExternalMigrationsAndNeverForcesChecksums()
    {
        var entrypoint = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "docker-entrypoint.sh"));

        entrypoint.Should().Contain("Production refuses API-boot migrations")
            .And.Contain("DEFINE USER OVERWRITE")
            .And.Contain("ROLES EDITOR")
            .And.Contain("setpriv --reuid")
            .And.NotContain("--force");
    }

    [Fact]
    public void ProductionEntrypoint_AllowsQuotedOwnerPunctuationButKeepsRuntimePasswordUrlSafe()
    {
        var entrypoint = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "docker-entrypoint.sh"));
        var schemaValidation = ShellFunction(entrypoint, "require_schema_job_config");
        var runtimeProvisioning = ShellFunction(entrypoint, "provision_runtime_user");

        ShellFunction(entrypoint, "is_strong_printable_secret")
            .Should().Contain("[ \"${#1}\" -ge 32 ]")
            .And.Contain("*[![:print:]]*");
        schemaValidation.Should()
            .Contain("grep -Eq '^[A-Za-z0-9_][A-Za-z0-9_-]{2,63}$'")
            .And.Contain("is_strong_printable_secret \"$SURREALFORGE_PASS\"")
            .And.NotContain(
                "printf '%s' \"$SURREALFORGE_PASS\" | grep -Eq '^[A-Za-z0-9._~-]{32,}$'");
        runtimeProvisioning.Should().Contain(
            "printf '%s' \"$runtime_user\" | grep -Eq '^[A-Za-z][A-Za-z0-9_]{2,63}$'")
            .And.Contain(
                "printf '%s' \"$runtime_pass\" | grep -Eq '^[A-Za-z0-9._~-]{32,}$'");
    }

    [Fact]
    public void RailwayTemplate_SeparatesSchemaOwnerFromDatabaseScopedRuntimeCredentials()
    {
        var path = Path.Combine(FindRepositoryRoot(), "deploy", "railway", "template.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var services = document.RootElement.GetProperty("services").EnumerateArray().ToArray();
        var databaseVariables = services.Single(service => service.GetProperty("name").GetString() == "surrealdb")
            .GetProperty("variables");
        var databaseService = services.Single(service => service.GetProperty("name").GetString() == "surrealdb");
        var schemaService = services.Single(service => service.GetProperty("name").GetString() == "azoa-schema");
        var schemaVariables = schemaService.GetProperty("variables");
        var apiService = services.Single(service => service.GetProperty("name").GetString() == "azoa-api");
        var apiVariables = apiService.GetProperty("variables");
        var frontendService = services.Single(service => service.GetProperty("name").GetString() == "azoa-frontend");

        databaseVariables.GetProperty("SURREAL_USER").GetString().Should().Be("azoa_schema_owner");
        databaseVariables.GetProperty("SURREAL_PASS").GetString().Should().Be("${{secret(48)}}");
        databaseVariables.GetProperty("PORT").GetString().Should().Be("8000");
        databaseVariables.GetProperty("RAILWAY_RUN_UID").GetString().Should().Be("0");
        databaseVariables.GetProperty("SURREAL_BIND").GetString().Should().Be("0.0.0.0:8000");
        databaseVariables.GetProperty("SURREAL_PATH").GetString().Should().Be("rocksdb:///data/db");
        databaseService.GetProperty("source").GetProperty("image").GetString().Should().Be(
            "docker.io/surrealdb/surrealdb@sha256:5757ed157c13b539bdc23a798ba2db1ffba6026deb3d15513058bffc77754a60");
        databaseService.GetProperty("deploy").GetProperty("startCommand").GetString()
            .Should().Be("/surreal start");
        schemaVariables.GetProperty("SURREALFORGE_USER").GetString()
            .Should().Be("${{surrealdb.SURREAL_USER}}");
        schemaVariables.GetProperty("SURREALFORGE_PASS").GetString()
            .Should().Be("${{surrealdb.SURREAL_PASS}}");
        schemaVariables.GetProperty("AZOA_RUNTIME_USER").GetString().Should().Be("azoa_runtime");
        schemaVariables.GetProperty("AZOA_RUNTIME_PASSWORD").GetString().Should().Be("${{secret(48)}}");
        apiVariables.GetProperty("SurrealRuntime__User").GetString()
            .Should().Be("${{azoa-schema.AZOA_RUNTIME_USER}}");
        apiVariables.GetProperty("SurrealRuntime__Password").GetString()
            .Should().Be("${{azoa-schema.AZOA_RUNTIME_PASSWORD}}");
        apiVariables.GetProperty("Blockchain__Bridge__RealValueEnabled").GetString()
            .Should().Be("false");
        apiVariables.GetProperty("NodeOperator__Username").GetString().Should().Be("node-operator");
        apiVariables.GetProperty("NodeOperator__Password").GetString().Should().Be("${{secret(48)}}");
        apiVariables.GetProperty("NodeOperator__CredentialRevision").GetString().Should().Be("1");
        apiVariables.GetProperty("NodeOperator__SessionMinutes").GetString().Should().Be("20");
        apiVariables.GetProperty("Kyc__Provider").GetString().Should().Be("unavailable");
        apiVariables.GetProperty("Kyc__ApprovalPolicy__AllowManualInDevelopment").GetString().Should().Be("false");
        apiVariables.GetProperty("Kyc__VeriffApiKey").GetString().Should().BeEmpty();
        apiVariables.GetProperty("Kyc__Hosted__ApiKey").GetString().Should().BeEmpty();
        apiVariables.GetProperty("Kyc__Hosted__WebhookSecret").GetString().Should().BeEmpty();
        apiVariables.GetProperty("Kyc__Hosted__BaseUrl").GetString().Should().BeEmpty();
        apiVariables.GetProperty("RAILWAY_RUN_UID").GetString().Should().Be("0");
        schemaService.GetProperty("source").GetProperty("image").GetString()
            .Should().Be(apiService.GetProperty("source").GetProperty("image").GetString());
        schemaService.GetProperty("deploy").GetProperty("startCommand").GetString()
            .Should().Be("/usr/local/bin/docker-entrypoint.sh schema");
        frontendService.GetProperty("source").GetProperty("image").GetString()
            .Should().Be("<PROMOTED_FRONTEND_IMAGE_REFERENCE>");

        var resolvedProductionConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SurrealRuntime:Endpoint"] = "http://surrealdb.internal:8000",
                ["SurrealRuntime:Namespace"] = apiVariables.GetProperty("SurrealRuntime__Namespace").GetString(),
                ["SurrealRuntime:Database"] = apiVariables.GetProperty("SurrealRuntime__Database").GetString(),
                ["SurrealRuntime:User"] = "generated-runtime-user",
                ["SurrealRuntime:Password"] = "generated-runtime-password",
                ["AZOA_SKIP_MIGRATIONS"] = apiVariables.GetProperty("AZOA_SKIP_MIGRATIONS").GetString()
            })
            .Build();

        var guard = () => SurrealRuntimeConfigurationGuard.GuardProduction(
            resolvedProductionConfig, isProduction: true);
        guard.Should().NotThrow();

        var template = File.ReadAllText(path);
        template.Should().Contain("<PROMOTED_API_IMAGE_REFERENCE>")
            .And.Contain("<PROMOTED_FRONTEND_IMAGE_REFERENCE>")
            .And.NotContain("\"branch\"")
            .And.NotContain("SurrealDb__User")
            .And.NotContain("SurrealDb__Password");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AZOA.WebAPI.csproj")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the AZOA repository root.");
    }

    private static string ShellFunction(string script, string name)
    {
        script = script.ReplaceLineEndings("\n");
        var start = script.IndexOf($"{name}() {{", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var end = script.IndexOf("\n}\n", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        return script[start..(end + 3)];
    }
}
