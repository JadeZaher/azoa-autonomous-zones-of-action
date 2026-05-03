using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using OASIS.WebAPI.IntegrationTests.Builders;
using OASIS.WebAPI.IntegrationTests.Factories;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.IntegrationTests.Controllers;

public class STARODKControllerIntegrationTests : IntegrationTestBase
{
    public STARODKControllerIntegrationTests(OASISTestWebApplicationFactory factory) : base(factory) { }

    // ═══════════════════════════════════════════════════════════════
    // CRUD
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CreateOrUpdate_ShouldCreateNewODK()
    {
        var model = new STARODKBuilder().WithName("Genesis").BuildCreateModel();

        var response = await Client.PostAsJsonAsync("api/starodk", model);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<STARODK>(response);
        result!.IsError.Should().BeFalse();
        result.Result!.Name.Should().Be("Genesis");
    }

    [Fact]
    public async Task CreateOrUpdate_ShouldUpdateExistingByName()
    {
        await SeedSTARODKAsync(o => o.WithName("Existing").WithDescription("Old"));
        var model = new STARODKBuilder().WithName("Existing").WithDescription("New").BuildCreateModel();

        var response = await Client.PostAsJsonAsync("api/starodk", model);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<STARODK>(response);
        result!.Result!.Description.Should().Be("New");
    }

    [Fact]
    public async Task Get_Existing_ShouldReturnODK()
    {
        var odk = await SeedSTARODKAsync();

        var response = await Client.GetAsync($"api/starodk/{odk.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<STARODK>(response);
        result!.Result!.Id.Should().Be(odk.Id);
    }

    [Fact]
    public async Task Get_NonExisting_ShouldReturnNotFound()
    {
        var response = await Client.GetAsync($"api/starodk/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAll_ShouldReturnList()
    {
        await SeedSTARODKAsync(o => o.WithName("A"));
        await SeedSTARODKAsync(o => o.WithName("B"));

        var response = await Client.GetAsync("api/starodk");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<IEnumerable<STARODK>>(response);
        result!.Result.Should().HaveCount(2);
    }

    [Fact]
    public async Task Delete_ShouldRemoveODK()
    {
        var odk = await SeedSTARODKAsync();

        var response = await Client.DeleteAsync($"api/starodk/{odk.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Delete_NonExisting_ShouldReturnNotFound()
    {
        var response = await Client.DeleteAsync($"api/starodk/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ═══════════════════════════════════════════════════════════════
    // GENERATE
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Generate_ShouldSetGeneratedCode()
    {
        var odk = await SeedSTARODKAsync();
        var request = new STARODKBuilder().Targeting("Solana").BuildGenerationRequest();

        var response = await Client.PostAsJsonAsync($"api/starodk/{odk.Id}/generate", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<STARODK>(response);
        result!.Result!.GeneratedCode.Should().NotBeNullOrEmpty();
        result.Result.TargetChain.Should().Be("Solana");
    }

    [Fact]
    public async Task Generate_NonExisting_ShouldReturnBadRequest()
    {
        var request = new STARODKBuilder().BuildGenerationRequest();

        var response = await Client.PostAsJsonAsync($"api/starodk/{Guid.NewGuid()}/generate", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ═══════════════════════════════════════════════════════════════
    // DEPLOY
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Deploy_ShouldSetDeploymentConfig()
    {
        var odk = await SeedSTARODKAsync(o => o.WithGeneratedCode("some code").Targeting("Algorand"));

        var response = await Client.PostAsync($"api/starodk/{odk.Id}/deploy", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<STARODK>(response);
        result!.Result!.DeploymentConfig.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Deploy_WithoutGeneration_ShouldReturnBadRequest()
    {
        var odk = await SeedSTARODKAsync();

        var response = await Client.PostAsync($"api/starodk/{odk.Id}/deploy", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ═══════════════════════════════════════════════════════════════
    // AUTH
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Get_WithoutAuth_ShouldReturnUnauthorized()
    {
        var unauthClient = Factory.CreateClient();
        var response = await unauthClient.GetAsync($"api/starodk/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
