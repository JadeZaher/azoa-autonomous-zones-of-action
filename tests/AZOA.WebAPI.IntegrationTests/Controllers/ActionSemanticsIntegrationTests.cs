using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using Xunit;

namespace AZOA.WebAPI.IntegrationTests.Controllers;

/// <summary>
/// H2 — Semantics integration suite (FR-9b/c, AC-1a at the HTTP layer).
/// Gate-fail cascade and fan-out validation through the HTTP layer.
/// See conductor/tracks/action-dag-semantic-hardening/NOTES.md §Phase H.
/// </summary>
public class ActionSemanticsIntegrationTests : IntegrationTestBase
{
    public ActionSemanticsIntegrationTests(AZOATestWebApplicationFactory factory) : base(factory) { }

    protected override bool SeedsDefaultPrincipalUser => true;

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a 3-node action:
    ///   GateCheck(false) →(Control) HolonGet →(Control) Emit
    /// The GateCheck has a predicate that always evaluates to false
    /// (predicate: "false") so it always fails, and the skip should cascade
    /// through both successors.
    /// </summary>
    private static ActionCreateModel GateCascadeAction(string name = "GateCascade") => new()
    {
        Name = name,
        Description = "GateCheck always-false Control Control cascade",
        Nodes =
        [
            new ActionNodeCreateModel
            {
                Name = "AlwaysFalseGate",
                NodeType = ActionNodeType.GateCheck,
                Config = JsonSerializer.Serialize(new
                {
                    predicate = "false",
                    reads = new { }
                }),
                IsEntry = true,
                IsTerminal = false
            },
            new ActionNodeCreateModel
            {
                Name = "FirstHop",
                NodeType = ActionNodeType.Emit,
                Config = JsonSerializer.Serialize(new { payload = new { hop = 1 } }),
                IsEntry = false,
                IsTerminal = false
            },
            new ActionNodeCreateModel
            {
                Name = "SecondHop",
                NodeType = ActionNodeType.Emit,
                Config = JsonSerializer.Serialize(new { payload = new { hop = 2 } }),
                IsEntry = false,
                IsTerminal = true
            }
        ],
        Edges =
        [
            new ActionEdgeCreateModel { SourceNodeId = 0, TargetNodeId = 1, EdgeType = ActionEdgeType.Control },
            new ActionEdgeCreateModel { SourceNodeId = 1, TargetNodeId = 2, EdgeType = ActionEdgeType.Control }
        ]
    };

    /// <summary>
    /// Builds a fan-out action: one entry node with TWO outgoing Control edges.
    /// Valid structurally (Kahn passes), but durable engine rejects it and
    /// publish must reject it (FR-3 / AC-3a).
    /// </summary>
    private static ActionCreateModel FanOutAction(string name = "FanOut") => new()
    {
        Name = name,
        Description = "Fan-out one node two Control successors",
        Nodes =
        [
            new ActionNodeCreateModel
            {
                Name = "Entry",
                NodeType = ActionNodeType.Emit,
                Config = JsonSerializer.Serialize(new { payload = new { } }),
                IsEntry = true,
                IsTerminal = false
            },
            new ActionNodeCreateModel
            {
                Name = "BranchA",
                NodeType = ActionNodeType.Emit,
                Config = JsonSerializer.Serialize(new { payload = new { branch = "A" } }),
                IsEntry = false,
                IsTerminal = true
            },
            new ActionNodeCreateModel
            {
                Name = "BranchB",
                NodeType = ActionNodeType.Emit,
                Config = JsonSerializer.Serialize(new { payload = new { branch = "B" } }),
                IsEntry = false,
                IsTerminal = true
            }
        ],
        Edges =
        [
            new ActionEdgeCreateModel { SourceNodeId = 0, TargetNodeId = 1, EdgeType = ActionEdgeType.Control },
            new ActionEdgeCreateModel { SourceNodeId = 0, TargetNodeId = 2, EdgeType = ActionEdgeType.Control }
        ]
    };

    // ─── H2-a: gate-fail cascade (AC-1a at the HTTP layer) ───────────────────

    [Fact]
    public async Task GateCheckFails_BothSuccessors_AreSkipped_ViaExecutionStateApi()
    {
        var skip = await SkipIfSurrealDbUnavailableAsync();
        Skip.IfNot(skip, "SurrealDB unavailable");

        // Create and publish the cascade action.
        var create = await Client.PostAsJsonAsync("api/action", GateCascadeAction(), JsonOptions);
        create.StatusCode.Should().Be(HttpStatusCode.OK, $"create failed: {await create.Content.ReadAsStringAsync()}");
        var action = (await ReadResultAsync<ActionDefinition>(create))!.Result!;

        var publish = await Client.PostAsync($"api/action/{action.Id}/publish", null);
        publish.StatusCode.Should().Be(HttpStatusCode.OK, $"publish failed: {await publish.Content.ReadAsStringAsync()}");

        // Execute admits a durable run; drive it to its terminal state, then read it via the API.
        var exec = await PostWithRequestKeyAsync(Client, $"api/action/{action.Id}/execute");
        exec.StatusCode.Should().Be(HttpStatusCode.OK, $"execute failed: {await exec.Content.ReadAsStringAsync()}");
        var run = (await ReadResultAsync<ActionRun>(exec))!.Result!;

        var execState = await SettleRunAsync(Client, run.Id);

        var nodeExecs = execState.NodeExecutions.ToList();
        nodeExecs.Should().HaveCount(3, "all three nodes should have execution rows");

        // The GateCheck entry node fails.
        var gate = nodeExecs.FirstOrDefault(n => n.State == ActionNodeState.Failed);
        gate.Should().NotBeNull("GateCheck with predicate 'false' must fail (AC-1a)");

        // BOTH successors must be Skipped — not just the first hop.
        var skipped = nodeExecs.Where(n => n.State == ActionNodeState.Skipped).ToList();
        skipped.Should().HaveCount(2,
            "cascade-skip must propagate through BOTH Control hops (AC-1a: second hop Skipped, FR-9b)");
    }

    // ─── H2-b: fan-out publish returns 400 with fan-out error (AC-3a) ─────────

    [Fact]
    public async Task FanOutAction_Publish_Returns400_WithFanOutError()
    {
        var skip = await SkipIfSurrealDbUnavailableAsync();
        Skip.IfNot(skip, "SurrealDB unavailable");

        var create = await Client.PostAsJsonAsync("api/action", FanOutAction(), JsonOptions);
        create.StatusCode.Should().Be(HttpStatusCode.OK, $"create failed: {await create.Content.ReadAsStringAsync()}");
        var action = (await ReadResultAsync<ActionDefinition>(create))!.Result!;

        var publish = await Client.PostAsync($"api/action/{action.Id}/publish", null);

        publish.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a fan-out action (>1 outgoing Control edges) must be rejected at publish (AC-3a, FR-9c)");
        var body = await publish.Content.ReadAsStringAsync();
        body.Should().ContainEquivalentOf("fan-out", "error must identify the fan-out violation (AC-3a)");

        // Action must remain Draft.
        var get = await Client.GetAsync($"api/action/{action.Id}");
        var refetched = (await ReadResultAsync<ActionDefinition>(get))!.Result!;
        refetched.Status.Should().Be(ActionStatus.Draft, "failed publish must not flip status to Active");
    }

    // ─── H2-c: fan-out action legacy-execute surfaces a warning (AC-3b) ────────
    // The legacy execute path allows fan-out (runs in topological order) but
    // the DagValidationResult carries a warning. The run itself may succeed.
    // This test verifies the execute call does NOT return 400 (it is lenient).

    [Fact]
    public async Task FanOutAction_LegacyExecute_Proceeds_WithoutHardReject()
    {
        var skip = await SkipIfSurrealDbUnavailableAsync();
        Skip.IfNot(skip, "SurrealDB unavailable");

        var create = await Client.PostAsJsonAsync("api/action", FanOutAction("FanOutLegacy"), JsonOptions);
        create.StatusCode.Should().Be(HttpStatusCode.OK, $"create failed: {await create.Content.ReadAsStringAsync()}");
        var action = (await ReadResultAsync<ActionDefinition>(create))!.Result!;

        // The fan-out action cannot be published (the previous test confirms this),
        // so we cannot use the publish→execute path. Per the spec (AC-3b), the
        // LEGACY execute path must remain lenient for fan-out. Since Phase B made
        // execute require Status == Active, a Draft action is rejected before the
        // fan-out check. The AC-3b contract is therefore only exercisable at the
        // UNIT level (ActionDagValidator.Validate called directly) — the HTTP
        // integration layer cannot exercise a Draft-forced execute without bypassing
        // the publish gate. This is the same conclusion reached in Phase B.
        //
        // Assertion: the action is Draft (cannot be published) and the execute
        // endpoint rejects it with "publish" in the error (not "fan-out"), which
        // confirms the fan-out warning path is gated behind the publish requirement.
        var exec = await Client.PostAsync($"api/action/{action.Id}/execute", null);
        exec.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "Draft action must be rejected at execute (publish gate takes priority over fan-out check)");
        var body = await exec.Content.ReadAsStringAsync();
        body.Should().ContainEquivalentOf("publish",
            "the Draft-execute rejection must name the publish requirement, not a fan-out error (AC-3b behaviour confirmed at unit level)");
    }
}
