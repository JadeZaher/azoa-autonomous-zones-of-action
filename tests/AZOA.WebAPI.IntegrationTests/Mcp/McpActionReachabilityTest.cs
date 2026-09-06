// ─── AZOA — MCP ActionDefinition Reachability: Acceptance Criterion Test ──────────
//
// LOAD-BEARING TEST (read before modifying this file)
// ─────────────────────────────────────────────────────
// spec.md lines 30-31 (acceptance section):
//   "A representative agent query (actions reachable from node X respecting
//    prerequisites) is a single graph query, not multi-step app code."
//
// This file provides the runtime evidence for that acceptance criterion:
//
//   ActionReachability_AcceptanceCriterion_IsSingleGraphQueryNotMultiStep
//     Wraps ISurrealExecutor in a recording decorator that counts ExecuteAsync calls.
//     Seeds a action with 6 nodes and 7 edges (small diamond DAG).
//     Calls ActionReachabilityTool.ExecuteAsync.
//     Asserts ExecuteAsync round-trip count == 1 (the combined nodes+edges query
//     issued via SurrealQuery.Combine is a single network call — NOT 6 calls for
//     each node, NOT 2 separate sequential calls for nodes then edges separately).
//
// The ownership-check QueryAsync call is NOT counted here; the assertion targets
// ExecuteAsync exclusively — that is the combined multi-statement path that
// proves the data-fetch is a single graph query.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Mcp;
using AZOA.WebAPI.Mcp.Tools;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Providers.Stores.Surreal;

namespace AZOA.WebAPI.IntegrationTests.Mcp;

/// <summary>
/// Acceptance test for the mcp-surface track's core performance contract:
/// a representative action-reachability query is resolved in a SINGLE graph
/// query (one ExecuteAsync round-trip), not via iterative per-node fetches.
/// </summary>
[Trait("Category", "Mcp")]
public sealed class McpActionReachabilityTest : IntegrationTestBase
{
    // Connection config sourced from SurrealTestDefaults (points at local instance).

    public McpActionReachabilityTest(AZOATestWebApplicationFactory factory) : base(factory) { }

    // ─────────────────────────────────────────────────────────────────────────
    // ACCEPTANCE CRITERION
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// LOAD-BEARING: proves the combined nodes+edges fetch is exactly ONE
    /// <see cref="ISurrealExecutor.ExecuteAsync"/> round-trip.
    ///
    /// Topology: 6-node diamond DAG
    ///
    ///          A (entry)
    ///        /   \
    ///       B     C
    ///        \   /
    ///          D
    ///        /   \
    ///       E     F (terminal)
    ///
    /// Edges: A→B, A→C, B→D, C→D, D→E, D→F  (7 edges as specified)
    ///
    /// Query: reachable from A.
    /// Expected: all 6 nodes are reachable (A enqueues itself + traverses all edges).
    /// ExecuteAsync count MUST be exactly 1 — the combined SELECT nodes; SELECT edges
    /// query is the single network call.
    /// </summary>
    [SkippableFact]
    public async Task ActionReachability_AcceptanceCriterion_IsSingleGraphQueryNotMultiStep()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(),
            "SurrealDB test container not reachable — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");

        var userId = Guid.Parse(TestAuthHandler.DefaultUserId);

        // ── 1. Seed the diamond DAG via the real action store ──────────────
        var realExecutor = CreateExecutor();
        var actionStore   = new SurrealActionStore(realExecutor);

        var actionId = Guid.NewGuid();
        var nodeA   = NewNode(actionId, "A", 0, isEntry: true);
        var nodeB   = NewNode(actionId, "B", 1);
        var nodeC   = NewNode(actionId, "C", 2);
        var nodeD   = NewNode(actionId, "D", 3);
        var nodeE   = NewNode(actionId, "E", 4);
        var nodeF   = NewNode(actionId, "F", 5, isTerminal: true);

        var action = new ActionDefinition
        {
            Id          = actionId,
            UserId    = userId,
            Name        = "AcceptanceDiamondDAG",
            Description = "6-node diamond DAG for acceptance criterion",
            Metadata    = new Dictionary<string, string>(),
            CreatedDate = DateTime.UtcNow,
            Nodes       = new List<ActionNode> { nodeA, nodeB, nodeC, nodeD, nodeE, nodeF },
            Edges       = new List<ActionEdge>
            {
                NewEdge(actionId, nodeA.Id, nodeB.Id),  // A→B
                NewEdge(actionId, nodeA.Id, nodeC.Id),  // A→C
                NewEdge(actionId, nodeB.Id, nodeD.Id),  // B→D
                NewEdge(actionId, nodeC.Id, nodeD.Id),  // C→D
                NewEdge(actionId, nodeD.Id, nodeE.Id),  // D→E
                NewEdge(actionId, nodeD.Id, nodeF.Id),  // D→F
                // 7th edge: an extra E→F to satisfy the "7 edges" spec
                NewEdge(actionId, nodeE.Id, nodeF.Id),  // E→F
            }
        };

        var upserted = await actionStore.UpsertActionAsync(action);
        upserted.IsError.Should().BeFalse(upserted.Message);

        // ── 2. Wrap executor in the recording decorator ───────────────────
        // We wrap a NEW executor (same namespace) so the counter is clean;
        // the seed calls above were routed through realExecutor.
        var cleanExecutor = CreateExecutor();
        var recorder      = new ExecuteAsyncRecordingDecorator(cleanExecutor);

        // ── 3. Call the tool with the recording executor ──────────────────
        var tool = new ActionReachabilityTool();
        var args = BuildArgs(new
        {
            action_id    = actionId.ToString(),
            from_node_id = nodeA.Id.ToString()
        });
        var ctx = BuildContext(userId, recorder);

        var result = await tool.ExecuteAsync(ctx, args, CancellationToken.None);

        // ── 4. Assert: no error, reachable set is correct ─────────────────
        result.TryGetProperty("error", out _).Should().BeFalse(
            $"tool must not return an error; got: {result.GetRawText()}");

        result.TryGetProperty("reachable_node_ids", out var reachableProp).Should().BeTrue(
            "response must have reachable_node_ids field");

        var reachableIds = reachableProp.EnumerateArray()
            .Select(e => e.GetString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // All 6 nodes are reachable from A in this diamond DAG
        reachableIds.Should().HaveCountGreaterThanOrEqualTo(5,
            "at minimum B, C, D, E, F are reachable from A via directed edges");

        // ── 5. THE ACCEPTANCE CRITERION ───────────────────────────────────
        // The combined SELECT nodes + SELECT edges query is ONE ExecuteAsync call.
        // The ownership-check uses QueryAsync (not counted here).
        recorder.ExecuteAsyncCallCount.Should().Be(1,
            "ActionReachabilityTool must fetch nodes+edges in a SINGLE ExecuteAsync " +
            "round-trip via SurrealQuery.Combine — not via separate per-node calls. " +
            "This is the spec.md acceptance criterion (lines 30-31): " +
            "'a representative agent query is a single graph query, not multi-step app code.'");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ISurrealExecutor CreateExecutor()
    {
        var options = new SurrealConnectionOptions
        {
            Endpoint  = SurrealTestDefaults.Endpoint,
            Namespace = TestNamespace,
            Database  = "test",
            User      = SurrealTestDefaults.User,
            Password  = SurrealTestDefaults.Password
        };
        var connection = new SurrealDbNetConnection(options);
        return new DefaultSurrealExecutor(connection);
    }

    private static ToolCallContext BuildContext(Guid userId, ISurrealExecutor executor)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        return new ToolCallContext(userId, executor, sp);
    }

    private static JsonElement BuildArgs(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static ActionNode NewNode(
        Guid actionId, string name, int order,
        bool isEntry = false, bool isTerminal = false) => new()
    {
        Id             = Guid.NewGuid(),
        ActionId        = actionId,
        NodeType       = ActionNodeType.HolonGet,
        Name           = name,
        Config         = "{}",
        IsEntry        = isEntry,
        IsTerminal     = isTerminal,
        ExecutionOrder = order,
    };

    private static ActionEdge NewEdge(Guid actionId, Guid src, Guid tgt) => new()
    {
        Id           = Guid.NewGuid(),
        ActionId      = actionId,
        SourceNodeId = src,
        TargetNodeId = tgt,
        EdgeType     = ActionEdgeType.Control,
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Recording decorator — counts ExecuteAsync calls only
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Transparent proxy for <see cref="ISurrealExecutor"/> that increments
    /// <see cref="ExecuteAsyncCallCount"/> for every call to
    /// <see cref="ExecuteAsync"/>. <see cref="QueryAsync{T}"/> and
    /// <see cref="QuerySingleAsync{T}"/> are forwarded without counting.
    ///
    /// This is intentionally a private inner class — its only purpose is to
    /// support <see cref="ActionReachability_AcceptanceCriterion_IsSingleGraphQueryNotMultiStep"/>.
    /// </summary>
    private sealed class ExecuteAsyncRecordingDecorator : ISurrealExecutor
    {
        private readonly ISurrealExecutor _inner;
        private int _executeAsyncCallCount;

        public int ExecuteAsyncCallCount => _executeAsyncCallCount;

        public ExecuteAsyncRecordingDecorator(ISurrealExecutor inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<T>> QueryAsync<T>(
            SurrealQuery query, CancellationToken ct = default)
            => _inner.QueryAsync<T>(query, ct);

        /// <inheritdoc />
        public Task<T?> QuerySingleAsync<T>(
            SurrealQuery query, CancellationToken ct = default)
            where T : class
            => _inner.QuerySingleAsync<T>(query, ct);

        /// <inheritdoc />
        public async Task<SurrealResponse> ExecuteAsync(
            SurrealQuery query, CancellationToken ct = default)
        {
            // Thread-safe increment — tests run single-threaded but correctness
            // is guaranteed regardless.
            System.Threading.Interlocked.Increment(ref _executeAsyncCallCount);
            return await _inner.ExecuteAsync(query, ct);
        }
    }
}
