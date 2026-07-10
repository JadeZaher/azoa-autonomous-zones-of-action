using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.IntegrationTests.Controllers;

/// <summary>
/// Integration tests for the dapp-composition pipeline:
///   DappSeriesController  (/api/dapp-series)
///   DappCompositionController (/api/dapp-series/{id}/*)
///
/// Storage: InMemoryDappSeriesStore is a Singleton across the factory, so
/// series created in one test are visible to others.  Each test names its
/// series with a unique Guid suffix and always resolves it by ID to avoid
/// cross-test assertions bleeding.
///
/// No SurrealDB container required — InMemoryDappSeriesStore handles all
/// persistence for these tests.  Do NOT add SkipIfSurrealDbUnavailable guards.
///
/// ISTARManager is NOT mocked here (Moq is not in the test project).
/// The generate/deploy endpoints are expected to return BadRequest because
/// the series is never in a Ready state; this naturally validates the
/// full HTTP wiring without requiring a real STAR backend.
/// </summary>
public class DappSeriesControllerIntegrationTests : IntegrationTestBase
{
    public DappSeriesControllerIntegrationTests(AZOATestWebApplicationFactory factory) : base(factory) { }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private DappSeriesCreateModel UniqueCreateModel(string? description = null) => new()
    {
        Name        = $"TestSeries-{Guid.NewGuid():N}",
        Description = description,
    };

    private async Task<DappSeries> SeedSeriesAsync(string? description = null)
    {
        var model    = UniqueCreateModel(description);
        var response = await Client.PostAsJsonAsync("api/dapp-series", model, JsonOptions);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<AZOAResult<DappSeries>>(JsonOptions);
        return result?.Result ?? throw new InvalidOperationException(
            $"Series seed failed: {await response.Content.ReadAsStringAsync()}");
    }

    // ═══════════════════════════════════════════════════════════════
    // SERIES CRUD
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Create_ValidModel_ShouldReturnCreatedSeries()
    {
        var model = UniqueCreateModel("A test dApp");

        var response = await Client.PostAsJsonAsync("api/dapp-series", model, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<DappSeries>(response);
        result!.IsError.Should().BeFalse();
        result.Result!.Name.Should().Be(model.Name);
        result.Result.Description.Should().Be("A test dApp");
        result.Result.Status.Should().Be(DappSeries.StatusKind.Draft);
    }

    [Fact]
    public async Task Get_ExistingSeries_ShouldReturnSeries()
    {
        var series = await SeedSeriesAsync();

        var response = await Client.GetAsync($"api/dapp-series/{series.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<DappSeries>(response);
        result!.IsError.Should().BeFalse();
        result.Result!.Id.Should().Be(series.Id);
        result.Result.Name.Should().Be(series.Name);
    }

    [Fact]
    public async Task Get_NonExistingSeries_ShouldReturnNotFound()
    {
        var response = await Client.GetAsync($"api/dapp-series/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task List_ShouldIncludeCreatedSeries()
    {
        var series = await SeedSeriesAsync();

        var response = await Client.GetAsync("api/dapp-series");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<IEnumerable<DappSeries>>(response);
        result!.IsError.Should().BeFalse();
        result.Result.Should().Contain(s => s.Id == series.Id);
    }

    [Fact]
    public async Task List_WithStatusFilter_ShouldReturnOnlyMatchingStatus()
    {
        var series = await SeedSeriesAsync();

        var response = await Client.GetAsync("api/dapp-series?status=Draft");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<IEnumerable<DappSeries>>(response);
        result!.IsError.Should().BeFalse();
        // Every returned series must be in Draft status
        result.Result.Should().AllSatisfy(s => s.Status.Should().Be(DappSeries.StatusKind.Draft));
        // Our newly created series must appear in the list
        result.Result.Should().Contain(s => s.Id == series.Id);
    }

    [Fact]
    public async Task Update_ExistingSeries_ShouldApplyChanges()
    {
        var series = await SeedSeriesAsync();
        var update = new DappSeriesUpdateModel
        {
            Name        = $"Updated-{Guid.NewGuid():N}",
            Description = "Updated description",
            TargetChain = "algorand-devnet",
        };

        var response = await Client.PutAsJsonAsync($"api/dapp-series/{series.Id}", update, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<DappSeries>(response);
        result!.IsError.Should().BeFalse();
        result.Result!.Name.Should().Be(update.Name);
        result.Result.Description.Should().Be("Updated description");
        result.Result.TargetChain.Should().Be("algorand-devnet");
    }

    [Fact]
    public async Task Delete_DraftSeries_ShouldSucceed()
    {
        var series = await SeedSeriesAsync();

        var response = await Client.DeleteAsync($"api/dapp-series/{series.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify it is gone
        var getResponse = await Client.GetAsync($"api/dapp-series/{series.Id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ═══════════════════════════════════════════════════════════════
    // QUEST MANAGEMENT
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ListQuests_NewSeries_ShouldReturnEmptyList()
    {
        var series = await SeedSeriesAsync();

        var response = await Client.GetAsync($"api/dapp-series/{series.Id}/quests");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<IEnumerable<DappSeriesQuest>>(response);
        result!.IsError.Should().BeFalse();
        result.Result.Should().BeEmpty();
    }

    [Fact]
    public async Task AddQuest_NonExistentQuest_ShouldReturnBadRequest()
    {
        var series = await SeedSeriesAsync();
        var model  = new DappSeriesAddQuestModel { QuestId = Guid.NewGuid(), Order = 1 };

        var response = await Client.PostAsJsonAsync($"api/dapp-series/{series.Id}/quests", model, JsonOptions);

        // The manager validates that the quest exists in IQuestStore;
        // a random Guid won't exist → BadRequest
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ═══════════════════════════════════════════════════════════════
    // COMPOSITION PIPELINE (failure-path verification)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Validate_EmptySeries_ShouldReturnValidationResultShape()
    {
        var series = await SeedSeriesAsync();

        var response = await Client.GetAsync($"api/dapp-series/{series.Id}/validate");

        // The manager should return an AZOAResult even for an empty series
        // (all rules fail, but no 500).
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Compose_EmptySeries_ShouldReturnBadRequest()
    {
        var series = await SeedSeriesAsync();

        var response = await Client.PostAsync($"api/dapp-series/{series.Id}/compose", null);

        // No quests → composition validation fails → BadRequest
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var result = await response.Content.ReadFromJsonAsync<AZOAResult<DappManifest>>(JsonOptions);
        result!.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Manifest_BeforeCompose_ShouldReturnNotFound()
    {
        var series = await SeedSeriesAsync();

        var response = await Client.GetAsync($"api/dapp-series/{series.Id}/manifest");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Status_NewSeries_ShouldBeDraft()
    {
        var series = await SeedSeriesAsync();

        var response = await Client.GetAsync($"api/dapp-series/{series.Id}/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // Server emits the StatusKind enum as a JSON string via JsonStringEnumConverter
        // (Program.cs:42); raw-body substring check avoids configuring a separate
        // JSON-converter chain in the test client.
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"result\":\"Draft\"");
        body.Should().NotContain("\"isError\":true");
    }

    [Fact]
    public async Task Generate_NonReadySeries_ShouldReturnBadRequest()
    {
        var series = await SeedSeriesAsync();

        var response = await Client.PostAsync($"api/dapp-series/{series.Id}/generate", null);

        // Series is Draft, not Ready → GenerateAsync guards against it
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var result = await response.Content.ReadFromJsonAsync<AZOAResult<object>>(JsonOptions);
        result!.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Deploy_NonReadySeries_ShouldReturnBadRequest()
    {
        var series = await SeedSeriesAsync();

        var response = await Client.PostAsync($"api/dapp-series/{series.Id}/deploy", null);

        // Series is Draft → DeployAsync guards against it
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var result = await response.Content.ReadFromJsonAsync<AZOAResult<object>>(JsonOptions);
        result!.IsError.Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    // AUTH
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Get_WithoutAuth_ShouldReturnUnauthorized()
    {
        var unauthClient = Factory.CreateClient(); // no X-Test-Auth header
        var response = await unauthClient.GetAsync($"api/dapp-series/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_WithoutAuth_ShouldReturnUnauthorized()
    {
        var unauthClient = Factory.CreateClient();
        var response = await unauthClient.PostAsJsonAsync("api/dapp-series", UniqueCreateModel(), JsonOptions);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_AsPlainDappUser_ShouldReturnForbidden()
    {
        using var plainUser = Factory.CreateAuthenticatedClient(AzoaDappRoles.User);

        var response = await plainUser.PostAsJsonAsync("api/dapp-series", UniqueCreateModel(), JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Read_AsPlainDappUser_ShouldRemainAllowed()
    {
        var series = await SeedSeriesAsync();
        using var plainUser = Factory.CreateAuthenticatedClient(AzoaDappRoles.User);

        var response = await plainUser.GetAsync($"api/dapp-series/{series.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Compose_AsDeveloperWithoutManagerRole_ShouldReturnForbidden()
    {
        var series = await SeedSeriesAsync();
        using var developer = Factory.CreateAuthenticatedClient(AzoaDappRoles.Developer);

        var response = await developer.PostAsync($"api/dapp-series/{series.Id}/compose", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ═══════════════════════════════════════════════════════════════
    // SWAGGER / OPENAPI DOCUMENTATION
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SwaggerJson_ShouldListAllDappCompositionEndpoints()
    {
        var response = await Client.GetAsync("/swagger/v1/swagger.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotBeNullOrEmpty();

        // Assert all dapp-composition endpoint paths are documented in Swagger
        var expectedPaths = new[]
        {
            "/api/dapp-series",
            "/api/dapp-series/{id}",
            "/api/dapp-series/{seriesId}/quests",
            "/api/dapp-series/{seriesId}/quests/{questId}",
            "/api/dapp-series/{seriesId}/quests/{questId}/order",
            "/api/dapp-series/{seriesId}/quests/{questId}/mappings",
            "/api/dapp-series/{id}/compose",
            "/api/dapp-series/{id}/validate",
            "/api/dapp-series/{id}/manifest",
            "/api/dapp-series/{id}/generate",
            "/api/dapp-series/{id}/deploy",
            "/api/dapp-series/{id}/status",
        };

        foreach (var path in expectedPaths)
        {
            body.Should().Contain(path, $"Swagger document should include endpoint path: {path}");
        }
    }
}
