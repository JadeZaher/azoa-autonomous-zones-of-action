using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using AZOA.WebAPI.IntegrationTests.Builders;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.IntegrationTests.Controllers;

public class ActionGraphBundleControllerIntegrationTests : IntegrationTestBase
{
    public ActionGraphBundleControllerIntegrationTests(AZOATestWebApplicationFactory factory) : base(factory) { }

    // ═══════════════════════════════════════════════════════════════
    // CRUD
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CreateOrUpdate_ShouldCreateNewBundle()
    {
        var model = new ActionGraphBundleBuilder().WithName("Genesis").BuildCreateModel();

        var response = await Client.PostAsJsonAsync("api/actiongraphbundle", model);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<ActionGraphBundle>(response);
        result!.IsError.Should().BeFalse();
        result.Result!.Name.Should().Be("Genesis");
    }

    [Fact]
    public async Task CreateOrUpdate_ShouldUpdateExistingByName()
    {
        await SeedActionGraphBundleAsync(o => o.WithName("Existing").WithDescription("Old"));
        var model = new ActionGraphBundleBuilder().WithName("Existing").WithDescription("New").BuildCreateModel();

        var response = await Client.PostAsJsonAsync("api/actiongraphbundle", model);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<ActionGraphBundle>(response);
        result!.Result!.Description.Should().Be("New");
    }

    [Fact]
    public async Task Get_Existing_ShouldReturnBundle()
    {
        var bundle = await SeedActionGraphBundleAsync();

        var response = await Client.GetAsync($"api/actiongraphbundle/{bundle.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<ActionGraphBundle>(response);
        result!.Result!.Id.Should().Be(bundle.Id);
    }

    [Fact]
    public async Task Get_NonExisting_ShouldReturnNotFound()
    {
        var response = await Client.GetAsync($"api/actiongraphbundle/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAll_ShouldReturnList()
    {
        await SeedActionGraphBundleAsync(o => o.WithName("A"));
        await SeedActionGraphBundleAsync(o => o.WithName("B"));

        var response = await Client.GetAsync("api/actiongraphbundle");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<IEnumerable<ActionGraphBundle>>(response);
        result!.Result.Should().HaveCount(2);
    }

    [Fact]
    public async Task Delete_ShouldRemoveBundle()
    {
        var bundle = await SeedActionGraphBundleAsync();

        var response = await Client.DeleteAsync($"api/actiongraphbundle/{bundle.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Delete_NonExisting_ShouldReturnNotFound()
    {
        var response = await Client.DeleteAsync($"api/actiongraphbundle/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ═══════════════════════════════════════════════════════════════
    // GENERATE
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Generate_ShouldSetGeneratedCode()
    {
        var bundle = await SeedActionGraphBundleAsync();
        var request = new ActionGraphBundleBuilder().Targeting("Solana").BuildGenerationRequest();

        var response = await Client.PostAsJsonAsync($"api/actiongraphbundle/{bundle.Id}/generate", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<ActionGraphBundle>(response);
        result!.Result!.GeneratedCode.Should().NotBeNullOrEmpty();
        result.Result.TargetChain.Should().Be("Solana");
    }

    [Fact]
    public async Task Generate_NonExisting_ShouldReturnBadRequest()
    {
        var request = new ActionGraphBundleBuilder().BuildGenerationRequest();

        var response = await Client.PostAsJsonAsync($"api/actiongraphbundle/{Guid.NewGuid()}/generate", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ═══════════════════════════════════════════════════════════════
    // DEPLOY
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Deploy_ShouldSetDeploymentConfig()
    {
        // GeneratedCode is not a create-model field (ActionGraphBundleCreateModel carries only
        // Name/Description/PublicKey/UserId) — the seed builder's WithGeneratedCode
        // is dropped on POST. GeneratedCode is set ONLY by the generate endpoint, so
        // deploy must be preceded by a real generate (the production flow).
        var bundle = await SeedActionGraphBundleAsync();
        var generateRequest = new ActionGraphBundleBuilder().Targeting("Algorand").BuildGenerationRequest();
        var generateResponse = await Client.PostAsJsonAsync($"api/actiongraphbundle/{bundle.Id}/generate", generateRequest);
        generateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await Client.PostAsync($"api/actiongraphbundle/{bundle.Id}/deploy", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<ActionGraphBundle>(response);
        result!.Result!.DeploymentConfig.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Deploy_WithoutGeneration_ShouldReturnBadRequest()
    {
        var bundle = await SeedActionGraphBundleAsync();

        var response = await Client.PostAsync($"api/actiongraphbundle/{bundle.Id}/deploy", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ═══════════════════════════════════════════════════════════════
    // AUTH
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Get_WithoutAuth_ShouldReturnUnauthorized()
    {
        var unauthClient = Factory.CreateClient();
        var response = await unauthClient.GetAsync($"api/actiongraphbundle/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ═══════════════════════════════════════════════════════════════
    // UPDATE (PUT)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_ValidId_ReturnsOk()
    {
        var bundle = await SeedActionGraphBundleAsync(o => o.WithName("BeforeUpdate"));
        var model = new ActionGraphBundleBuilder().WithName("AfterUpdate").BuildCreateModel();

        var response = await Client.PutAsJsonAsync($"api/actiongraphbundle/{bundle.Id}", model);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<ActionGraphBundle>(response);
        result!.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task Update_InvalidId_ReturnsNotFound()
    {
        // {id:guid} route constraint — non-guid routes do not match; ASP.NET returns 404.
        var model = new ActionGraphBundleBuilder().WithName("X").BuildCreateModel();

        var response = await Client.PutAsJsonAsync("api/actiongraphbundle/not-a-guid", model);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_Unauthorized_Returns401()
    {
        var unauthClient = Factory.CreateClient();
        var model = new ActionGraphBundleBuilder().WithName("X").BuildCreateModel();

        var response = await unauthClient.PutAsJsonAsync($"api/actiongraphbundle/{Guid.NewGuid()}", model);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ═══════════════════════════════════════════════════════════════
    // IDOR closure (security: cross-user overwrite prevention)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_DifferentUser_PrivateRecord_Returns404()
    {
        // User A creates a record via the default authenticated client.
        var userA_record = await SeedActionGraphBundleAsync(o => o.WithName("A's secret"));

        // User B authenticates and attempts to PUT to A's id.
        var userB = Guid.Parse("b2222222-2222-2222-2222-222222222222");
        using var clientB = Factory.CreateAuthenticatedClientForUser(userB);
        var hijack = new ActionGraphBundleBuilder().WithName("Owned by B now").WithDescription("hijack").BuildCreateModel();

        var response = await clientB.PutAsJsonAsync($"api/actiongraphbundle/{userA_record.Id}", hijack);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "PUT IDOR must be closed -- a different user cannot overwrite (or even confirm) A's private record by route id");

        // And the underlying record is untouched.
        var verify = await Client.GetAsync($"api/actiongraphbundle/{userA_record.Id}");
        verify.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<ActionGraphBundle>(verify);
        result!.Result!.Name.Should().Be("A's secret");
        result.Result.Description.Should().NotBe("hijack");
    }

    [Fact]
    public async Task Update_OwnRecord_Returns200()
    {
        // Same authenticated user can update its own record by route id.
        var bundle = await SeedActionGraphBundleAsync(o => o.WithName("Mine").WithDescription("v1"));
        var update = new ActionGraphBundleBuilder().WithName("Mine").WithDescription("v2").BuildCreateModel();

        var response = await Client.PutAsJsonAsync($"api/actiongraphbundle/{bundle.Id}", update);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<ActionGraphBundle>(response);
        result!.IsError.Should().BeFalse();
        result.Result!.Description.Should().Be("v2");
    }

    [Fact]
    public async Task CreateOrUpdate_DifferentUser_SameName_DoesNotOverwrite()
    {
        // POST IDOR closure: User A and User B both POST "Foo" -> two distinct records.
        var modelA = new ActionGraphBundleBuilder().WithName("Foo").WithDescription("A's Foo").BuildCreateModel();
        var responseA = await Client.PostAsJsonAsync("api/actiongraphbundle", modelA);
        responseA.StatusCode.Should().Be(HttpStatusCode.OK);
        var resultA = await ReadResultAsync<ActionGraphBundle>(responseA);

        var userB = Guid.Parse("b3333333-3333-3333-3333-333333333333");
        using var clientB = Factory.CreateAuthenticatedClientForUser(userB);
        var modelB = new ActionGraphBundleBuilder().WithName("Foo").WithDescription("B's Foo").BuildCreateModel();
        var responseB = await clientB.PostAsJsonAsync("api/actiongraphbundle", modelB);
        responseB.StatusCode.Should().Be(HttpStatusCode.OK);
        var resultB = await ReadResultAsync<ActionGraphBundle>(responseB);

        // Distinct records -- no overwrite.
        resultA!.Result!.Id.Should().NotBe(resultB!.Result!.Id);
        resultA.Result.Description.Should().Be("A's Foo");
        resultB.Result.Description.Should().Be("B's Foo");

        // Each user's scoped list shows its own record with original data preserved.
        var listResponse = await Client.GetAsync("api/actiongraphbundle");
        var list = await ReadResultAsync<IEnumerable<ActionGraphBundle>>(listResponse);
        list!.Result.Should().Contain(s => s.Id == resultA.Result.Id && s.Description == "A's Foo");
        list.Result.Should().NotContain(s => s.Id == resultB.Result.Id);

        var listResponseB = await clientB.GetAsync("api/actiongraphbundle");
        var listB = await ReadResultAsync<IEnumerable<ActionGraphBundle>>(listResponseB);
        listB!.Result.Should().Contain(s => s.Id == resultB.Result.Id && s.Description == "B's Foo");
    }
}
