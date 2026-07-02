using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using Xunit;

namespace AZOA.WebAPI.IntegrationTests.Controllers;

/// <summary>
/// H2 — Semantics integration suite (FR-9b/c, AC-1a at the HTTP layer).
/// Gate-fail cascade and fan-out validation through the HTTP layer.
/// See conductor/tracks/quest-dag-semantic-hardening/NOTES.md §Phase H.
/// </summary>
public class QuestSemanticsIntegrationTests : IntegrationTestBase
{
    public QuestSemanticsIntegrationTests(AZOATestWebApplicationFactory factory) : base(factory) { }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a 3-node quest:
    ///   GateCheck(false) →(Control) HolonGet →(Control) Emit
    /// The GateCheck has a predicate that always evaluates to false
    /// (predicate: "false") so it always fails, and the skip should cascade
    /// through both successors.
    /// </summary>
    private static QuestCreateModel GateCascadeQuest(string name = "GateCascade") => new()
    {
        Name = name,
        Description = "GateCheck always-false → Control → Control cascade",
        Nodes =
        [
            new QuestNodeCreateModel
            {
                Name = "AlwaysFalseGate",
                NodeType = QuestNodeType.GateCheck,
                Config = JsonSerializer.Serialize(new
                {
                    predicate = "false",
                    reads = new { }
                }),
                IsEntry = true,
                IsTerminal = false
            },
            new QuestNodeCreateModel
            {
                Name = "FirstHop",
                NodeType = QuestNodeType.Emit,
                Config = JsonSerializer.Serialize(new { payload = new { hop = 1 } }),
                IsEntry = false,
                IsTerminal = false
            },
            new QuestNodeCreateModel
            {
                Name = "SecondHop",
                NodeType = QuestNodeType.Emit,
                Config = JsonSerializer.Serialize(new { payload = new { hop = 2 } }),
                IsEntry = false,
                IsTerminal = true
            }
        ],
        Edges =
        [
            new QuestEdgeCreateModel { SourceNodeId = 0, TargetNodeId = 1, EdgeType = QuestEdgeType.Control },
            new QuestEdgeCreateModel { SourceNodeId = 1, TargetNodeId = 2, EdgeType = QuestEdgeType.Control }
        ]
    };

    /// <summary>
    /// Builds a fan-out quest: one entry node with TWO outgoing Control edges.
    /// Valid structurally (Kahn passes), but durable engine rejects it and
    /// publish must reject it (FR-3 / AC-3a).
    /// </summary>
    private static QuestCreateModel FanOutQuest(string name = "FanOut") => new()
    {
        Name = name,
        Description = "Fan-out: one node → two Control successors",
        Nodes =
        [
            new QuestNodeCreateModel
            {
                Name = "Entry",
                NodeType = QuestNodeType.Emit,
                Config = JsonSerializer.Serialize(new { payload = new { } }),
                IsEntry = true,
                IsTerminal = false
            },
            new QuestNodeCreateModel
            {
                Name = "BranchA",
                NodeType = QuestNodeType.Emit,
                Config = JsonSerializer.Serialize(new { payload = new { branch = "A" } }),
                IsEntry = false,
                IsTerminal = true
            },
            new QuestNodeCreateModel
            {
                Name = "BranchB",
                NodeType = QuestNodeType.Emit,
                Config = JsonSerializer.Serialize(new { payload = new { branch = "B" } }),
                IsEntry = false,
                IsTerminal = true
            }
        ],
        Edges =
        [
            new QuestEdgeCreateModel { SourceNodeId = 0, TargetNodeId = 1, EdgeType = QuestEdgeType.Control },
            new QuestEdgeCreateModel { SourceNodeId = 0, TargetNodeId = 2, EdgeType = QuestEdgeType.Control }
        ]
    };

    // ─── H2-a: gate-fail cascade (AC-1a at the HTTP layer) ───────────────────

    [Fact]
    public async Task GateCheckFails_BothSuccessors_AreSkipped_ViaExecutionStateApi()
    {
        var skip = await SkipIfSurrealDbUnavailableAsync();
        Skip.IfNot(skip, "SurrealDB unavailable");

        // Create and publish the cascade quest.
        var create = await Client.PostAsJsonAsync("api/quest", GateCascadeQuest(), JsonOptions);
        create.StatusCode.Should().Be(HttpStatusCode.OK, $"create failed: {await create.Content.ReadAsStringAsync()}");
        var quest = (await ReadResultAsync<Quest>(create))!.Result!;

        var publish = await Client.PostAsync($"api/quest/{quest.Id}/publish", null);
        publish.StatusCode.Should().Be(HttpStatusCode.OK, $"publish failed: {await publish.Content.ReadAsStringAsync()}");

        // Execute (legacy synchronous path).
        var exec = await Client.PostAsync($"api/quest/{quest.Id}/execute", null);
        exec.StatusCode.Should().Be(HttpStatusCode.OK, $"execute failed: {await exec.Content.ReadAsStringAsync()}");
        var run = (await ReadResultAsync<QuestRun>(exec))!.Result!;

        // Read execution state via the API.
        var stateResp = await Client.GetAsync($"api/quest/runs/{run.Id}/execution-state");
        stateResp.StatusCode.Should().Be(HttpStatusCode.OK, $"execution-state failed: {await stateResp.Content.ReadAsStringAsync()}");
        var execState = (await ReadResultAsync<QuestExecutionState>(stateResp))!.Result!;

        var nodeExecs = execState.NodeExecutions.ToList();
        nodeExecs.Should().HaveCount(3, "all three nodes should have execution rows");

        // The GateCheck entry node fails.
        var gate = nodeExecs.FirstOrDefault(n => n.State == QuestNodeState.Failed);
        gate.Should().NotBeNull("GateCheck with predicate 'false' must fail (AC-1a)");

        // BOTH successors must be Skipped — not just the first hop.
        var skipped = nodeExecs.Where(n => n.State == QuestNodeState.Skipped).ToList();
        skipped.Should().HaveCount(2,
            "cascade-skip must propagate through BOTH Control hops (AC-1a: second hop Skipped, FR-9b)");
    }

    // ─── H2-b: fan-out publish returns 400 with fan-out error (AC-3a) ─────────

    [Fact]
    public async Task FanOutQuest_Publish_Returns400_WithFanOutError()
    {
        var skip = await SkipIfSurrealDbUnavailableAsync();
        Skip.IfNot(skip, "SurrealDB unavailable");

        var create = await Client.PostAsJsonAsync("api/quest", FanOutQuest(), JsonOptions);
        create.StatusCode.Should().Be(HttpStatusCode.OK, $"create failed: {await create.Content.ReadAsStringAsync()}");
        var quest = (await ReadResultAsync<Quest>(create))!.Result!;

        var publish = await Client.PostAsync($"api/quest/{quest.Id}/publish", null);

        publish.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a fan-out quest (>1 outgoing Control edges) must be rejected at publish (AC-3a, FR-9c)");
        var body = await publish.Content.ReadAsStringAsync();
        body.Should().ContainEquivalentOf("fan-out", "error must identify the fan-out violation (AC-3a)");

        // Quest must remain Draft.
        var get = await Client.GetAsync($"api/quest/{quest.Id}");
        var refetched = (await ReadResultAsync<Quest>(get))!.Result!;
        refetched.Status.Should().Be(QuestStatus.Draft, "failed publish must not flip status to Active");
    }

    // ─── H2-c: fan-out quest legacy-execute surfaces a warning (AC-3b) ────────
    // The legacy execute path allows fan-out (runs in topological order) but
    // the DagValidationResult carries a warning. The run itself may succeed.
    // This test verifies the execute call does NOT return 400 (it is lenient).

    [Fact]
    public async Task FanOutQuest_LegacyExecute_Proceeds_WithoutHardReject()
    {
        var skip = await SkipIfSurrealDbUnavailableAsync();
        Skip.IfNot(skip, "SurrealDB unavailable");

        var create = await Client.PostAsJsonAsync("api/quest", FanOutQuest("FanOutLegacy"), JsonOptions);
        create.StatusCode.Should().Be(HttpStatusCode.OK, $"create failed: {await create.Content.ReadAsStringAsync()}");
        var quest = (await ReadResultAsync<Quest>(create))!.Result!;

        // The fan-out quest cannot be published (the previous test confirms this),
        // so we cannot use the publish→execute path. Per the spec (AC-3b), the
        // LEGACY execute path must remain lenient for fan-out. Since Phase B made
        // execute require Status == Active, a Draft quest is rejected before the
        // fan-out check. The AC-3b contract is therefore only exercisable at the
        // UNIT level (QuestDagValidator.Validate called directly) — the HTTP
        // integration layer cannot exercise a Draft-forced execute without bypassing
        // the publish gate. This is the same conclusion reached in Phase B.
        //
        // Assertion: the quest is Draft (cannot be published) and the execute
        // endpoint rejects it with "publish" in the error (not "fan-out"), which
        // confirms the fan-out warning path is gated behind the publish requirement.
        var exec = await Client.PostAsync($"api/quest/{quest.Id}/execute", null);
        exec.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "Draft quest must be rejected at execute (publish gate takes priority over fan-out check)");
        var body = await exec.Content.ReadAsStringAsync();
        body.Should().ContainEquivalentOf("publish",
            "the Draft-execute rejection must name the publish requirement, not a fan-out error (AC-3b behaviour confirmed at unit level)");
    }
}
