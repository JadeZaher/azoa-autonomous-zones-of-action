using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Admin;
using SurrealForge.Client;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>
/// Verifies ordinary avatar login never receives node-operator authority and
/// legacy partial bootstrap configuration fails during hosted startup.
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
        string _environmentName,
        string? seedEmail = null,
        string? seedSecret = null,
        FakeAdminBootstrapStateStore? bootstrapState = null)
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

        return new AvatarManager(
            store.Object,
            config,
            bootstrapState ?? new FakeAdminBootstrapStateStore());
    }

    private static AZOA.WebAPI.Models.Avatar MakeAvatar(string email) => new()
    {
        Id = Guid.NewGuid(),
        Email = email,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword("password123"),
    };

    private static JwtSecurityToken Decode(string jwt) => new JwtSecurityTokenHandler().ReadJwtToken(jwt);

    [Fact]
    public async Task Login_WithMatchingLegacySeed_NeverStampsOperatorAuthority()
    {
        var avatar = MakeAvatar("admin@azoa.test");
        var store = MakeStore(avatar);
        var manager = MakeManager(store, "Development", "admin@azoa.test", "seed-secret");

        var result = await manager.LoginAsync(new AvatarLoginModel
        {
            Email = avatar.Email,
            Password = "password123",
            BootstrapSecret = "seed-secret",
        });

        result.IsError.Should().BeFalse();
        var token = Decode(result.Result!);
        token.Claims.Should().NotContain(c => c.Type == "scope" && c.Value == AzoaScopes.Operator);
        token.Claims.Should().NotContain(c => c.Type == "scope" && c.Value == AzoaScopes.NodeGovern);
        token.Claims.Should().NotContain(c => c.Type == "role" && c.Value == "Admin");
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
    public async Task Login_WithDeveloperRole_EmitsDappDevelopClaims()
    {
        var avatar = MakeAvatar("dev@azoa.test");
        avatar.DappRole = AzoaDappRoles.Developer;
        var store = MakeStore(avatar);
        var manager = MakeManager(store, "Development");

        var result = await manager.LoginAsync(new AvatarLoginModel { Email = avatar.Email, Password = "password123" });

        result.IsError.Should().BeFalse();
        var token = Decode(result.Result!);
        token.Claims.Should().Contain(c => c.Type == "dapp_role" && c.Value == AzoaDappRoles.Developer);
        token.Claims.Should().Contain(c => c.Type == "scope" && c.Value == AzoaScopes.DappDevelop);
        token.Claims.Should().NotContain(c => c.Type == "scope" && c.Value == AzoaScopes.DappManage);
    }

    [Fact]
    public async Task Login_WithManagerRole_EmitsDevelopAndManageClaims()
    {
        var avatar = MakeAvatar("manager@azoa.test");
        avatar.DappRole = AzoaDappRoles.Manager;
        var store = MakeStore(avatar);
        var manager = MakeManager(store, "Development");

        var result = await manager.LoginAsync(new AvatarLoginModel { Email = avatar.Email, Password = "password123" });

        result.IsError.Should().BeFalse();
        var token = Decode(result.Result!);
        token.Claims.Should().Contain(c => c.Type == "dapp_role" && c.Value == AzoaDappRoles.Manager);
        token.Claims.Should().Contain(c => c.Type == "scope" && c.Value == AzoaScopes.DappDevelop);
        token.Claims.Should().Contain(c => c.Type == "scope" && c.Value == AzoaScopes.DappManage);
        token.Claims.Should().NotContain(c => c.Type == "scope" && c.Value == AzoaScopes.Operator);
        token.Claims.Should().NotContain(c => c.Type == "scope" && c.Value == AzoaScopes.NodeGovern);
    }

    [Fact]
    public async Task Login_WithPlainUserRole_EmitsNoDappAuthority()
    {
        var avatar = MakeAvatar("user@azoa.test");
        var store = MakeStore(avatar);
        var manager = MakeManager(store, "Development");

        var result = await manager.LoginAsync(new AvatarLoginModel { Email = avatar.Email, Password = "password123" });

        result.IsError.Should().BeFalse();
        var token = Decode(result.Result!);
        token.Claims.Should().Contain(c => c.Type == "dapp_role" && c.Value == AzoaDappRoles.User);
        token.Claims.Should().NotContain(c => c.Type == "scope" && c.Value == AzoaScopes.DappDevelop);
        token.Claims.Should().NotContain(c => c.Type == "scope" && c.Value == AzoaScopes.DappManage);
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
    public async Task HostedStartup_WithPartialLegacySeed_InProduction_FailsClosedByThrowing()
    {
        // Only SeedSecret set, no SeedEmail — partial config, Production environment.
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(value => value.EnvironmentName).Returns(Environments.Production);
        var hosted = new SeedAdminHostedService(
            Options.Create(new AdminBootstrapOptions { SeedSecret = "seed-secret" }),
            Options.Create(new NodeOperatorOptions()),
            environment.Object,
            Mock.Of<IServiceScopeFactory>(),
            Mock.Of<ILogger<SeedAdminHostedService>>());

        var act = async () => await hosted.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Login_WithMissingOrWrongBootstrapSecret_DoesNotStamp()
    {
        var avatar = MakeAvatar("admin@azoa.test");
        var store = MakeStore(avatar);
        var manager = MakeManager(store, "Development", avatar.Email, "seed-secret");

        var missing = await manager.LoginAsync(new AvatarLoginModel { Email = avatar.Email, Password = "password123" });
        var wrong = await manager.LoginAsync(new AvatarLoginModel
        {
            Email = avatar.Email,
            Password = "password123",
            BootstrapSecret = "wrong-secret",
        });

        Decode(missing.Result!).Claims.Should().NotContain(c => c.Type == "scope" && c.Value == AzoaScopes.NodeGovern);
        Decode(wrong.Result!).Claims.Should().NotContain(c => c.Type == "scope" && c.Value == AzoaScopes.NodeGovern);
    }

    [Fact]
    public async Task Login_WithLegacyBinding_NeverGrantsEitherAvatarNodeAuthority()
    {
        var seed = MakeAvatar("admin@azoa.test");
        var attacker = MakeAvatar("attacker@azoa.test");
        var state = new FakeAdminBootstrapStateStore();
        var store = MakeStore(seed);
        var manager = MakeManager(store, "Development", seed.Email, "seed-secret", state);

        var first = await manager.LoginAsync(new AvatarLoginModel
        {
            Email = seed.Email,
            Password = "password123",
            BootstrapSecret = "seed-secret",
        });
        store.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<IAvatar>> { Result = new IAvatar[] { attacker } });
        var replay = await manager.LoginAsync(new AvatarLoginModel
        {
            Email = attacker.Email,
            Password = "password123",
            BootstrapSecret = "seed-secret",
        });

        Decode(first.Result!).Claims.Should().NotContain(c => c.Type == "scope" && c.Value == AzoaScopes.NodeGovern);
        Decode(replay.Result!).Claims.Should().NotContain(c => c.Type == "scope" && c.Value == AzoaScopes.NodeGovern);
    }

    [Fact]
    public async Task Update_SelfCannotClaimConfiguredBootstrapEmail()
    {
        var avatar = MakeAvatar("attacker@azoa.test");
        avatar.Id = Guid.NewGuid();
        var store = MakeStore(avatar);
        store.Setup(p => p.GetByIdAsync(avatar.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IAvatar> { Result = avatar });
        var manager = MakeManager(store, "Development", "admin@azoa.test", "seed-secret");

        var result = await manager.UpdateAsync(
            avatar.Id,
            new AvatarUpdateModel { Email = "admin@azoa.test" },
            avatar.Id);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("cannot be claimed");
    }

    private sealed class FakeAdminBootstrapStateStore : IAdminBootstrapStateStore
    {
        private AdminBootstrapState? _state;

        public Task<AZOAResult<AdminBootstrapState?>> GetAsync(CancellationToken ct = default)
            => Task.FromResult(new AZOAResult<AdminBootstrapState?> { Result = _state, Message = "Success" });

        public Task<AZOAResult<AdminBootstrapState>> BindOnceAsync(
            AdminBootstrapState state,
            CancellationToken ct = default)
        {
            _state ??= state;
            return Task.FromResult(new AZOAResult<AdminBootstrapState> { Result = _state, Message = "Success" });
        }

        public Task<AZOAResult<AdminBootstrapState>> RotateCredentialsAsync(
            Guid avatarId,
            string username,
            string passwordHash,
            long expectedRevision,
            long nextRevision,
            DateTimeOffset changedAt,
            CancellationToken ct = default)
        {
            if (_state is null || _state.CredentialRevision != expectedRevision)
                return Task.FromResult(AZOAResult<AdminBootstrapState>.Failure("Revision conflict."));

            _state.CredentialRevision = nextRevision;
            _state.SessionRevision++;
            _state.CredentialUpdatedAt = changedAt;
            return Task.FromResult(AZOAResult<AdminBootstrapState>.Success(_state));
        }
    }
}
