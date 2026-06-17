using System.Text.Json;
using OASIS.WebAPI.Interfaces.QuestExecution;
using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Services.Quest.Predicates;

namespace OASIS.WebAPI.Services.Quest.Handlers;

/// <summary>
/// Handles <see cref="QuestNodeType.GateCheck"/>. Evaluates a tenant-supplied,
/// whitelisted boolean predicate over upstream node outputs and injected reads,
/// returning <c>Ok {"pass":true}</c> when the predicate holds and <c>Fail</c>
/// otherwise. A Fail propagates through the engine's existing
/// failed-predecessor skip (<c>QuestManager.cs:272-285</c>), gating downstream
/// nodes — so GateCheck <i>is</i> the branch primitive.
/// </summary>
/// <remarks>
/// <para>
/// The predicate references upstream outputs as
/// <c>upstream.&lt;nodeName&gt;.&lt;jsonPath&gt;</c> (mirroring
/// <see cref="ComposeOutputsNodeHandler"/>'s upstream gather: incoming edge →
/// source node → that node's <see cref="QuestNodeExecution.Output"/>) and
/// injected reads as <c>reads.&lt;name&gt;</c> from
/// <see cref="GateCheckNodeConfig.Reads"/>.
/// </para>
/// <para>
/// The evaluator (<see cref="GatePredicateEvaluator"/>) is a closed-grammar
/// interpreter — no function calls, no reflection, no <c>eval</c>. Any parse
/// error, unknown path, or type mismatch surfaces as a
/// <see cref="GatePredicateException"/> which this handler catches and turns
/// into a <c>Fail</c> (it never lets an exception escape into the engine, and
/// the gate fails closed).
/// </para>
/// <para>
/// Store-free and chain-free: <c>RequiresChainCapability</c> stays the default
/// <c>false</c>. The handler reads only <c>context.UpstreamExecutions</c> and
/// the node config — never a manager, store, or chain.
/// </para>
/// </remarks>
public sealed class GateCheckNodeHandler : IQuestNodeHandler
{
    public QuestNodeType NodeType => QuestNodeType.GateCheck;

    // RequiresChainCapability intentionally NOT overridden — stays default false.

    public Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        var cfg = JsonSerializer.Deserialize<GateCheckNodeConfig>(context.Node.Config, QuestNodeJson.Options)
                  ?? new GateCheckNodeConfig();

        var scope = BuildScope(context, cfg);

        bool pass;
        try
        {
            pass = GatePredicateEvaluator.Evaluate(cfg.Predicate, scope);
        }
        catch (GatePredicateException ex)
        {
            return Task.FromResult(QuestNodeResults.Fail($"gate predicate error: {ex.Message}"));
        }

        return Task.FromResult(pass
            ? QuestNodeResults.Ok("{\"pass\":true}")
            : QuestNodeResults.Fail($"gate not met: {cfg.Predicate}"));
    }

    /// <summary>
    /// Builds the evaluator scope: upstream node outputs keyed by
    /// <c>upstream.&lt;nodeName&gt;</c> (parsed from each predecessor's
    /// <see cref="QuestNodeExecution.Output"/>) plus injected reads keyed by
    /// <c>reads.&lt;name&gt;</c>.
    /// </summary>
    private static Dictionary<string, JsonElement> BuildScope(
        QuestNodeExecutionContext context, GateCheckNodeConfig cfg)
    {
        var scope = new Dictionary<string, JsonElement>();

        var incomingNodeIds = context.Quest.Edges
            .Where(e => e.TargetNodeId == context.NodeId)
            .Select(e => e.SourceNodeId)
            .ToHashSet();

        foreach (var sourceNode in context.Quest.Nodes.Where(n => incomingNodeIds.Contains(n.Id)))
        {
            if (context.UpstreamExecutions.TryGetValue(sourceNode.Id, out var exec)
                && !string.IsNullOrWhiteSpace(exec.Output))
            {
                if (TryParseJson(exec.Output, out var element))
                {
                    scope[$"upstream.{sourceNode.Name}"] = element;
                }
            }
        }

        foreach (var (name, value) in cfg.Reads)
        {
            scope[$"reads.{name}"] = value;
        }

        return scope;
    }

    private static bool TryParseJson(string json, out JsonElement element)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            element = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }
}
