using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Sagas;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AZOA.WebAPI.IntegrationTests.Controllers;

/// <summary>
/// final-hardening-cutover Phase A1 — the regression test that would have caught
/// "durable actions are inert under shipped config". Starts a durable workflow run
/// through the HTTP surface, drives the saga processor deterministically (direct
/// ISagaProcessor tick — no sleeping), and asserts the run actually executes and
/// advances past its entry node. See Services/Sagas/AGENTS.md §direct-invoke.
/// </summary>
public class ActionWorkflowDurableExecutionIntegrationTests : IntegrationTestBase
{
    public ActionWorkflowDurableExecutionIntegrationTests(AZOATestWebApplicationFactory factory) : base(factory) { }

    /// Two-node Tier-1 action (Emit → Emit): both nodes auto-advance and need no
    /// chain capability, so a durable run drives entry→terminal→Succeeded purely
    /// through the saga processor.
    private static ActionCreateModel LinearAutoAdvanceAction(string name = "DurableWorkflowAction") => new()
    {
        Name = name,
        Description = "Linear Tier-1 Emit→Emit action for durable-execution e2e",
        Nodes =
        [
            new ActionNodeCreateModel
            {
                Name = "Entry",
                NodeType = ActionNodeType.Emit,
                Config = JsonSerializer.Serialize(new { payload = new { step = "entry" } }),
                IsEntry = true,
                IsTerminal = false
            },
            new ActionNodeCreateModel
            {
                Name = "Finish",
                NodeType = ActionNodeType.Emit,
                Config = JsonSerializer.Serialize(new { payload = new { step = "finish" } }),
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

    private async Task<ActionDefinition> CreateAndPublishActionAsync()
    {
        var create = await Client.PostAsJsonAsync("api/action", LinearAutoAdvanceAction(), JsonOptions);
        create.StatusCode.Should().Be(HttpStatusCode.OK, $"create failed: {await create.Content.ReadAsStringAsync()}");
        var action = (await ReadResultAsync<ActionDefinition>(create))!.Result!;

        var publish = await Client.PostAsync($"api/action/{action.Id}/publish", null);
        publish.StatusCode.Should().Be(HttpStatusCode.OK, $"publish failed: {await publish.Content.ReadAsStringAsync()}");
        return (await ReadResultAsync<ActionDefinition>(publish))!.Result!;
    }

    /// Drain all currently-due saga steps in fresh DI scopes until the outbox is
    /// idle. Multiple consecutive idle observations tolerate the enabled hosted
    /// processor briefly owning a step while this deterministic drain checks the
    /// same outbox. Returns the total number of steps whose handler ran here.
    private async Task<int> DrainSagaStepsAsync(int maxTicks = 64)
    {
        var total = 0;
        var idleTicks = 0;
        for (var tick = 0; tick < maxTicks; tick++)
        {
            using var scope = Factory.Services.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<ISagaProcessor>();
            var processed = await processor.ProcessDueStepsAsync(CancellationToken.None);
            total += processed;
            idleTicks = processed == 0 ? idleTicks + 1 : 0;
            if (idleTicks >= 3) break;
            if (processed == 0)
                await Task.Delay(25);
        }
        return total;
    }

    // ─── A1: durable run started via HTTP actually executes + advances ─────────

    [Fact]
    public async Task StartWorkflow_WithProcessorOn_EntryNodeExecutes_And_RunAdvances()
    {
        var skip = await SkipIfSurrealDbUnavailableAsync();
        Skip.IfNot(skip, "SurrealDB unavailable");

        var action = await CreateAndPublishActionAsync();

        // Start the durable workflow run through the HTTP surface. This returns
        // immediately: the run enqueues its entry node as a saga step and the
        // PROCESSOR is what must dispatch it (the A1 defect: with Sagas disabled
        // the processor never ran, so the entry step sat forever).
        var start = await Client.PostAsync($"api/action/{action.Id}/start-workflow", null);
        start.StatusCode.Should().Be(HttpStatusCode.OK, $"start-workflow failed: {await start.Content.ReadAsStringAsync()}");
        var run = (await ReadResultAsync<ActionRun>(start))!.Result!;
        run.ActionId.Should().Be(action.Id);
        run.Status.Should().Be(ActionRunStatus.Running, "the entry node is enqueued and the run is marked Running before the processor ticks");

        // Drive the saga processor deterministically (the tick the hosted service
        // runs when Sagas:Enabled=true, which is now the default). Before A1 this
        // work was enqueued but NEVER drained.
        var processed = await DrainSagaStepsAsync();
        processed.Should().BeGreaterThan(0, "the durable run enqueued at least the entry node — the processor must dispatch it (regression guard for A1)");

        // The run must have advanced past its entry node. Emit→Emit auto-advances
        // to a terminal Succeeded once the processor drains both steps.
        var stateResp = await Client.GetAsync($"api/action/runs/{run.Id}/execution-state");
        stateResp.StatusCode.Should().Be(HttpStatusCode.OK, $"execution-state failed: {await stateResp.Content.ReadAsStringAsync()}");
        var state = (await ReadResultAsync<ActionExecutionState>(stateResp))!.Result!;

        state.CompletedNodes.Should().BeGreaterThan(0, "the entry node must have actually executed once the processor ticked");
        state.PendingNodes.Should().Be(0, "no node should remain Pending after the outbox drains");
        state.Status.Should().Be(ActionRunStatus.Succeeded, "a linear auto-advance durable run reaches Succeeded once the processor drains it end-to-end");
    }

    // ─── A1 corollary: the run does NOT advance until the processor ticks ──────

    [Fact]
    public async Task StartWorkflow_BeforeProcessorTick_EntryNodeIsStillPending()
    {
        var skip = await SkipIfSurrealDbUnavailableAsync();
        Skip.IfNot(skip, "SurrealDB unavailable");

        var action = await CreateAndPublishActionAsync();

        var start = await Client.PostAsync($"api/action/{action.Id}/start-workflow", null);
        start.StatusCode.Should().Be(HttpStatusCode.OK, $"start-workflow failed: {await start.Content.ReadAsStringAsync()}");
        var run = (await ReadResultAsync<ActionRun>(start))!.Result!;

        // WITHOUT ticking the processor, no node has executed yet — the work is
        // durably enqueued and waiting. This pins the exact mechanism A1 broke:
        // dispatch is the processor's job, and only the processor's.
        var stateResp = await Client.GetAsync($"api/action/runs/{run.Id}/execution-state");
        var state = (await ReadResultAsync<ActionExecutionState>(stateResp))!.Result!;
        state.CompletedNodes.Should().Be(0, "no node executes until the saga processor dispatches the enqueued entry step");

        // Now drive the processor and confirm the previously-inert run advances —
        // proving the enqueued step was real work awaiting the (now-default-on) processor.
        var processed = await DrainSagaStepsAsync();
        processed.Should().BeGreaterThan(0);

        var afterResp = await Client.GetAsync($"api/action/runs/{run.Id}/execution-state");
        var after = (await ReadResultAsync<ActionExecutionState>(afterResp))!.Result!;
        after.CompletedNodes.Should().BeGreaterThan(0, "once the processor ticks, the durable run executes");
    }
}
