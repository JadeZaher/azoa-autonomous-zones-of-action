using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using FluentAssertions;
using Moq;

namespace AZOA.WebAPI.Tests.Managers;

public sealed class ConsentManagerTests
{
    [Fact]
    public async Task Foreign_grant_is_indistinguishable_from_missing()
    {
        var grants = new Mock<IConsentGrantStore>();
        var id = Guid.NewGuid(); var owner = Guid.NewGuid();
        grants.Setup(item => item.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<ConsentGrant>.Success(new ConsentGrant { Id = id, GrantorUserId = owner }));
        var manager = new ConsentManager(grants.Object);
        var foreign = await manager.GetForUserAsync(Guid.NewGuid(), id);
        foreign.IsError.Should().BeTrue(); foreign.Message.Should().StartWith(TenantAuthorizationError.NotFound);
        (await manager.GetForUserAsync(owner, id)).Result!.Id.Should().Be(id);
    }

    [Fact]
    public async Task Scoped_lists_preserve_store_failures_and_do_not_mutate_grants()
    {
        var grants = new Mock<IConsentGrantStore>(MockBehavior.Strict);
        var actor = Guid.NewGuid(); var tenant = Guid.NewGuid();
        grants.Setup(item => item.ListByGrantorAsync(actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IEnumerable<ConsentGrant>>.Success([]));
        grants.Setup(item => item.ListByTenantAsync(tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IEnumerable<ConsentGrant>>.Failure("store unavailable"));
        var manager = new ConsentManager(grants.Object);
        (await manager.ListForUserAsync(actor)).Result.Should().BeEmpty();
        var failed = await manager.ListForTenantAsync(tenant);
        failed.IsError.Should().BeTrue(); failed.Message.Should().Be("store unavailable");
    }
}
