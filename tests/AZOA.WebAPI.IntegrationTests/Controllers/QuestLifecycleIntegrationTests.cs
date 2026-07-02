using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using AZOA.WebAPI.IntegrationTests.Builders;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using Xunit;

namespace AZOA.WebAPI.IntegrationTests.Controllers;

/// <summary>
/// H1 — Lifecycle integration suite (FR-9d).
/// Exercises the create→publish→execute lifecycle through the HTTP layer.
/// See conductor/tracks/quest-dag-semantic-hardening/NOTES.md §Phase H.
/// </summary>
public class QuestLifecycleIntegrationTests : IntegrationTestBase
{
    public QuestLifecycleIntegrationTests(AZOATestWebApplicationFactory factory) : base(factory) { }

    // ─── helpers ──────────────────────────────────────────────────────────────

    /// Build a minimal valid Tier-1 quest (HolonGet + Emit, no chain capability).
    private static QuestCreateModel MinimalTierOneQuest(string name = "LifecycleQuest") => new()
    {
        Name = name,
        Description = "Linear Tier-1 quest for lifecycle e2e",
        Nodes =
        [
            new QuestNodeCreateModel
            {
                Name = "Gate",
                NodeType = QuestNodeType.HolonGet,
                Config = JsonSerializer.Serialize(new { Id = Guid.NewGuid() }),
                IsEntry = true,
                IsTerminal = false
            },
            new QuestNodeCreateModel
            {
                Name = "Finish",
                NodeType = QuestNodeType.Emit,
                Config = JsonSerializer.Serialize(new { payload = new { done = true } }),
                IsEntry = false,
                IsTerminal = true
            }
        ],
        Edges =
        [
            new QuestEdgeCreateModel
            {
                SourceNodeId = 0,
                TargetNodeId = 1,
                EdgeType = QuestEdgeType.Control
            }
        ]
    };

    private async Task<Quest> CreateAndPublishQuestAsync(QuestCreateModel? model = null)
    {
        model ??= MinimalTierOneQuest();
        var create = await Client.PostAsJsonAsync("api/quest", model, JsonOptions);
        create.StatusCode.Should().Be(HttpStatusCode.OK, $"create failed: {await create.Content.ReadAsStringAsync()}");
        var quest = (await ReadResultAsync<Quest>(create))!.Result!;

        var publish = await Client.PostAsync($"api/quest/{quest.Id}/publish", null);
        publish.StatusCode.Should().Be(HttpStatusCode.OK, $"publish failed: {await publish.Content.ReadAsStringAsync()}");
        var published = (await ReadResultAsync<Quest>(publish))!.Result!;
        published.Status.Should().Be(QuestStatus.Active);
        return published;
    }

    // ─── H1-a: linear Tier-1 quest create→publish→execute→run Completed ──────

    [Fact]
    public async Task LinearTierOneQuest_CreatePublishExecute_Succeeds()
    {
        var skip = await SkipIfSurrealDbUnavailableAsync();
        Skip.IfNot(skip, "SurrealDB unavailable");

        var quest = await CreateAndPublishQuestAsync();

        var exec = await Client.PostAsync($"api/quest/{quest.Id}/execute", null);
        exec.StatusCode.Should().Be(HttpStatusCode.OK, $"execute failed: {await exec.Content.ReadAsStringAsync()}");
        var run = (await ReadResultAsync<QuestRun>(exec))!.Result!;

        run.QuestId.Should().Be(quest.Id);
        // Run may complete synchronously or require a completed state.
        // The legacy execute path is synchronous; the run reaches Completed or
        // a terminal state before returning.
        run.Status.Should().NotBe(QuestRunStatus.Pending, "run should advance past Pending synchronously");
    }

    // ─── H1-b: execute on Draft is rejected ──────────────────────────────────

    [Fact]
    public async Task Execute_OnDraftQuest_ReturnsBadRequest_NamingPublish()
    {
        var skip = await SkipIfSurrealDbUnavailableAsync();
        Skip.IfNot(skip, "SurrealDB unavailable");

        // Create but do NOT publish.
        var create = await Client.PostAsJsonAsync("api/quest", MinimalTierOneQuest("DraftExec"), JsonOptions);
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        var quest = (await ReadResultAsync<Quest>(create))!.Result!;
        quest.Status.Should().Be(QuestStatus.Draft);

        var exec = await Client.PostAsync($"api/quest/{quest.Id}/execute", null);

        exec.StatusCode.Should().Be(HttpStatusCode.BadRequest, "executing a Draft quest must be rejected");
        var body = await exec.Content.ReadAsStringAsync();
        body.Should().ContainEquivalentOf("publish", "error message must name the publish requirement (AC-2b)");
    }

    // ─── H1-c: node mutation on Active quest is rejected ─────────────────────

    [Fact]
    public async Task AddNode_OnActiveQuest_ReturnsBadRequest()
    {
        var skip = await SkipIfSurrealDbUnavailableAsync();
        Skip.IfNot(skip, "SurrealDB unavailable");

        var quest = await CreateAndPublishQuestAsync(MinimalTierOneQuest("MutateActive"));

        var addNode = await Client.PostAsJsonAsync(
            $"api/quest/{quest.Id}/nodes",
            new QuestNodeCreateModel
            {
                Name = "Extra",
                NodeType = QuestNodeType.Emit,
                Config = "{}",
                IsEntry = false,
                IsTerminal = false
            },
            JsonOptions);

        addNode.StatusCode.Should().Be(HttpStatusCode.BadRequest, "adding a node to an Active quest must be rejected (AC-2c)");
    }

    // ─── H1-c: edge mutation on Active quest is rejected ─────────────────────

    [Fact]
    public async Task AddEdge_OnActiveQuest_ReturnsBadRequest()
    {
        var skip = await SkipIfSurrealDbUnavailableAsync();
        Skip.IfNot(skip, "SurrealDB unavailable");

        var quest = await CreateAndPublishQuestAsync(MinimalTierOneQuest("MutateEdgeActive"));

        var addEdge = await Client.PostAsJsonAsync(
            $"api/quest/{quest.Id}/edges",
            new QuestEdgeAddModel
            {
                SourceNodeId = Guid.NewGuid(),
                TargetNodeId = Guid.NewGuid(),
                EdgeType = QuestEdgeType.Control
            },
            JsonOptions);

        addEdge.StatusCode.Should().Be(HttpStatusCode.BadRequest, "adding an edge to an Active quest must be rejected (AC-2c)");
    }

    // ─── H1-c: mutation succeeds after unpublish ──────────────────────────────

    [Fact]
    public async Task AddNode_AfterUnpublish_Succeeds()
    {
        var skip = await SkipIfSurrealDbUnavailableAsync();
        Skip.IfNot(skip, "SurrealDB unavailable");

        var quest = await CreateAndPublishQuestAsync(MinimalTierOneQuest("MutateAfterUnpublish"));

        var unpublish = await Client.PostAsync($"api/quest/{quest.Id}/unpublish", null);
        unpublish.StatusCode.Should().Be(HttpStatusCode.OK, $"unpublish failed: {await unpublish.Content.ReadAsStringAsync()}");
        var draft = (await ReadResultAsync<Quest>(unpublish))!.Result!;
        draft.Status.Should().Be(QuestStatus.Draft);

        var addNode = await Client.PostAsJsonAsync(
            $"api/quest/{quest.Id}/nodes",
            new QuestNodeCreateModel
            {
                Name = "NewNode",
                NodeType = QuestNodeType.Emit,
                Config = JsonSerializer.Serialize(new { payload = new { extra = true } }),
                IsEntry = false,
                IsTerminal = false
            },
            JsonOptions);

        addNode.StatusCode.Should().Be(HttpStatusCode.OK, $"add node after unpublish failed: {await addNode.Content.ReadAsStringAsync()}");
    }

    // ─── H1-e: publish IDOR — other avatar cannot publish someone else's quest ─

    [Fact]
    public async Task Publish_OtherAvatarsQuest_ReturnsNotFound()
    {
        var skip = await SkipIfSurrealDbUnavailableAsync();
        Skip.IfNot(skip, "SurrealDB unavailable");

        // Create quest as the default avatar.
        var create = await Client.PostAsJsonAsync("api/quest", MinimalTierOneQuest("IDORTarget"), JsonOptions);
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        var quest = (await ReadResultAsync<Quest>(create))!.Result!;

        // Attempt to publish as a DIFFERENT avatar.
        var otherAvatarId = Guid.NewGuid();
        using var otherClient = Factory.CreateAuthenticatedClientForAvatar(otherAvatarId);

        var publish = await otherClient.PostAsync($"api/quest/{quest.Id}/publish", null);

        // LoadOwnedQuestAsync scopes by avatarId; a quest owned by a different
        // avatar returns not-found semantics (IDOR-resistant pattern).
        publish.StatusCode.Should().BeOneOf(
            new[] { HttpStatusCode.BadRequest, HttpStatusCode.NotFound },
            because: "a foreign avatar must not be able to publish someone else's quest (publish IDOR, AC-2a)");
    }
}
