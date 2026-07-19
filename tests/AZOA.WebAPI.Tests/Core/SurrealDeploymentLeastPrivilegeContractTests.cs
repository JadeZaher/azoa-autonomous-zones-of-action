using System.Text.Json;
using System.Security.Cryptography;
using AZOA.WebAPI.Core.Surreal;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace AZOA.WebAPI.Tests.Core;

public sealed class SurrealDeploymentLeastPrivilegeContractTests
{
    [Theory]
    [InlineData("admin_bootstrap_state.surql", "89531eaf99831478f07018f63fbd5d8e20d10296283d258f350ad6f8dc7c6483")]
    [InlineData("bridge_tx.surql", "eb42fc6b723d7fc91ba95e8bddef58d9c994b07ad89a96d20a291ef55f584084")]
    [InlineData("holon.surql", "befd3f9dd6e9a15a8f6dcc278153d028c1da1629b836bf2f15621424bfd7605e")]
    [InlineData("kyc_submission.surql", "aad029ce96cc6009b147b5022d275abb42ae4d0a7011261f6e63a6dc633e7ac2")]
    [InlineData("operation_log.surql", "f67cb9da11f239416dabb42ab9839f9615bb782aa586688d6ce95c8da7b8994c")]
    [InlineData("swap_state.surql", "40f8f1ba577fbb6511da0432c3a0f2616e8b28e35e0477d7bc69a0808ea2ef99")]
    public void CompatibilityBaseline_RetainsTheShippedChecksum(string fileName, string expectedChecksum)
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "Persistence",
            "SurrealDb",
            "CompatibilityBaselines",
            fileName);

        var checksum = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            .ToLowerInvariant();

        checksum.Should().Be(expectedChecksum);
    }

    [Fact]
    public void CompatibilityBaselines_KeepForwardMigrationsForCurrentGeneratedSchema()
    {
        var migrations = Path.Combine(FindRepositoryRoot(), "Persistence", "SurrealDb", "Migrations");
        var holon = File.ReadAllText(Path.Combine(
            migrations,
            "20260718_135000__add_holon_transfer_reservation.surql"));
        var confirmation = File.ReadAllText(Path.Combine(
            migrations,
            "20260718_136000__add_pending_confirmation_states.surql"));

        holon.Should().Contain("transfer_reservation_key")
            .And.Contain("transfer_target_avatar_id")
            .And.Contain("transfer_reserved_at")
            .And.Contain("last_transfer_settlement_key")
            .And.Contain("holon_transfer_reservation")
            .And.NotContain("OVERWRITE");
        confirmation.Should().Contain("DEFINE PARAM OVERWRITE $operation_log_status")
            .And.Contain("DEFINE FIELD OVERWRITE status ON TABLE operation_log")
            .And.Contain("DEFINE PARAM OVERWRITE $swap_state_status")
            .And.Contain("DEFINE FIELD OVERWRITE status ON TABLE swap_state")
            .And.Contain("PendingConfirmation");
    }

    [Fact]
    public void RailwaySchemaJob_UsesDockerAndSurfacesARepeatedFailure()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "deploy",
            "railway",
            "schema.railway.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var build = document.RootElement.GetProperty("build");
        var deploy = document.RootElement.GetProperty("deploy");

        build.GetProperty("builder").GetString().Should().Be("DOCKERFILE");
        build.GetProperty("dockerfilePath").GetString().Should().Be("Dockerfile");
        deploy.GetProperty("startCommand").GetString()
            .Should().Be("/usr/local/bin/docker-entrypoint.sh schema");
        deploy.GetProperty("restartPolicyType").GetString().Should().Be("ON_FAILURE");
        deploy.GetProperty("restartPolicyMaxRetries").GetInt32().Should().Be(1);
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
        apiVariables.GetProperty("SurrealRuntime__AuthenticationScope").GetString()
            .Should().Be("Database");
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
        schemaService.GetProperty("deploy").GetProperty("restartPolicyType").GetString()
            .Should().Be("ON_FAILURE");
        schemaService.GetProperty("deploy").GetProperty("restartPolicyMaxRetries").GetInt32()
            .Should().Be(1);
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
                ["SurrealRuntime:AuthenticationScope"] = apiVariables
                    .GetProperty("SurrealRuntime__AuthenticationScope").GetString(),
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
