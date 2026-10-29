using AZOA.WebAPI.Services.Auth;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace AZOA.WebAPI.Tests.Auth;

/// <summary>The <c>Tenant:ClaimInvites</c> node policy an operator configures; see Services/Auth/AGENTS.md §claim-invites.</summary>
public sealed class ClaimInviteOptionsTests
{
    [Theory]
    [InlineData(null, 72 * 60)]
    [InlineData("3.00:00:00", 72 * 60)]
    [InlineData("00:30:00", 30)]
    [InlineData("7.00:00:00", 7 * 24 * 60)]
    public void Accepted_lifetimes(string? configured, int expectedMinutes)
        => Bind(("Lifetime", configured)).Lifetime.Should().Be(TimeSpan.FromMinutes(expectedMinutes));

    [Theory]
    [InlineData("7.00:00:01", "at most 7 days")]
    [InlineData("72:00:00", "72 DAYS")]
    [InlineData("00:00:00", "positive")]
    [InlineData("-01:00:00", "positive")]
    [InlineData("three days", "not a TimeSpan")]
    public void Refused_lifetimes_name_the_key_and_the_fix(string configured, string explanation)
    {
        var bind = () => Bind(("Lifetime", configured));

        bind.Should().Throw<InvalidOperationException>()
            .WithMessage("*Tenant:ClaimInvites:Lifetime*").WithMessage($"*{explanation}*");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void The_token_is_withheld_unless_explicitly_enabled(string? configured, bool returned)
        => Bind(("ReturnTokenToTenant", configured)).ReturnTokenToTenant.Should().Be(returned);

    [Fact]
    public void A_malformed_token_switch_is_refused_rather_than_read_as_off()
    {
        var bind = () => Bind(("ReturnTokenToTenant", "yes"));

        bind.Should().Throw<InvalidOperationException>().WithMessage("*Tenant:ClaimInvites:ReturnTokenToTenant*");
    }

    /// <summary>Only the integration-test host may hand a member's claim token to the tenant that minted it.</summary>
    [Fact]
    public void No_committed_configuration_except_IntegrationTest_returns_the_token_to_the_tenant()
    {
        var files = Directory.EnumerateFiles(RepositoryRoot(), "appsettings*.json").ToList();
        files.Should().Contain(file => Path.GetFileName(file) == "appsettings.Production.json");

        foreach (var file in files)
        {
            var options = ClaimInviteOptions.FromConfiguration(new ConfigurationBuilder().AddJsonFile(file).Build());
            var integrationTest = Path.GetFileName(file) == "appsettings.IntegrationTest.json";
            options.ReturnTokenToTenant.Should().Be(integrationTest, Path.GetFileName(file));
        }
    }

    private static ClaimInviteOptions Bind((string Key, string? Value) setting)
        => ClaimInviteOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"{ClaimInviteOptions.SectionName}:{setting.Key}"] = setting.Value })
            .Build());

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AZOA.WebAPI.csproj")))
                return directory.FullName;
        throw new InvalidOperationException("Could not locate the AZOA repository root.");
    }
}
