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
