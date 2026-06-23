using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using AZOA.WebAPI.IntegrationTests.Builders;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.IntegrationTests.Controllers;

public class STARODKControllerIntegrationTests : IntegrationTestBase
{
    public STARODKControllerIntegrationTests(AZOATestWebApplicationFactory factory) : base(factory) { }

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

    // ═══════════════════════════════════════════════════════════════
    // UPDATE (PUT)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_ValidId_ReturnsOk()
    {
        var odk = await SeedSTARODKAsync(o => o.WithName("BeforeUpdate"));
        var model = new STARODKBuilder().WithName("AfterUpdate").BuildCreateModel();

        var response = await Client.PutAsJsonAsync($"api/starodk/{odk.Id}", model);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<STARODK>(response);
        result!.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task Update_InvalidId_ReturnsNotFound()
    {
        // {id:guid} route constraint — non-guid routes do not match; ASP.NET returns 404.
        var model = new STARODKBuilder().WithName("X").BuildCreateModel();

        var response = await Client.PutAsJsonAsync("api/starodk/not-a-guid", model);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_Unauthorized_Returns401()
    {
        var unauthClient = Factory.CreateClient();
        var model = new STARODKBuilder().WithName("X").BuildCreateModel();

        var response = await unauthClient.PutAsJsonAsync($"api/starodk/{Guid.NewGuid()}", model);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ═══════════════════════════════════════════════════════════════
    // IDOR closure (security: cross-avatar overwrite prevention)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_DifferentAvatar_Returns403()
    {
        // Avatar A creates a record via the default authenticated client.
        var avatarA_record = await SeedSTARODKAsync(o => o.WithName("A's secret"));

        // Avatar B authenticates and attempts to PUT to A's id.
        var avatarB = Guid.Parse("b2222222-2222-2222-2222-222222222222");
        using var clientB = Factory.CreateAuthenticatedClientForAvatar(avatarB);
        var hijack = new STARODKBuilder().WithName("Owned by B now").WithDescription("hijack").BuildCreateModel();

        var response = await clientB.PutAsJsonAsync($"api/starodk/{avatarA_record.Id}", hijack);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "PUT IDOR must be closed -- a different avatar cannot overwrite A's record by route id");

        // And the underlying record is untouched.
        var verify = await Client.GetAsync($"api/starodk/{avatarA_record.Id}");
        verify.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<STARODK>(verify);
        result!.Result!.Name.Should().Be("A's secret");
        result.Result.Description.Should().NotBe("hijack");
    }

    [Fact]
    public async Task Update_OwnRecord_Returns200()
    {
        // Same authenticated avatar can update its own record by route id.
        var odk = await SeedSTARODKAsync(o => o.WithName("Mine").WithDescription("v1"));
        var update = new STARODKBuilder().WithName("Mine").WithDescription("v2").BuildCreateModel();

        var response = await Client.PutAsJsonAsync($"api/starodk/{odk.Id}", update);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<STARODK>(response);
        result!.IsError.Should().BeFalse();
        result.Result!.Description.Should().Be("v2");
    }

    [Fact]
    public async Task CreateOrUpdate_DifferentAvatar_SameName_DoesNotOverwrite()
    {
        // POST IDOR closure: Avatar A and Avatar B both POST "Foo" -> two distinct records.
        var modelA = new STARODKBuilder().WithName("Foo").WithDescription("A's Foo").BuildCreateModel();
        var responseA = await Client.PostAsJsonAsync("api/starodk", modelA);
        responseA.StatusCode.Should().Be(HttpStatusCode.OK);
        var resultA = await ReadResultAsync<STARODK>(responseA);

        var avatarB = Guid.Parse("b3333333-3333-3333-3333-333333333333");
        using var clientB = Factory.CreateAuthenticatedClientForAvatar(avatarB);
        var modelB = new STARODKBuilder().WithName("Foo").WithDescription("B's Foo").BuildCreateModel();
        var responseB = await clientB.PostAsJsonAsync("api/starodk", modelB);
        responseB.StatusCode.Should().Be(HttpStatusCode.OK);
        var resultB = await ReadResultAsync<STARODK>(responseB);

        // Distinct records -- no overwrite.
        resultA!.Result!.Id.Should().NotBe(resultB!.Result!.Id);
        resultA.Result.Description.Should().Be("A's Foo");
        resultB.Result.Description.Should().Be("B's Foo");

        // A's view of /api/starodk now lists at least its own record with original data preserved.
        var listResponse = await Client.GetAsync("api/starodk");
        var list = await ReadResultAsync<IEnumerable<STARODK>>(listResponse);
        list!.Result.Should().Contain(s => s.Id == resultA.Result.Id && s.Description == "A's Foo");
        list.Result.Should().Contain(s => s.Id == resultB.Result.Id && s.Description == "B's Foo");
    }
}
