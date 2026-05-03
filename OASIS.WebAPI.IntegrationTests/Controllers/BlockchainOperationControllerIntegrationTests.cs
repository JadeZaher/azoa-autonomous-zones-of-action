using System.Net;
using FluentAssertions;
using OASIS.WebAPI.IntegrationTests.Builders;
using OASIS.WebAPI.IntegrationTests.Factories;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.IntegrationTests.Controllers;

public class BlockchainOperationControllerIntegrationTests : IntegrationTestBase
{
    public BlockchainOperationControllerIntegrationTests(OASISTestWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task Get_ExistingOperation_ShouldReturnOperation()
    {
        var op = await SeedBlockchainOperationAsync();

        var response = await Client.GetAsync($"api/blockchainoperation/{op.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<BlockchainOperation>(response);
        result!.Result!.Id.Should().Be(op.Id);
    }

    [Fact]
    public async Task Get_NonExisting_ShouldReturnNotFound()
    {
        var response = await Client.GetAsync($"api/blockchainoperation/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetByAvatar_ShouldReturnOperationsForAvatar()
    {
        var avatarId = Guid.Parse(TestAuthHandler.DefaultAvatarId);
        await SeedBlockchainOperationAsync(o => o.ForAvatar(avatarId).OfType("Mint"));
        await SeedBlockchainOperationAsync(o => o.ForAvatar(avatarId).OfType("Burn"));

        var response = await Client.GetAsync($"api/blockchainoperation/avatar/{avatarId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<IEnumerable<BlockchainOperation>>(response);
        result!.Result.Should().HaveCount(2);
    }

    [Fact]
    public async Task Get_WithoutAuth_ShouldReturnUnauthorized()
    {
        var unauthClient = Factory.CreateClient();
        var response = await unauthClient.GetAsync($"api/blockchainoperation/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
