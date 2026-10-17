using AZOA.WebAPI.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;

namespace AZOA.WebAPI.Tests.Kyb;

public sealed class DevelopmentProviderSecretsTests
{
    [Fact]
    public void AliasParserNeverImportsLiveKeysOrActivationFlags()
    {
        var aliases = DevelopmentProviderSecrets.ReadAliases([
            "IDENTOMAT_API_KEY='company-fixture'", "STRIPE_SECRET_API_KEY_DEV=sk_test_fixture",
            "STRIPE_SECRET_API_KEY_LIVE=sk_live_FORBIDDEN", "Fiat__Enabled=true", "Fiat__Environment=Live",
            "Kyc__Identomat__ConfigId=kyc-config", "Kyb__ConfigId=kyb-config", "Fiat__Test__WebhookSecret=whsec_fixture",
        ]);
        aliases.Should().HaveCount(5);
        aliases.Values.Should().NotContain("sk_live_FORBIDDEN");
        aliases["IDENTOMAT_API_KEY"].Should().Be("company-fixture");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("IntegrationTest")]
    public void DotEnvIsNeverLoadedOutsideDevelopment(string environmentName)
    {
        WithFixture(environmentName, (configuration, environment) =>
        {
            DevelopmentProviderSecrets.Apply(configuration, environment);
            configuration["Kyc:Identomat:CompanyKey"].Should().BeNull();
            configuration["Fiat:Test:SecretKey"].Should().BeNull();
        });
    }

    [Fact]
    public void DevelopmentMappingsPreserveExplicitConfigurationAndRejectLiveKeyInDevAlias()
    {
        WithFixture("Development", (configuration, environment) =>
        {
            configuration["Kyc:Identomat:CompanyKey"] = "explicit-company";
            configuration["Fiat:Test:WebhookSecret"] = "explicit-webhook";
            DevelopmentProviderSecrets.Apply(configuration, environment);
            configuration["Kyc:Identomat:CompanyKey"].Should().Be("explicit-company");
            configuration["Fiat:Test:WebhookSecret"].Should().Be("explicit-webhook");
            configuration["Kyb:ConfigId"].Should().Be("business-config");
            configuration["Kyc:Identomat:CallbackApiKey"].Should().Be("callback-fixture");
            configuration["Fiat:Test:SecretKey"].Should().BeNull();
            configuration["Fiat:Live:SecretKey"].Should().BeNull();
        });
    }

    private static void WithFixture(string environmentName, Action<ConfigurationManager, IHostEnvironment> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "azoa-provider-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ".env");
        File.WriteAllLines(path, ["IDENTOMAT_API_KEY=company-fixture", "STRIPE_SECRET_API_KEY_DEV=sk_live_wrong-mode",
            "STRIPE_SECRET_API_KEY_LIVE=sk_live_never-import", "Kyb__ConfigId=business-config",
            "Kyc__Identomat__CallbackApiKey=callback-fixture", "Fiat__Test__WebhookSecret=whsec_fixture"]);
        try
        {
            var environment = new Mock<IHostEnvironment>();
            environment.SetupGet(value => value.EnvironmentName).Returns(environmentName);
            environment.SetupGet(value => value.ContentRootPath).Returns(directory);
            using var configuration = new ConfigurationManager();
            test(configuration, environment.Object);
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }
}
