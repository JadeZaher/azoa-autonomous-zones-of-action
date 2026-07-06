using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>
/// final-hardening-cutover H2: AvatarManager.GenerateJwt's operator:admin
/// bootstrap seam (Services/Admin/AGENTS.md). Covers armed/off/partial and the
/// Production fail-closed throw.
/// </summary>
public class AdminBootstrapTests
{
    private static Mock<IAvatarStore> MakeStore(IAvatar avatar)
    {
        var store = new Mock<IAvatarStore>();
        store.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<IAvatar>> { Result = new[] { avatar } });
        return store;
    }

    private static AvatarManager MakeManager(
        Mock<IAvatarStore> store,
        string environmentName,
        string? seedEmail = null,
        string? seedSecret = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "super-secret-key-for-testing-only!",
            ["Jwt:Issuer"] = "test",
            ["Jwt:Audience"] = "test",
        };
        if (seedEmail != null) settings["AdminBootstrap:SeedEmail"] = seedEmail;
        if (seedSecret != null) settings["AdminBootstrap:SeedSecret"] = seedSecret;

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        // IHostEnvironment.IsProduction() is an extension method (not mockable
        // directly) that compares EnvironmentName against Environments.Production
        // — so mocking EnvironmentName alone drives it correctly.
        var environment = new Mock<IHostEnvironment>();
        environment.Setup(e => e.EnvironmentName).Returns(environmentName);

        return new AvatarManager(store.Object, config, environment.Object);
    }

    private static Avatar MakeAvatar(string email) => new()
    {
        Email = email,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword("password123"),
    };

    private static JwtSecurityToken Decode(string jwt) => new JwtSecurityTokenHandler().ReadJwtToken(jwt);

    [Fact]
    public async Task Login_WithBothSeedValuesSetAndMatchingEmail_StampsOperatorAdmin()
    {
        var avatar = MakeAvatar("admin@azoa.test");
        var store = MakeStore(avatar);
        var manager = MakeManager(store, "Development", "admin@azoa.test", "seed-secret");

        var result = await manager.LoginAsync(new AvatarLoginModel { Email = avatar.Email, Password = "password123" });

        result.IsError.Should().BeFalse();
        var token = Decode(result.Result!);
        token.Claims.Should().Contain(c => c.Type == "scope" && c.Value == AzoaScopes.Operator);
        token.Claims.Should().Contain(c => c.Type == "role" && c.Value == "Admin");
    }

    [Fact]
    public async Task Login_WithSeedConfigured_ButDifferentEmail_DoesNotStamp()
    {
        var avatar = MakeAvatar("regular-user@azoa.test");
        var store = MakeStore(avatar);
        var manager = MakeManager(store, "Development", "admin@azoa.test", "seed-secret");

        var result = await manager.LoginAsync(new AvatarLoginModel { Email = avatar.Email, Password = "password123" });

        result.IsError.Should().BeFalse();
        var token = Decode(result.Result!);
        token.Claims.Should().NotContain(c => c.Type == "scope" && c.Value == AzoaScopes.Operator);
    }

    [Fact]
    public async Task Login_WithBootstrapOff_NeverStamps()
    {
        var avatar = MakeAvatar("admin@azoa.test");
        var store = MakeStore(avatar);
        var manager = MakeManager(store, "Development"); // no seed email/secret at all

        var result = await manager.LoginAsync(new AvatarLoginModel { Email = avatar.Email, Password = "password123" });

        result.IsError.Should().BeFalse();
        var token = Decode(result.Result!);
        token.Claims.Should().NotContain(c => c.Type == "scope" && c.Value == AzoaScopes.Operator);
    }

    [Fact]
    public async Task Login_WithPartialSeedConfig_InDevelopment_SkipsSafely()
    {
        var avatar = MakeAvatar("admin@azoa.test");
        var store = MakeStore(avatar);
        // Only SeedEmail set, no SeedSecret — partial config.
        var manager = MakeManager(store, "Development", seedEmail: "admin@azoa.test");

        var result = await manager.LoginAsync(new AvatarLoginModel { Email = avatar.Email, Password = "password123" });

        result.IsError.Should().BeFalse();
        var token = Decode(result.Result!);
        token.Claims.Should().NotContain(c => c.Type == "scope" && c.Value == AzoaScopes.Operator);
    }

    [Fact]
    public async Task Login_WithPartialSeedConfig_InProduction_FailsClosedByThrowing()
    {
        var avatar = MakeAvatar("admin@azoa.test");
        var store = MakeStore(avatar);
        // Only SeedSecret set, no SeedEmail — partial config, Production environment.
        var manager = MakeManager(store, "Production", seedSecret: "seed-secret");

        var act = async () => await manager.LoginAsync(new AvatarLoginModel { Email = avatar.Email, Password = "password123" });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
