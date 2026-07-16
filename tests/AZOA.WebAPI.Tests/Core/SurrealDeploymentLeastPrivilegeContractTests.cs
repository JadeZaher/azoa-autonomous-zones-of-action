using FluentAssertions;

namespace AZOA.WebAPI.Tests.Core;

public sealed class SurrealDeploymentLeastPrivilegeContractTests
{
    [Fact]
    public void ProductionEntrypoint_RequiresExternalMigrationsAndNeverForcesChecksums()
    {
        var entrypoint = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "docker-entrypoint.sh"));

        entrypoint.Should().Contain("Production refuses API-boot migrations")
            .And.NotContain("--force");
    }

    [Fact]
    public void RailwayTemplate_DoesNotInjectRootOrSchemaCredentialsIntoApi()
    {
        var template = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "deploy", "railway", "template.json"));

        template.Should().Contain("\"AZOA_SKIP_MIGRATIONS\": \"1\"")
            .And.NotContain("SurrealDb__User")
            .And.NotContain("SurrealDb__Password")
            .And.NotContain("SURREALFORGE_USER")
            .And.NotContain("SURREALFORGE_PASS");
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
}
