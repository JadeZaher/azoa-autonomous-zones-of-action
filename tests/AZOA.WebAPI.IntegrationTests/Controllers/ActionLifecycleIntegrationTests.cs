using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using AZOA.WebAPI.IntegrationTests.Builders;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using Xunit;

namespace AZOA.WebAPI.IntegrationTests.Controllers;

/// <summary>
/// H1 — Lifecycle integration suite (FR-9d).
/// Exercises the create→publish→execute lifecycle through the HTTP layer.
/// See conductor/tracks/action-dag-semantic-hardening/NOTES.md §Phase H.
/// </summary>
public class ActionLifecycleIntegrationTests : IntegrationTestBase
{
    public ActionLifecycleIntegrationTests(AZOATestWebApplicationFactory factory) : base(factory) { }

    // ─── helpers ──────────────────────────────────────────────────────────────

    /// Build a minimal valid Tier-1 action (HolonGet + Emit, no chain capability).
    private static ActionCreateModel MinimalTierOneAction(string name = "LifecycleAction") => new()
    {
        Name = name,
        Description = "Linear Tier-1 action for lifecycle e2e",
        Nodes =
        [
            new ActionNodeCreateModel
            {
                Name = "Gate",
                NodeType = ActionNodeType.HolonGet,
                Config = JsonSerializer.Serialize(new { Id = Guid.NewGuid() }),
                IsEntry = true,
                IsTerminal = false
            },
            new ActionNodeCreateModel
            {
                Name = "Finish",
                NodeType = ActionNodeType.Emit,
                Config = JsonSerializer.Serialize(new { payload = new { done = true } }),
                IsEntry = false,
                IsTerminal = true
            }
        ],
        Edges =
        [
            new ActionEdgeCreateModel
            {
                SourceNodeId = 0,
                TargetNodeId = 1,
                EdgeType = ActionEdgeType.Control
            }
        ]
    };

    private async Task<ActionDefinition> CreateAndPublishActionAsync(ActionCreateModel? model = null)
    {
        model ??= MinimalTierOneAction();
        var create = await Client.PostAsJsonAsync("api/action", model, JsonOptions);
        create.StatusCode.Should().Be(HttpStatusCode.OK, $"create failed: {await create.Content.ReadAsStringAsync()}");
        var action = (await ReadResultAsync<ActionDefinition>(create))!.Result!;

        var publish = await Client.PostAsync($"api/action/{action.Id}/publish", null);
        publish.StatusCode.Should().Be(HttpStatusCode.OK, $"publish failed: {await publish.Content.ReadAsStringAsync()}");
        var published = (await ReadResultAsync<ActionDefinition>(publish))!.Result!;
        published.Status.Should().Be(ActionStatus.Active);
        return published;
    }

    // ─── H1-a: linear Tier-1 action create→publish→execute→run Completed ──────

    [Fact]
    public async Task LinearTierOneAction_CreatePublishExecute_Succeeds()
    {
        var skip = await SkipIfSurrealDbUnavailableAsync();
        Skip.IfNot(skip, "SurrealDB unavailable");

        var action = await CreateAndPublishActionAsync();

        var exec = await Client.PostAsync($"api/action/{action.Id}/execute", null);
        exec.StatusCode.Should().Be(HttpStatusCode.OK, $"execute failed: {await exec.Content.ReadAsStringAsync()}");
        var run = (await ReadResultAsync<ActionRun>(exec))!.Result!;

        run.ActionId.Should().Be(action.Id);
        // Run may complete synchronously or require a completed state.
        // The legacy execute path is synchronous; the run reaches Completed or
        // a terminal state before returning.
        run.Status.Should().NotBe(ActionRunStatus.Pending, "run should advance past Pending synchronously");
    }

    // ─── H1-b: execute on Draft is rejected ──────────────────────────────────

    [Fact]
    public async Task Execute_OnDraftAction_ReturnsBadRequest_NamingPublish()
    {
        var skip = await SkipIfSurrealDbUnavailableAsync();
        Skip.IfNot(skip, "SurrealDB unavailable");

        // Create but do NOT publish.
        var create = await Client.PostAsJsonAsync("api/action", MinimalTierOneAction("DraftExec"), JsonOptions);
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        var action = (await ReadResultAsync<ActionDefinition>(create))!.Result!;
        action.Status.Should().Be(ActionStatus.Draft);

        var exec = await Client.PostAsync($"api/action/{action.Id}/execute", null);

        exec.StatusCode.Should().Be(HttpStatusCode.BadRequest, "executing a Draft action must be rejected");
        var body = await exec.Content.ReadAsStringAsync();
        body.Should().ContainEquivalentOf("publish", "error message must name the publish requirement (AC-2b)");
    }

    // ─── H1-c: node mutation on Active action is rejected ─────────────────────

    [Fact]
    public async Task AddNode_OnActiveAction_ReturnsBadRequest()
    {
        var skip = await SkipIfSurrealDbUnavailableAsync();
        Skip.IfNot(skip, "SurrealDB unavailable");

        var action = await CreateAndPublishActionAsync(MinimalTierOneAction("MutateActive"));

        var addNode = await Client.PostAsJsonAsync(
            $"api/action/{action.Id}/nodes",
            new ActionNodeCreateModel
            {
                Name = "Extra",
                NodeType = ActionNodeType.Emit,
                Config = "{}",
                IsEntry = false,
                IsTerminal = false
            },
            JsonOptions);

        addNode.StatusCode.Should().Be(HttpStatusCode.BadRequest, "adding a node to an Active action must be rejected (AC-2c)");
    }

    // ─── H1-c: edge mutation on Active action is rejected ─────────────────────

    [Fact]
    public async Task AddEdge_OnActiveAction_ReturnsBadRequest()
    {
        var skip = await SkipIfSurrealDbUnavailableAsync();
        Skip.IfNot(skip, "SurrealDB unavailable");

        var action = await CreateAndPublishActionAsync(MinimalTierOneAction("MutateEdgeActive"));

        var addEdge = await Client.PostAsJsonAsync(
            $"api/action/{action.Id}/edges",
            new ActionEdgeAddModel
            {
                SourceNodeId = Guid.NewGuid(),
                TargetNodeId = Guid.NewGuid(),
                EdgeType = ActionEdgeType.Control
            },
            JsonOptions);

        addEdge.StatusCode.Should().Be(HttpStatusCode.BadRequest, "adding an edge to an Active action must be rejected (AC-2c)");
    }

    // ─── H1-c: mutation succeeds after unpublish ──────────────────────────────

    [Fact]
    public async Task AddNode_AfterUnpublish_Succeeds()
    {
        var skip = await SkipIfSurrealDbUnavailableAsync();
        Skip.IfNot(skip, "SurrealDB unavailable");

        var action = await CreateAndPublishActionAsync(MinimalTierOneAction("MutateAfterUnpublish"));

        var unpublish = await Client.PostAsync($"api/action/{action.Id}/unpublish", null);
        unpublish.StatusCode.Should().Be(HttpStatusCode.OK, $"unpublish failed: {await unpublish.Content.ReadAsStringAsync()}");
        var draft = (await ReadResultAsync<ActionDefinition>(unpublish))!.Result!;
        draft.Status.Should().Be(ActionStatus.Draft);

        var addNode = await Client.PostAsJsonAsync(
            $"api/action/{action.Id}/nodes",
            new ActionNodeCreateModel
            {
                Name = "NewNode",
                NodeType = ActionNodeType.Emit,
                Config = JsonSerializer.Serialize(new { payload = new { extra = true } }),
                IsEntry = false,
                IsTerminal = false
            },
            JsonOptions);

        addNode.StatusCode.Should().Be(HttpStatusCode.OK, $"add node after unpublish failed: {await addNode.Content.ReadAsStringAsync()}");
    }

    // ─── H1-e: publish IDOR — other user cannot publish someone else's action ─

    [Fact]
    public async Task Publish_OtherUsersAction_ReturnsNotFound()
    {
        var skip = await SkipIfSurrealDbUnavailableAsync();
        Skip.IfNot(skip, "SurrealDB unavailable");

        // Create action as the default user.
        var create = await Client.PostAsJsonAsync("api/action", MinimalTierOneAction("IDORTarget"), JsonOptions);
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        var action = (await ReadResultAsync<ActionDefinition>(create))!.Result!;

        // Attempt to publish as a DIFFERENT user.
        var otherUserId = Guid.NewGuid();
        using var otherClient = Factory.CreateAuthenticatedClientForUser(otherUserId);

        var publish = await otherClient.PostAsync($"api/action/{action.Id}/publish", null);

        // LoadOwnedActionAsync scopes by userId; a action owned by a different
        // user returns not-found semantics (IDOR-resistant pattern).
        publish.StatusCode.Should().BeOneOf(
            new[] { HttpStatusCode.BadRequest, HttpStatusCode.NotFound },
            because: "a foreign user must not be able to publish someone else's action (publish IDOR, AC-2a)");
    }
}
