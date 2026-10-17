using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests;

public class UserManagerTests
{
    private readonly Mock<IUserStore> _store;
    private readonly UserManager _manager;

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public async Task Stable_identity_login_requires_active_self_owned_password_credentials(bool active, bool tenantOwned, bool rejected)
    {
        var user = new User { Id = Guid.NewGuid(), IsActive = active,
            OwnerTenantId = tenantOwned ? Guid.NewGuid() : null, PasswordHash = BCrypt.Net.BCrypt.HashPassword("claimant-password") };
        _store.Setup(store => store.GetByIdAsync(user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IUser>.Success(user));
        var result = await _manager.LoginAsync(new UserLoginModel { UserId = user.Id, Password = "claimant-password" });
        result.IsError.Should().Be(rejected);
        (await _manager.LoginAsync(new UserLoginModel { UserId = user.Id, Password = "tenant-password" })).IsError.Should().BeTrue();
        (await _manager.LoginAsync(new UserLoginModel { UserId = user.Id, Email = "other@example.test", Password = "claimant-password" })).IsError.Should().BeTrue();
        _store.Verify(store => store.GetAllAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    public UserManagerTests()
    {
        _store = new Mock<IUserStore>();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZOA:DefaultProvider"] = "InMemory",
                ["Jwt:Key"] = "super-secret-key-for-testing-only!",
                ["Jwt:Issuer"] = "test",
                ["Jwt:Audience"] = "test"
            })
            .Build();

        var environment = new Mock<IHostEnvironment>();
        environment.Setup(e => e.EnvironmentName).Returns("Development");
        _manager = new UserManager(_store.Object, config);
    }

    [Fact]
    public async Task RegisterAsync_ShouldHashPasswordAndSave()
    {
        // RegisterAsync now checks for duplicate email/username first, so the
        // user listing must be mocked or the loop NREs on a null result.
        _store.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IUser>> { Result = Array.Empty<IUser>() });

        _store.Setup(p => p.UpsertAsync(It.IsAny<IUser>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IUser a, CancellationToken _) => new AZOAResult<IUser> { Result = a });

        var model = new UserRegisterModel
        {
            Username = "testuser",
            Email = "test@test.com",
            Password = "password123"
        };

        var result = await _manager.RegisterAsync(model);

        result.IsError.Should().BeFalse();
        result.Result.Should().NotBeNull();
        result.Result!.Username.Should().Be("testuser");
        result.Result.PasswordHash.Should().NotBe("password123");
    }

    [Fact]
    public async Task LoginAsync_WithValidCredentials_ShouldReturnToken()
    {
        var user = new User
        {
            Email = "test@test.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("password123")
        };

        _store.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IUser>> { Result = new[] { user } });

        var model = new UserLoginModel { Email = "test@test.com", Password = "password123" };
        var result = await _manager.LoginAsync(model);

        result.IsError.Should().BeFalse();
        result.Result.Should().NotBeNullOrEmpty();
        var token = new JwtSecurityTokenHandler().ReadJwtToken(result.Result);
        token.Claims.Should().Contain(claim =>
            claim.Type == AZOA.WebAPI.Core.AzoaClaims.TokenUse
            && claim.Value == AZOA.WebAPI.Core.AzoaClaims.TokenUseLogin);
        token.Claims.Should().Contain(claim =>
            claim.Type == AZOA.WebAPI.Core.AzoaClaims.AuthTime);
    }

    [Fact]
    public async Task LoginAsync_WithInvalidCredentials_ShouldReturnError()
    {
        _store.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IUser>> { Result = Array.Empty<IUser>() });

        var model = new UserLoginModel { Email = "test@test.com", Password = "wrong" };
        var result = await _manager.LoginAsync(model);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Invalid credentials");
    }

    [Fact]
    public async Task GetAsync_ShouldReturnUser()
    {
        var user = new User { Id = Guid.NewGuid(), Username = "test" };
        _store.Setup(p => p.GetByIdAsync(user.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IUser> { Result = user });

        var result = await _manager.GetAsync(user.Id);

        result.Result.Should().NotBeNull();
        result.Result!.Username.Should().Be("test");
    }

}

/// <summary>
/// OI-11: every writer of an account identity must fold case, because
/// <c>user_email</c> / <c>user_username</c> are case-SENSITIVE UNIQUE indexes
/// (<c>Persistence/SurrealDb/Generated/Schemas/user.surql</c>).
/// </summary>
/// <remarks>
/// <c>UserManager.RegisterAsync</c> and <c>UserManager.UpdateAsync</c> have always folded.
/// <c>TenantManager.ProvisionChildAsync</c> did not, so a tenant that provisioned
/// <c>Alice@Example.com</c> left a row that a later <c>alice@example.com</c> slid past the
/// index - two rows, two password hashes, one address, with
/// <c>UserManager.ResolveLoginSubject</c> left to refuse the ambiguity rather than a person
/// keeping their account. These tests pin the WRITE side of that rule.
/// </remarks>
public class TenantIdentityFoldingTests
{
    /// <summary>What the UNIQUE index says when a stored value is already taken.</summary>
    private const string UniqueIndexRefusal = "Database index exists: user_email";

    /// <summary>
    /// A user store whose create adjudicates the way the real UNIQUE indexes do: it compares
    /// the STORED BYTES (ordinal). Nothing in the store folds case - folding is the writer's
    /// job, which is exactly what OI-11 is about, so a fake that folded here would test
    /// nothing.
    /// </summary>
    private static (Mock<IUserStore> store, List<IUser> rows) StoreHolding(params IUser[] seed)
    {
        var rows = new List<IUser>(seed);
        var store = new Mock<IUserStore>();

        store.Setup(s => s.GetByTenantAndExternalUserAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IUser>());
        store.Setup(s => s.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IUser> { IsError = true, Message = "User not found." });
        store.Setup(s => s.CreateIfAbsentAsync(It.IsAny<IUser>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IUser candidate, CancellationToken _) =>
            {
                var taken = rows.Any(row =>
                    string.Equals(row.Email, candidate.Email, StringComparison.Ordinal)
                    || string.Equals(row.Username, candidate.Username, StringComparison.Ordinal));
                if (taken)
                    return new AZOAResult<IUser> { IsError = true, Message = UniqueIndexRefusal };

                rows.Add(candidate);
                return new AZOAResult<IUser> { Result = candidate };
            });

        return (store, rows);
    }

    private static TenantManager NewTenantManager(Mock<IUserStore> store)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "super-secret-key-for-testing-only-quite-long-enough!",
                ["Jwt:Issuer"] = "test",
                ["Jwt:Audience"] = "test"
            })
            .Build();

        var grants = new Mock<IConsentGrantStore>();
        grants.Setup(g => g.ListByGrantorAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<ConsentGrant>> { Result = Array.Empty<ConsentGrant>() });

        return new TenantManager(store.Object, config, grants.Object, ConsentFixture.Issuer());
    }

    [Fact]
    public async Task ProvisionChild_FoldsTheUsername_AndNeverStoresTheTenantSuppliedEmail()
    {
        var (store, rows) = StoreHolding();

        var result = await NewTenantManager(store).ProvisionFixtureAsync(
            Guid.NewGuid(),
            new ProvisionChildModel
            {
                ExternalUserId = "alice-1",
                Username = "Alice",
                Email = "  Alice@Example.COM  "
            });

        result.IsError.Should().BeFalse(result.Message);
        rows.Should().ContainSingle();
        rows[0].Email.Should().EndWith("@onboard.azoa.local");
        rows[0].Username.Should().Be("alice");
    }

    [Fact]
    public async Task ProvisionChild_CannotSquatAVictimsAddress()
    {
        var (store, rows) = StoreHolding();

        (await NewTenantManager(store).ProvisionFixtureAsync(
            Guid.NewGuid(),
            new ProvisionChildModel { ExternalUserId = "victim-1", Username = "victim-tenant", Email = "victim@example.com" }))
            .IsError.Should().BeFalse();

        rows.Should().NotContain(row => row.Email == "victim@example.com",
            "the victim can still register their own address afterwards (audit-1 LOW)");
    }

    [Fact]
    public async Task ProvisionChild_FoldedWrite_LeavesLoginUnambiguous()
    {
        var (store, rows) = StoreHolding(new User
        {
            Id = Guid.NewGuid(),
            Username = "alice",
            Email = "alice@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("password123")
        });

        await NewTenantManager(store).ProvisionFixtureAsync(
            Guid.NewGuid(),
            new ProvisionChildModel { ExternalUserId = "alice-1", Email = "ALICE@example.com" });

        // One row survives, so LoginAsync has exactly one password hash to verify against
        // instead of two rows folding together and resolving to nothing.
        store.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<IUser>> { Result = rows.ToArray() });

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "super-secret-key-for-testing-only!",
                ["Jwt:Issuer"] = "test",
                ["Jwt:Audience"] = "test"
            })
            .Build();

        var login = await new UserManager(store.Object, config).LoginAsync(
            new UserLoginModel { Email = "alice@example.com", Password = "password123" });

        login.IsError.Should().BeFalse(login.Message);
        login.Result.Should().NotBeNullOrEmpty();
    }
}
