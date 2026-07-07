using System.Text.Json;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Services.Quest;
using AZOA.WebAPI.Services.Quest.Predicates;

namespace AZOA.WebAPI.Services.Quest.Handlers;

/// <summary>
/// Handles <see cref="QuestNodeType.GateCheck"/>. Evaluates a tenant-supplied,
/// whitelisted boolean predicate over upstream node outputs, injected reads, and
/// holon lifecycle state, returning <c>Ok {"pass":true}</c> when the predicate
/// holds and <c>Fail</c> otherwise. A Fail propagates through the engine's existing
/// failed-predecessor skip (<c>QuestManager.cs:272-285</c>), gating downstream
/// nodes — so GateCheck <i>is</i> the branch primitive.
/// </summary>
/// <remarks>
/// <para>
/// The predicate references upstream outputs as
/// <c>upstream.&lt;nodeName&gt;.&lt;jsonPath&gt;</c> (mirroring
/// <see cref="ComposeOutputsNodeHandler"/>'s upstream gather: incoming edge →
/// source node → that node's <see cref="QuestNodeExecution.Output"/>), injected
/// reads as <c>reads.&lt;name&gt;</c> from <see cref="GateCheckNodeConfig.Reads"/>,
/// and holon lifecycle state as <c>holon.&lt;id&gt;.&lt;field&gt;</c> for each holon
/// id in <see cref="GateCheckNodeConfig.Holons"/> (smart-gates-holon-state §8.1).
/// The holon resolver reads the holon's CURRENT state directly — no upstream
/// <c>HolonGet</c> node is required to thread the value through.
/// </para>
/// <para>
/// The evaluator (<see cref="GatePredicateEvaluator"/>) is a closed-grammar
/// interpreter — no function calls, no reflection, no <c>eval</c>. Any parse
/// error, unknown path, or type mismatch surfaces as a
/// <see cref="GatePredicateException"/> which this handler catches and turns
/// into a <c>Fail</c> (it never lets an exception escape into the engine, and
/// the gate fails closed). A missing/unreadable/non-owned holon ALSO
    /// fails the gate closed (holon reads are owner-scoped to the run owner).
/// </para>
/// <para>
/// Chain-free: <c>RequiresChainCapability</c> stays the default <c>false</c>. The
/// handler reads <c>context.UpstreamExecutions</c>, the node config, and (when the
/// config lists holons) the holon manager — never a chain.
/// </para>
/// </remarks>
public sealed class GateCheckNodeHandler : IQuestNodeHandler
{
    private readonly IHolonManager _holonManager;

    public GateCheckNodeHandler(IHolonManager holonManager) => _holonManager = holonManager;

    public QuestNodeType NodeType => QuestNodeType.GateCheck;

    // RequiresChainCapability intentionally NOT overridden — stays default false.

    public async Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context, CancellationToken ct = default)
    {
        if (!QuestNodeConfig.TryDeserialize<GateCheckNodeConfig>(context.Node.Config, nameof(QuestNodeType.GateCheck), out var cfg, out var cfgParseError))
            return QuestNodeResults.Fail(cfgParseError);

        Dictionary<string, JsonElement> scope;
        try
        {
            scope = await BuildScopeAsync(context, cfg, ct);
        }
        catch (GatePredicateException ex)
        {
            // A holon that could not be resolved (missing / store error) fails the
            // gate CLOSED — same posture as a predicate error, never silently pass.
            return QuestNodeResults.Fail($"gate scope error: {ex.Message}");
        }

        bool pass;
        try
        {
            pass = GatePredicateEvaluator.Evaluate(cfg.Predicate, scope);
        }
        catch (GatePredicateException ex)
        {
            return QuestNodeResults.Fail($"gate predicate error: {ex.Message}");
        }

        return pass
            ? QuestNodeResults.Ok("{\"pass\":true}")
            : QuestNodeResults.Fail($"gate not met: {cfg.Predicate}");
    }

    /// <summary>
    /// Builds the evaluator scope: <c>upstream.&lt;nodeName&gt;</c> +
    /// <c>run.&lt;nodeName&gt;</c> roots built by the SHARED resolver helpers
    /// (<c>QuestConfigBindingResolver.BuildUpstreamScope</c>/<c>BuildRunScope</c>),
    /// merged with gate-local <c>reads.&lt;name&gt;</c> and
    /// <c>holon.&lt;id&gt;</c> keys. See Services/Quest/AGENTS.md §gate-predicate.
    /// </summary>
    private async Task<Dictionary<string, JsonElement>> BuildScopeAsync(
        QuestNodeExecutionContext context, GateCheckNodeConfig cfg, CancellationToken ct)
    {
        // OrdinalIgnoreCase so merged upstream./run./reads./holon. lookups behave
        // uniformly (matches the resolver's case-insensitive scope dicts).
        var scope = new Dictionary<string, JsonElement>(
            QuestConfigBindingResolver.BuildUpstreamScope(context.Node, context.Quest, context.UpstreamExecutions),
            StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in QuestConfigBindingResolver.BuildRunScope(context.Quest, context.AllRunExecutions))
            scope[key] = value;

        foreach (var (name, value) in cfg.Reads)
        {
            scope[$"reads.{name}"] = value;
        }

        // Holon-state resolver (§8.1): read each configured holon's CURRENT state
        // and key it as holon.<id> so the predicate can compare holon.<id>.<field>
        // directly. RUNNER-SCOPED: a predicate may only name holons owned by the
        // acting avatar (the RUNNER — context.ActingAvatarId, C1/H1). A holon owned
        // by any other avatar (including the quest owner on a marketplace run) is
        // rejected with the SAME error as not-found so existence cannot be probed
        // across avatars. Fail closed on a missing/unreadable/non-owned holon: the
        // gate must never silently pass when the lifecycle state it gates on cannot
        // be read.
        foreach (var holonId in cfg.Holons)
        {
            // Scope the read to the runner; the explicit AvatarId check below stays as
            // defense-in-depth (owner-STRICT: a public holon owned by another avatar is
            // still rejected — a predicate may only name the runner's own holons).
            var holonResult = await _holonManager.GetAsync(holonId, context.ActingAvatarId);
            if (holonResult.IsError || holonResult.Result is null
                || holonResult.Result.AvatarId != context.ActingAvatarId)
                throw new GatePredicateException($"holon '{holonId}' not found or unreadable");

            scope[$"holon.{holonId}"] = HolonStateJson(holonResult.Result);
        }

        return scope;
    }

    /// <summary>
    /// Flattens a holon's live lifecycle state into a JSON object so the predicate
    /// can read <c>holon.&lt;id&gt;.&lt;field&gt;</c>. Exposes the typed fields
    /// (name, assetType, tokenId, chainId, isActive, parentHolonId, avatarId) AND
    /// every <see cref="IHolon.Metadata"/> entry (e.g. <c>status</c>, <c>phase</c>).
    /// Metadata is where the consumer-owned lifecycle field (<c>status == "FUNDED"</c>)
    /// lives — AZOA derives no economic meaning from it, it only exposes it for
    /// comparison. A metadata key that collides with a typed field name is preferred
    /// (the consumer's explicit lifecycle value wins over the structural field).
    /// </summary>
    private static JsonElement HolonStateJson(IHolon holon)
    {
        var state = new Dictionary<string, object?>
        {
            ["id"] = holon.Id.ToString(),
            ["name"] = holon.Name,
            ["assetType"] = holon.AssetType,
            ["tokenId"] = holon.TokenId,
            ["chainId"] = holon.ChainId,
            ["isActive"] = holon.IsActive,
            ["parentHolonId"] = holon.ParentHolonId?.ToString(),
            ["avatarId"] = holon.AvatarId?.ToString(),
        };

        // Metadata overlays the typed fields: the consumer-authored lifecycle
        // values (status/phase/…) are the primary gate inputs.
        foreach (var (key, value) in holon.Metadata)
            state[key] = value;

        var json = JsonSerializer.Serialize(state, QuestNodeJson.Options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
