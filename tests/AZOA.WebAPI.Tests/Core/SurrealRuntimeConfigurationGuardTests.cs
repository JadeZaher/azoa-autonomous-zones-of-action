using FluentAssertions;
using Microsoft.Extensions.Configuration;
using AZOA.WebAPI.Core.Surreal;

namespace AZOA.WebAPI.Tests.Core;

public sealed class SurrealRuntimeConfigurationGuardTests
{
    [Fact]
    public void GuardProduction_ValidDatabaseRuntimeAndExternalSchemaJob_AllowsStartup()
    {
        var configuration = ProductionRuntimeConfiguration();

        var act = () => SurrealRuntimeConfigurationGuard.GuardProduction(configuration, isProduction: true);

        act.Should().NotThrow();
    }

    [Fact]
    public void GuardProduction_RootRuntimeUser_RejectsStartup()
    {
        var configuration = ProductionRuntimeConfiguration(("SurrealRuntime:User", "ROOT"));

        var act = () => SurrealRuntimeConfigurationGuard.GuardProduction(configuration, isProduction: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*never root*");
    }

    [Fact]
    public void GuardProduction_MissingRuntimeCredentials_RejectsStartup()
    {
        var configuration = ProductionRuntimeConfiguration(("SurrealRuntime:Password", ""));

        var act = () => SurrealRuntimeConfigurationGuard.GuardProduction(configuration, isProduction: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SurrealRuntime:Password*");
    }

    [Fact]
    public void GuardProduction_LegacyDatabaseCredentials_RejectsStartup()
    {
        var configuration = ProductionRuntimeConfiguration(("SurrealDb:User", "root"));

        var act = () => SurrealRuntimeConfigurationGuard.GuardProduction(configuration, isProduction: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*legacy SurrealDb credentials*");
    }

    [Fact]
    public void GuardProduction_MigrationsEnabled_RejectsStartup()
    {
        var configuration = ProductionRuntimeConfiguration(("AZOA_SKIP_MIGRATIONS", "0"));

        var act = () => SurrealRuntimeConfigurationGuard.GuardProduction(configuration, isProduction: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*separate schema job*");
    }

    [Fact]
    public void ResolveRuntimeSectionName_ProductionWithoutRuntime_RejectsLegacyFallback()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SurrealDb:User"] = "root",
            })
            .Build();

        var act = () => SurrealRuntimeConfigurationGuard.ResolveRuntimeSectionName(configuration, isProduction: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SurrealRuntime configuration is required*");
    }

    [Fact]
    public void ResolveRuntimeSectionName_NonProductionLegacyConfiguration_RemainsCompatible()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SurrealDb:Endpoint"] = "http://localhost:8000",
            })
            .Build();

        SurrealRuntimeConfigurationGuard.ResolveRuntimeSectionName(configuration, isProduction: false)
            .Should().Be("SurrealDb");
    }

    private static IConfiguration ProductionRuntimeConfiguration(params (string Key, string Value)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["SurrealRuntime:Endpoint"] = "http://surrealdb.internal:8000",
            ["SurrealRuntime:Namespace"] = "azoa",
            ["SurrealRuntime:Database"] = "azoa",
            ["SurrealRuntime:User"] = "azoa_runtime",
            ["SurrealRuntime:Password"] = "not-a-real-secret",
            ["AZOA_SKIP_MIGRATIONS"] = "1",
        };
        foreach (var (key, value) in overrides)
            values[key] = value;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
