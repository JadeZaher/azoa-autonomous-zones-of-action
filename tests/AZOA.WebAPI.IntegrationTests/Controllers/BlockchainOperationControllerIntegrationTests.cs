using System.Net;
using FluentAssertions;
using AZOA.WebAPI.IntegrationTests.Builders;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.IntegrationTests.Controllers;

public class BlockchainOperationControllerIntegrationTests : IntegrationTestBase
{
    public BlockchainOperationControllerIntegrationTests(AZOATestWebApplicationFactory factory) : base(factory) { }

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
    public async Task GetByUser_ShouldReturnOperationsForUser()
    {
        var userId = Guid.Parse(TestAuthHandler.DefaultUserId);
        await SeedBlockchainOperationAsync(o => o.ForUser(userId).OfType("Mint"));
        await SeedBlockchainOperationAsync(o => o.ForUser(userId).OfType("Burn"));

        var response = await Client.GetAsync($"api/blockchainoperation/user/{userId}");

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
