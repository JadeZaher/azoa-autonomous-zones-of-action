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

namespace AZOA.WebAPI.Tests.Managers;

public class AvatarManagerExtendedTests
{
    private readonly Mock<IAvatarStore> _store;
    private readonly AvatarManager _manager;

    public AvatarManagerExtendedTests()
    {
        _store = new Mock<IAvatarStore>();

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
        _manager = new AvatarManager(_store.Object, config, environment.Object);
    }

    [Fact]
    public async Task GetAllAsync_ShouldReturnAllAvatars()
    {
        _store.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IAvatar>>
                 {
                     Result = new[] { new Avatar { Username = "a1" }, new Avatar { Username = "a2" } }
                 });

        var result = await _manager.GetAllAsync();

        result.Result.Should().HaveCount(2);
    }

    [Fact]
    public async Task UpdateAsync_WithMissingAvatar_ShouldReturnError()
    {
        var id = Guid.NewGuid();
        _store.Setup(p => p.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IAvatar> { IsError = true, Message = "Not found" });

        var result = await _manager.UpdateAsync(id, new AvatarUpdateModel { FirstName = "X" }, id);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateAsync_WithPartialFields_ShouldOnlyUpdateProvided()
    {
        var avatar = new Avatar
        {
            Id = Guid.NewGuid(),
            Username = "keep",
            Email = "keep@test.com",
            FirstName = "Old",
            LastName = "Name",
            IsActive = true
        };
        _store.Setup(p => p.GetByIdAsync(avatar.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IAvatar> { Result = avatar });
        _store.Setup(p => p.UpsertAsync(It.IsAny<IAvatar>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IAvatar a, CancellationToken _) => new AZOAResult<IAvatar> { Result = a });

        var result = await _manager.UpdateAsync(avatar.Id, new AvatarUpdateModel { FirstName = "New" }, avatar.Id);

        result.IsError.Should().BeFalse();
        result.Result!.FirstName.Should().Be("New");
        result.Result.Username.Should().Be("keep");
        result.Result.LastName.Should().Be("Name");
        result.Result.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_ShouldReturnProviderResult()
    {
        var id = Guid.NewGuid();
        _store.Setup(p => p.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<bool> { Result = true });

        var result = await _manager.DeleteAsync(id, id);

        result.Result.Should().BeTrue();
    }

    // Deleted in Mission B: RegisterAsync_WhenProviderActivationFails_ShouldReturnError.
    // Premise is architecturally obsolete — provider-selection/activation guards
    // were removed (W2); managers now inject a concrete store via DI, so the
    // "No storage provider available" code path no longer exists.
}
