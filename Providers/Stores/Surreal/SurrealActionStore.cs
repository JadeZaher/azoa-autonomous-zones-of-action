using System.Text.Json;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using SurrealForge.Client.Schema;
using AZOA.WebAPI.Core.Surreal;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IActionStore"/>. Materialises the ActionDefinition aggregate
/// across the three definition-side tables introduced by surrealdb-migration
/// task 9: <c>action</c> (head row), <c>action_node</c> (one row per step),
/// <c>action_edge</c> (one row per directed control-flow link). Per-(run, node)
/// runtime state lives on <see cref="SurrealActionRunStore"/> +
/// <see cref="SurrealActionNodeExecutionStore"/>; this store is shape-only.
///
/// <para>
/// <b>Pattern</b> mirrors <see cref="SurrealApiKeyStore"/> /
/// <see cref="SurrealActionTemplateStore"/>: Guid('N') lowercase-hex record ids,
/// inline POCOs (replace with generated POCO when source-gen catches up — see
/// <c>AZOA.WebAPI.Persistence.SurrealDb.Models.ActionDefinition</c>), parameter-bound SurrealQL
/// only (G3 / SRDB0001 — no string interpolation of values).
/// </para>
///
/// <para>
/// <b>Aggregate fan-out</b>. <see cref="UpsertActionAsync"/> writes the parent
/// row + replaces the child node / edge sets in a single multi-statement
/// request. The order is (1) DELETE existing child rows for this action, (2)
/// UPSERT the parent action row, (3) CREATE the new node rows, (4) CREATE the
/// new edge rows. <see cref="GetActionAsync"/> issues three SELECTs (head +
/// nodes + edges) in one combined request and stitches them in C#.
/// </para>
///
/// <para>
/// <b>Dependencies persistence gap</b>: <see cref="ActionDefinition.Dependencies"/> is NOT
/// persisted by this store — the matching <c>action_dependency</c> .surql
/// schema file is outside this round's owned-files set (its mermaid stub at
/// <c>source/180_action_dependency.mermaid</c> exists but the runtime .surql
/// is owned by a follow-up round). Round-trips deliberately drop the
/// Dependencies list to an empty collection. Pre-launch this is acceptable
/// (no live data, dapp-composition flows are not yet active); when the
/// dependency .surql lands, extend this store to fan-out to a fourth child
/// table.
/// </para>
///
/// <para>
/// <b>Template + node-template</b> CRUD also lives on <see cref="IActionStore"/>
/// (legacy surface). Reads are implemented by SELECT against the
/// already-shipped <c>action_template</c> / <c>action_node_template</c> tables
/// (130 / 140); writes use UPSERT (UPDATE ... CONTENT). The read-only
/// <see cref="IActionTemplateStore"/> interface is unchanged.
/// </para>
/// </summary>
public sealed class SurrealActionStore : IActionStore
{
    private const string ActionTable             = "action";
    private const string ActionNodeTable         = "action_node";
    private const string ActionEdgeTable         = "action_edge";
    private const string ActionTemplateTable     = "action_template";
    private const string ActionNodeTemplateTable = "action_node_template";

    private readonly ISurrealExecutor _executor;

    public SurrealActionStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    // ── ActionDefinition CRUD ────────────────────────────────────────────────────────────

    public async Task<AZOAResult<ActionDefinition>> GetActionAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var surrealId = SurrealId.ToSurrealId(id);

            var headQ = SurrealQuery.SelectById(ActionTable, surrealId);

            var rows = await _executor.QueryAsync<ActionPoco>(headQ, ct);
            if (rows.Count == 0)
                return Missing<ActionDefinition>($"ActionDefinition {id} not found.");

            var action = ToDomain(rows[0]);
            await HydrateChildrenAsync(action, ct);
            return Ok(action);
        }
        catch (Exception ex)
        {
            return Err<ActionDefinition>($"SurrealActionStore.GetActionAsync({id}) failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<ActionDefinition>>> GetActionsByUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        try
        {
            var userLink = SurrealLink.ToLink("user", SurrealId.ToSurrealId(userId));
            var q = SurrealQuery<ActionPoco>.From()
                .Where(x => x.UserId == userLink);

            var rows = await _executor.QueryAsync<ActionPoco>(q, ct);
            return await HydrateManyAsync(rows, ct);
        }
        catch (Exception ex)
        {
            return Err<IEnumerable<ActionDefinition>>($"SurrealActionStore.GetActionsByUserAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<ActionDefinition>>> GetPublicActionsAsync(CancellationToken ct = default)
    {
        try
        {
            // Marketplace discovery: only owner-opted-in public actions. Status is
            // filtered to Active by the caller (ActionManager) since status lives on
            // ActionRun, not the ActionDefinition row.
            var q = SurrealQuery<ActionPoco>.From()
                .Where(x => x.IsPublic == true);

            var rows = await _executor.QueryAsync<ActionPoco>(q, ct);
            return await HydrateManyAsync(rows, ct);
        }
        catch (Exception ex)
        {
            return Err<IEnumerable<ActionDefinition>>($"SurrealActionStore.GetPublicActionsAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<ActionDefinition>>> GetActionsByDappSeriesAsync(
        Guid dappSeriesId, CancellationToken ct = default)
    {
        try
        {
            var seriesLink = SurrealLink.ToLink("dapp_series", SurrealId.ToSurrealId(dappSeriesId));
            var q = SurrealQuery<ActionPoco>.From()
                .Where(x => x.DappSeriesId == seriesLink);

            var rows = await _executor.QueryAsync<ActionPoco>(q, ct);
            return await HydrateManyAsync(rows, ct);
        }
        catch (Exception ex)
        {
            return Err<IEnumerable<ActionDefinition>>($"SurrealActionStore.GetActionsByDappSeriesAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<ActionDefinition>> UpsertActionAsync(ActionDefinition action, CancellationToken ct = default)
    {
        if (action is null)
            return Err<ActionDefinition>("UpsertActionAsync: action must not be null.");

        try
        {
            // Allocate id for freshly-authored actions so child rows can reference it.
            if (action.Id == Guid.Empty)
                action.Id = Guid.NewGuid();

            var surrealId = SurrealId.ToSurrealId(action.Id);
            var poco      = FromDomain(action);

            // Step 1: clear any prior child rows for this action so the upsert
            // replaces the graph rather than accumulating stale nodes / edges.
            // Step 2: UPSERT the action head row (UPDATE ... CONTENT creates if absent).
            // Each step is its own ExecuteAsync call so a SCHEMAFULL field-level
            // assertion failure in step 2 fails fast and does not leave the head
            // row in place with no children.
            // Two single-statement DELETEs combined into one request:
            // SurrealQuery.Of rejects a multi-statement (';'-joined) body, so
            // compose them via Combine (mirrors HydrateChildrenAsync).
            var actionLink = SurrealRecordParam.Of("action", surrealId);
            var deleteNodesQ = SurrealQuery
                .Of("DELETE action_node WHERE action_id = $_qid")
                .WithParam("_qid", actionLink);
            var deleteEdgesQ = SurrealQuery
                .Of("DELETE action_edge WHERE action_id = $_qid")
                .WithParam("_qid", actionLink);

            var deleteResp = await _executor.ExecuteAsync(
                SurrealQuery.Combine(deleteNodesQ, deleteEdgesQ), ct);
            deleteResp.EnsureAllOk();

            var headQ = SurrealQuery
                .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t",    ActionTable)
                .WithParam("_id",   surrealId)
                .WithParam("_body", poco);

            var headResp = await _executor.ExecuteAsync(headQ, ct);
            headResp.EnsureAllOk();

            // Step 3: insert nodes (CREATE per row keeps SCHEMAFULL validation
            // active per-statement; if any node violates an ASSERT the call
            // fails and the partial state is visible to the caller via the
            // raised exception).
            foreach (var node in action.Nodes)
            {
                if (node.Id == Guid.Empty) node.Id = Guid.NewGuid();
                if (node.ActionId == Guid.Empty) node.ActionId = action.Id;

                var nodePoco = FromDomain(node);
                var nodeQ = SurrealQuery
                    .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                    .WithParam("_t",    ActionNodeTable)
                    .WithParam("_id",   nodePoco.Id)
                    .WithParam("_body", nodePoco);

                var nodeResp = await _executor.ExecuteAsync(nodeQ, ct);
                nodeResp.EnsureAllOk();
            }

            // Step 4: insert edges.
            foreach (var edge in action.Edges)
            {
                if (edge.Id == Guid.Empty) edge.Id = Guid.NewGuid();
                if (edge.ActionId == Guid.Empty) edge.ActionId = action.Id;

                var edgePoco = FromDomain(edge);
                var edgeQ = SurrealQuery
                    .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                    .WithParam("_t",    ActionEdgeTable)
                    .WithParam("_id",   edgePoco.Id)
                    .WithParam("_body", edgePoco);

                var edgeResp = await _executor.ExecuteAsync(edgeQ, ct);
                edgeResp.EnsureAllOk();
            }

            // Dependencies are intentionally NOT persisted — see the class XML
            // doc "Dependencies persistence gap" section. Round-trip them as
            // empty.

            return new AZOAResult<ActionDefinition> { Result = action, Message = "Upserted." };
        }
        catch (Exception ex)
        {
            return Err<ActionDefinition>($"SurrealActionStore.UpsertActionAsync({action.Id}) failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<bool>> DeleteActionAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var surrealId = SurrealId.ToSurrealId(id);

            // Pre-check existence: a DELETE of a missing row is a silent no-op in
            // SurrealDB, and the post-delete absence check cannot distinguish
            // "deleted an existing row" from "never existed". Mirror the NFT/Holon
            // delete contract — a non-existent id is an error, not a success.
            // raw: narrow `id`-only projection, not a full-row SELECT — SelectById
            // fetches `SELECT *`; see Persistence/SurrealDb/CONVENTION.md §query-surface.
            var existsQ = SurrealQuery
                .Of("SELECT id FROM type::record($_t, $_id)")
                .WithParam("_t",  ActionTable)
                .WithParam("_id", surrealId);
            var existing = await _executor.QueryAsync<ActionIdProjection>(existsQ, ct);
            if (existing.Count == 0)
                return new AZOAResult<bool>
                {
                    Result = false, IsError = true, Message = $"ActionDefinition {id} not found.",
                };

            // Delete head + child rows. SurrealQuery.Of rejects a multi-statement
            // (';'-joined) body, so compose three single-statement DELETEs via
            // Combine (mirrors HydrateChildrenAsync / UpsertActionAsync). SurrealDB
            // does not emit a reliable "affected count" on DELETE across versions,
            // so we check the head row absence after the call.
            var actionLink = SurrealRecordParam.Of("action", surrealId);
            var deleteHeadQ = SurrealQuery
                .Of("DELETE type::record($_t, $_id)")
                .WithParam("_t",  ActionTable)
                .WithParam("_id", surrealId);
            var deleteNodesQ = SurrealQuery
                .Of("DELETE action_node WHERE action_id = $_qid")
                .WithParam("_qid", actionLink);
            var deleteEdgesQ = SurrealQuery
                .Of("DELETE action_edge WHERE action_id = $_qid")
                .WithParam("_qid", actionLink);

            var resp = await _executor.ExecuteAsync(
                SurrealQuery.Combine(deleteHeadQ, deleteNodesQ, deleteEdgesQ), ct);
            resp.EnsureAllOk();

            // Verify the head row is gone (returns empty set on missing row).
            // raw: narrow `id`-only projection — see §query-surface note above.
            var verify = SurrealQuery
                .Of("SELECT id FROM type::record($_t, $_id)")
                .WithParam("_t",  ActionTable)
                .WithParam("_id", surrealId);

            var rows = await _executor.QueryAsync<ActionIdProjection>(verify, ct);
            var deleted = rows.Count == 0;

            return new AZOAResult<bool>
            {
                Result  = deleted,
                Message = deleted ? "Deleted." : $"ActionDefinition {id} not found.",
                IsError = !deleted,
            };
        }
        catch (Exception ex)
        {
            return Err<bool>($"SurrealActionStore.DeleteActionAsync({id}) failed: {ex.Message}");
        }
    }

    // ── Definition lifecycle CAS (F6 TOCTOU guard) ────────────────────────────

    public async Task<int> TryTransitionActionStatusAsync(
        Guid id, ActionStatus expected, ActionStatus next, long expectedVersion, CancellationToken ct = default)
    {
        try
        {
            var surrealId = SurrealId.ToSurrealId(id);

            // Single conditional UPDATE: flip status + bump version ONLY when the
            // row is still at (expected status, expectedVersion). A concurrent
            // transition that already advanced status OR version yields zero
            // affected rows — returned VERBATIM. Field names are author-controlled
            // (allowlisted); every value is parameter-bound (SRDB0001 / G3).
            // SurrealDB 3.x requires SET before WHERE.
            var q = SurrealQuery
                .Of("UPDATE type::record($_t, $_id) SET status = $_next, version = version + 1 WHERE status = $_expected AND version = $_ver RETURN AFTER")
                .WithParam("_t",        ActionTable)
                .WithParam("_id",       surrealId)
                .WithParam("_next",     next.ToString())
                .WithParam("_expected", expected.ToString())
                .WithParam("_ver",      expectedVersion);

            var response = await _executor.ExecuteAsync(q, ct);
            if (response.Count == 0) return 0;
            var stmt = response[0];
            if (!stmt.IsOk) return 0;
            return stmt.AffectedCount();
        }
        catch (SurrealStatementException)
        {
            return 0;
        }
    }

    public async Task<int> TryConfirmActionStateAsync(
        Guid id, ActionStatus expected, long expectedVersion, CancellationToken ct = default)
    {
        try
        {
            var surrealId = SurrealId.ToSurrealId(id);

            // Conditional no-op self-write: sets version to its own value so the
            // row is untouched, but the WHERE predicate gates on (expected status,
            // expectedVersion). Affected>0 proves the definition has not moved
            // since the caller read it (closes unpublish-vs-run-start). No status
            // change, no version bump.
            var q = SurrealQuery
                .Of("UPDATE type::record($_t, $_id) SET version = version WHERE status = $_expected AND version = $_ver RETURN AFTER")
                .WithParam("_t",        ActionTable)
                .WithParam("_id",       surrealId)
                .WithParam("_expected", expected.ToString())
                .WithParam("_ver",      expectedVersion);

            var response = await _executor.ExecuteAsync(q, ct);
            if (response.Count == 0) return 0;
            var stmt = response[0];
            if (!stmt.IsOk) return 0;
            return stmt.AffectedCount();
        }
        catch (SurrealStatementException)
        {
            return 0;
        }
    }

    // ── ActionTemplate CRUD ────────────────────────────────────────────────────

    public async Task<AZOAResult<ActionTemplate>> GetActionTemplateAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery.SelectById(ActionTemplateTable, SurrealId.ToSurrealId(id));

            var rows = await _executor.QueryAsync<ActionTemplatePoco>(q, ct);
            return rows.Count == 0
                ? Missing<ActionTemplate>($"ActionTemplate {id} not found.")
                : Ok(ToDomain(rows[0]));
        }
        catch (Exception ex)
        {
            return Err<ActionTemplate>($"SurrealActionStore.GetActionTemplateAsync({id}) failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<ActionTemplate>>> GetAllActionTemplatesAsync(
        CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery<ActionTemplatePoco>.From();
            var rows = await _executor.QueryAsync<ActionTemplatePoco>(q, ct);
            IEnumerable<ActionTemplate> result = rows.Select(ToDomain).ToList();
            return new AZOAResult<IEnumerable<ActionTemplate>> { Result = result, Message = "Success" };
        }
        catch (Exception ex)
        {
            return Err<IEnumerable<ActionTemplate>>($"SurrealActionStore.GetAllActionTemplatesAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<ActionTemplate>> UpsertActionTemplateAsync(
        ActionTemplate template, CancellationToken ct = default)
    {
        if (template is null)
            return Err<ActionTemplate>("UpsertActionTemplateAsync: template must not be null.");

        try
        {
            if (template.Id == Guid.Empty) template.Id = Guid.NewGuid();
            var poco = FromDomain(template);

            var q = SurrealQuery
                .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t",    ActionTemplateTable)
                .WithParam("_id",   poco.Id)
                .WithParam("_body", poco);

            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();

            return new AZOAResult<ActionTemplate> { Result = template, Message = "Upserted." };
        }
        catch (Exception ex)
        {
            return Err<ActionTemplate>($"SurrealActionStore.UpsertActionTemplateAsync({template.Id}) failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<bool>> DeleteActionTemplateAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var surrealId = SurrealId.ToSurrealId(id);
            var del = SurrealQuery
                .Of("DELETE type::record($_t, $_id)")
                .WithParam("_t",  ActionTemplateTable)
                .WithParam("_id", surrealId);

            var resp = await _executor.ExecuteAsync(del, ct);
            resp.EnsureAllOk();

            // Verify the head row is gone. raw: narrow `id`-only projection —
            // see §query-surface note in Persistence/SurrealDb/CONVENTION.md.
            var verify = SurrealQuery
                .Of("SELECT id FROM type::record($_t, $_id)")
                .WithParam("_t",  ActionTemplateTable)
                .WithParam("_id", surrealId);

            var rows = await _executor.QueryAsync<ActionIdProjection>(verify, ct);
            var deleted = rows.Count == 0;

            return new AZOAResult<bool>
            {
                Result  = deleted,
                Message = deleted ? "Deleted." : $"ActionTemplate {id} not found.",
                IsError = !deleted,
            };
        }
        catch (Exception ex)
        {
            return Err<bool>($"SurrealActionStore.DeleteActionTemplateAsync({id}) failed: {ex.Message}");
        }
    }

    // ── ActionNodeTemplate CRUD ────────────────────────────────────────────────

    public async Task<AZOAResult<ActionNodeTemplate>> UpsertActionNodeTemplateAsync(
        ActionNodeTemplate template, CancellationToken ct = default)
    {
        if (template is null)
            return Err<ActionNodeTemplate>("UpsertActionNodeTemplateAsync: template must not be null.");

        try
        {
            if (template.Id == Guid.Empty) template.Id = Guid.NewGuid();
            var poco = FromDomain(template);

            var q = SurrealQuery
                .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t",    ActionNodeTemplateTable)
                .WithParam("_id",   poco.Id)
                .WithParam("_body", poco);

            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();

            return new AZOAResult<ActionNodeTemplate> { Result = template, Message = "Upserted." };
        }
        catch (Exception ex)
        {
            return Err<ActionNodeTemplate>(
                $"SurrealActionStore.UpsertActionNodeTemplateAsync({template.Id}) failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<ActionNodeTemplate>>> GetAllActionNodeTemplatesAsync(
        CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery<ActionNodeTemplatePoco>.From();
            var rows = await _executor.QueryAsync<ActionNodeTemplatePoco>(q, ct);
            IEnumerable<ActionNodeTemplate> result = rows.Select(ToDomain).ToList();
            return new AZOAResult<IEnumerable<ActionNodeTemplate>> { Result = result, Message = "Success" };
        }
        catch (Exception ex)
        {
            return Err<IEnumerable<ActionNodeTemplate>>(
                $"SurrealActionStore.GetAllActionNodeTemplatesAsync failed: {ex.Message}");
        }
    }

    // ── Hydration: child rows (nodes + edges) for a action ─────────────────────

    /// <summary>
    /// Loads the child rows for a single action and hangs them off the supplied
    /// instance. Two SELECTs in one combined request — keeps wire round-trips
    /// to one per action after the head fetch.
    /// </summary>
    private async Task HydrateChildrenAsync(ActionDefinition action, CancellationToken ct)
    {
        var surrealId = SurrealId.ToSurrealId(action.Id);

        var actionLink = SurrealRecordParam.Of("action", surrealId);

        // raw: both SELECTs are fused into one wire round-trip via Combine();
        // the typed/LINQ tiers have no multi-statement combine surface — see
        // Persistence/SurrealDb/CONVENTION.md §query-surface.
        var nodesQ = SurrealQuery
            .Of("SELECT * FROM action_node WHERE action_id = $_qid ORDER BY execution_order ASC")
            .WithParam("_qid", actionLink);

        var edgesQ = SurrealQuery
            .Of("SELECT * FROM action_edge WHERE action_id = $_qid")
            .WithParam("_qid", actionLink);

        var combined = SurrealQuery.Combine(nodesQ, edgesQ);
        var resp = await _executor.ExecuteAsync(combined, ct);
        resp.EnsureAllOk();

        var nodeRows = resp.GetValues<ActionNodePoco>(0);
        var edgeRows = resp.GetValues<ActionEdgePoco>(1);

        action.Nodes = nodeRows.Select(ToDomain).ToList();
        action.Edges = edgeRows.Select(ToDomain).ToList();
        // Dependencies are intentionally dropped — see class XML doc.
        action.Dependencies = new List<ActionDependency>();
    }

    private async Task<AZOAResult<IEnumerable<ActionDefinition>>> HydrateManyAsync(
        IReadOnlyList<ActionPoco> rows, CancellationToken ct)
    {
        var result = new List<ActionDefinition>(rows.Count);
        foreach (var row in rows)
        {
            var action = ToDomain(row);
            await HydrateChildrenAsync(action, ct);
            result.Add(action);
        }
        return new AZOAResult<IEnumerable<ActionDefinition>> { Result = result, Message = "Success" };
    }

    // ── Mapping: ActionDefinition ↔ ActionPoco ────────────────────────────────────────────

    private static ActionPoco FromDomain(ActionDefinition q) => new()
    {
        Id           = SurrealId.ToSurrealId(q.Id),
        UserId     = SurrealLink.ToLink("user", SurrealId.ToSurrealId(q.UserId)) ?? string.Empty,
        Name         = q.Name ?? string.Empty,
        Description  = q.Description,
        TemplateId   = q.TemplateId.HasValue   ? SurrealLink.ToLink("action_template", SurrealId.ToSurrealId(q.TemplateId.Value))   : null,
        DappSeriesId = q.DappSeriesId.HasValue ? SurrealLink.ToLink("dapp_series", SurrealId.ToSurrealId(q.DappSeriesId.Value)) : null,
        Metadata     = MetadataToJsonObject(q.Metadata),
        Status       = q.Status.ToString(),
        Version      = q.Version,
        IsPublic     = q.IsPublic,
        OriginUserId = q.OriginUserId.HasValue ? SurrealLink.ToLink("user", SurrealId.ToSurrealId(q.OriginUserId.Value)) : null,
        PublishedVersionHash = q.PublishedVersionHash,
        RunAccess    = q.RunAccess.ToString(),
        InvitedUserIds = q.InvitedUserIds
            .Select(a => SurrealLink.ToLink("user", SurrealId.ToSurrealId(a))!)
            .ToList(),
        CreatedDate  = ToUtcOffset(q.CreatedDate),
    };

    private static ActionDefinition ToDomain(ActionPoco p) => new()
    {
        Id           = FromSurrealIdDirect(p.Id),
        UserId     = string.IsNullOrEmpty(p.UserId) ? Guid.Empty : FromSurrealId(SurrealLink.FromLink(p.UserId)!),
        Name         = p.Name ?? string.Empty,
        Description  = p.Description,
        TemplateId   = string.IsNullOrEmpty(p.TemplateId)   ? null : FromSurrealId(SurrealLink.FromLink(p.TemplateId)!),
        DappSeriesId = string.IsNullOrEmpty(p.DappSeriesId) ? null : FromSurrealId(SurrealLink.FromLink(p.DappSeriesId)!),
        Metadata     = MetadataFromJsonObject(p.Metadata),
        Status       = Enum.TryParse<ActionStatus>(p.Status, out var st) ? st : ActionStatus.Draft,
        Version      = p.Version,
        IsPublic     = p.IsPublic,
        OriginUserId = string.IsNullOrEmpty(p.OriginUserId) ? null : FromSurrealId(SurrealLink.FromLink(p.OriginUserId)!),
        PublishedVersionHash = string.IsNullOrEmpty(p.PublishedVersionHash) ? null : p.PublishedVersionHash,
        RunAccess    = Enum.TryParse<ActionRunAccess>(p.RunAccess, out var ra) ? ra : ActionRunAccess.Open,
        InvitedUserIds = (p.InvitedUserIds ?? new List<string>())
            .Select(a => FromSurrealId(SurrealLink.FromLink(a)!))
            .Where(g => g != Guid.Empty)
            .ToList(),
        CreatedDate  = p.CreatedDate.UtcDateTime,
        Nodes        = new List<ActionNode>(),
        Edges        = new List<ActionEdge>(),
        Dependencies = new List<ActionDependency>(),
    };

    // ── Mapping: ActionNode ↔ ActionNodePoco ────────────────────────────────────

    private static ActionNodePoco FromDomain(ActionNode n) => new()
    {
        Id              = SurrealId.ToSurrealId(n.Id),
        ActionId         = SurrealLink.ToLink("action", SurrealId.ToSurrealId(n.ActionId)) ?? string.Empty,
        NodeTemplateId  = n.NodeTemplateId.HasValue ? SurrealLink.ToLink("action_node_template", SurrealId.ToSurrealId(n.NodeTemplateId.Value)) : null,
        NodeType        = n.NodeType.ToString(),
        Name            = n.Name ?? string.Empty,
        Config          = n.Config ?? "{}",
        IsEntry         = n.IsEntry,
        IsTerminal      = n.IsTerminal,
        ExecutionOrder  = n.ExecutionOrder,
    };

    private static ActionNode ToDomain(ActionNodePoco p) => new()
    {
        Id              = FromSurrealIdDirect(p.Id),
        ActionId         = string.IsNullOrEmpty(p.ActionId) ? Guid.Empty : FromSurrealId(SurrealLink.FromLink(p.ActionId)!),
        NodeTemplateId  = string.IsNullOrEmpty(p.NodeTemplateId) ? null : FromSurrealId(SurrealLink.FromLink(p.NodeTemplateId)!),
        NodeType        = ParseNodeType(p.NodeType),
        Name            = p.Name ?? string.Empty,
        Config          = string.IsNullOrEmpty(p.Config) ? "{}" : p.Config,
        IsEntry         = p.IsEntry,
        IsTerminal      = p.IsTerminal,
        ExecutionOrder  = p.ExecutionOrder,
    };

    // ── Mapping: ActionEdge ↔ ActionEdgePoco ────────────────────────────────────

    private static ActionEdgePoco FromDomain(ActionEdge e) => new()
    {
        Id            = SurrealId.ToSurrealId(e.Id),
        ActionId       = SurrealLink.ToLink("action", SurrealId.ToSurrealId(e.ActionId)) ?? string.Empty,
        SourceNodeId  = SurrealLink.ToLink("action_node", SurrealId.ToSurrealId(e.SourceNodeId)) ?? string.Empty,
        TargetNodeId  = SurrealLink.ToLink("action_node", SurrealId.ToSurrealId(e.TargetNodeId)) ?? string.Empty,
        Condition     = e.Condition,
        EdgeType      = e.EdgeType.ToString(),
    };

    private static ActionEdge ToDomain(ActionEdgePoco p) => new()
    {
        Id            = FromSurrealIdDirect(p.Id),
        ActionId       = string.IsNullOrEmpty(p.ActionId)       ? Guid.Empty : FromSurrealId(SurrealLink.FromLink(p.ActionId)!),
        SourceNodeId  = string.IsNullOrEmpty(p.SourceNodeId)  ? Guid.Empty : FromSurrealId(SurrealLink.FromLink(p.SourceNodeId)!),
        TargetNodeId  = string.IsNullOrEmpty(p.TargetNodeId)  ? Guid.Empty : FromSurrealId(SurrealLink.FromLink(p.TargetNodeId)!),
        Condition     = p.Condition,
        EdgeType      = ParseEdgeType(p.EdgeType),
    };

    // ── Mapping: ActionTemplate ↔ ActionTemplatePoco ───────────────────────────

    private static ActionTemplatePoco FromDomain(ActionTemplate t) => new()
    {
        Id             = SurrealId.ToSurrealId(t.Id),
        Name           = t.Name ?? string.Empty,
        Description    = t.Description,
        AuthorUserId = SurrealId.ToSurrealId(t.AuthorUserId),
        Parameters     = t.Parameters ?? "{}",
        Version        = string.IsNullOrEmpty(t.Version) ? "1.0.0" : t.Version,
        IsPublic       = t.IsPublic,
        Tags           = t.Tags?.ToList() ?? new List<string>(),
        Nodes          = t.Nodes.Select(FromDomainTemplateNode).ToList(),
        Edges          = t.Edges.Select(FromDomainTemplateEdge).ToList(),
    };

    private static ActionTemplate ToDomain(ActionTemplatePoco p)
    {
        var template = new ActionTemplate
        {
            Id             = FromSurrealIdDirect(p.Id),
            Name           = p.Name ?? string.Empty,
            Description    = p.Description,
            AuthorUserId = string.IsNullOrEmpty(p.AuthorUserId) ? Guid.Empty : FromSurrealId(p.AuthorUserId),
            Parameters     = string.IsNullOrEmpty(p.Parameters) ? "{}" : p.Parameters,
            Version        = string.IsNullOrEmpty(p.Version) ? "1.0.0" : p.Version,
            IsPublic       = p.IsPublic,
            Tags           = p.Tags?.ToList() ?? new List<string>(),
        };

        if (p.Nodes is not null)
            template.Nodes = p.Nodes.Select(n => ToDomainTemplateNode(n, template.Id)).ToList();
        if (p.Edges is not null)
            template.Edges = p.Edges.Select(e => ToDomainTemplateEdge(e, template.Id)).ToList();

        return template;
    }

    private static ActionTemplateNodePoco FromDomainTemplateNode(ActionTemplateNode n) => new()
    {
        Id              = SurrealId.ToSurrealId(n.Id),
        SlotId          = n.SlotId,
        NodeTemplateId  = SurrealId.ToSurrealId(n.NodeTemplateId),
        ParamOverrides  = n.ParamOverrides ?? "{}",
        IsEntry         = n.IsEntry,
        IsTerminal      = n.IsTerminal,
    };

    private static ActionTemplateNode ToDomainTemplateNode(ActionTemplateNodePoco n, Guid templateId) => new()
    {
        Id              = string.IsNullOrEmpty(n.Id) ? Guid.NewGuid() : FromSurrealIdDirect(n.Id),
        TemplateId      = templateId,
        SlotId          = n.SlotId ?? string.Empty,
        NodeTemplateId  = string.IsNullOrEmpty(n.NodeTemplateId) ? Guid.Empty : FromSurrealId(n.NodeTemplateId),
        ParamOverrides  = string.IsNullOrEmpty(n.ParamOverrides) ? "{}" : n.ParamOverrides,
        IsEntry         = n.IsEntry,
        IsTerminal      = n.IsTerminal,
    };

    private static ActionTemplateEdgePoco FromDomainTemplateEdge(ActionTemplateEdge e) => new()
    {
        Id            = SurrealId.ToSurrealId(e.Id),
        SourceSlotId  = e.SourceSlotId,
        TargetSlotId  = e.TargetSlotId,
        EdgeType      = e.EdgeType.ToString(),
    };

    private static ActionTemplateEdge ToDomainTemplateEdge(ActionTemplateEdgePoco e, Guid templateId) => new()
    {
        Id            = string.IsNullOrEmpty(e.Id) ? Guid.NewGuid() : FromSurrealIdDirect(e.Id),
        TemplateId    = templateId,
        SourceSlotId  = e.SourceSlotId ?? string.Empty,
        TargetSlotId  = e.TargetSlotId ?? string.Empty,
        EdgeType      = ParseEdgeType(e.EdgeType),
    };

    // ── Mapping: ActionNodeTemplate ↔ ActionNodeTemplatePoco ───────────────────

    private static ActionNodeTemplatePoco FromDomain(ActionNodeTemplate t) => new()
    {
        Id              = SurrealId.ToSurrealId(t.Id),
        Name            = t.Name ?? string.Empty,
        NodeType        = t.NodeType.ToString(),
        Description     = t.Description,
        DefaultConfig   = t.DefaultConfig  ?? "{}",
        ConfigSchema    = t.ConfigSchema   ?? "{}",
        InputSchema     = t.InputSchema    ?? "{}",
        OutputSchema    = t.OutputSchema   ?? "{}",
        Version         = string.IsNullOrEmpty(t.Version) ? "1.0.0" : t.Version,
        AuthorUserId  = SurrealId.ToSurrealId(t.AuthorUserId),
        IsPublic        = t.IsPublic,
        Tags            = t.Tags?.ToList() ?? new List<string>(),
    };

    private static ActionNodeTemplate ToDomain(ActionNodeTemplatePoco p) => new()
    {
        Id              = FromSurrealIdDirect(p.Id),
        Name            = p.Name ?? string.Empty,
        NodeType        = ParseNodeType(p.NodeType),
        Description     = p.Description,
        DefaultConfig   = string.IsNullOrEmpty(p.DefaultConfig) ? "{}" : p.DefaultConfig,
        ConfigSchema    = string.IsNullOrEmpty(p.ConfigSchema)  ? "{}" : p.ConfigSchema,
        InputSchema     = string.IsNullOrEmpty(p.InputSchema)   ? "{}" : p.InputSchema,
        OutputSchema    = string.IsNullOrEmpty(p.OutputSchema)  ? "{}" : p.OutputSchema,
        Version         = string.IsNullOrEmpty(p.Version) ? "1.0.0" : p.Version,
        AuthorUserId  = string.IsNullOrEmpty(p.AuthorUserId) ? Guid.Empty : FromSurrealId(p.AuthorUserId),
        IsPublic        = p.IsPublic,
        Tags            = p.Tags?.ToList() ?? new List<string>(),
    };

    // ── Shared helpers ────────────────────────────────────────────────────────


    private static Guid FromSurrealId(string id)
    {
        var stripped = StripIdPrefix(id);
        return Guid.TryParseExact(stripped, "N", out var g) ? g : Guid.Empty;
    }

    private static Guid FromSurrealIdDirect(string id)
        => Guid.ParseExact(id, "N");

    private static string StripIdPrefix(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var colon = raw.IndexOf(':');
        if (colon < 0 || colon >= raw.Length - 1) return raw;
        return raw[(colon + 1)..].Trim('⟨', '⟩');
    }

    private static DateTimeOffset ToUtcOffset(DateTime dt)
    {
        var utc = dt.Kind switch
        {
            DateTimeKind.Utc         => dt,
            DateTimeKind.Local       => dt.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
            _                        => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        };
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    /// <summary>
    /// Metadata is a SurrealDB object (key -> string). We serialise via
    /// JsonElement so the executor's existing System.Text.Json codepath emits
    /// the canonical object shape; the schema asserts TYPE object.
    /// </summary>
    private static JsonElement MetadataToJsonObject(Dictionary<string, string>? metadata)
    {
        var payload = metadata ?? new Dictionary<string, string>();
        return JsonSerializer.SerializeToElement(payload);
    }

    private static Dictionary<string, string> MetadataFromJsonObject(JsonElement element)
    {
        var result = new Dictionary<string, string>();
        if (element.ValueKind != JsonValueKind.Object) return result;
        foreach (var prop in element.EnumerateObject())
        {
            result[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString() ?? string.Empty
                : prop.Value.GetRawText();
        }
        return result;
    }

    private static ActionNodeType ParseNodeType(string? raw) =>
        Enum.TryParse<ActionNodeType>(raw, ignoreCase: false, out var v)
            ? v
            : throw new InvalidOperationException(
                $"Unrecognised ActionNodeType '{raw}' read from SurrealDB. " +
                "Schema ASSERT INSIDE [...] should have prevented this; refresh the schema.");

    private static ActionEdgeType ParseEdgeType(string? raw) =>
        Enum.TryParse<ActionEdgeType>(raw, ignoreCase: false, out var v)
            ? v
            : ActionEdgeType.Control;

    // ── AZOAResult helpers ───────────────────────────────────────────────────

    private static AZOAResult<T> Ok<T>(T value, string msg = "Success") =>
        new() { Result = value, Message = msg };

    private static AZOAResult<T> Missing<T>(string msg) =>
        new() { IsError = true, Message = msg, Result = default };

    private static AZOAResult<T> Err<T>(string msg) =>
        new() { IsError = true, Message = msg, Result = default };

    // ── POCOs (private — replace with generated POCO when source-gen catches up) ──

    private sealed class ActionPoco : ISurrealRecord
    {
        public string SchemaName => ActionTable;

        [JsonPropertyName("id")]              public string Id { get; set; } = string.Empty;
        [References(typeof(AZOA.WebAPI.Persistence.SurrealDb.Models.User))] [JsonPropertyName("user_id")]       public string UserId { get; set; } = string.Empty;
        [JsonPropertyName("name")]            public string? Name { get; set; }
        [JsonPropertyName("description")]     public string? Description { get; set; }
        [References(typeof(AZOA.WebAPI.Persistence.SurrealDb.Models.ActionTemplate))] [JsonPropertyName("template_id")]     public string? TemplateId { get; set; }
        [References(typeof(AZOA.WebAPI.Persistence.SurrealDb.Models.DappSeries))] [JsonPropertyName("dapp_series_id")]  public string? DappSeriesId { get; set; }
        [JsonPropertyName("metadata")]        public JsonElement Metadata { get; set; }
        [JsonPropertyName("status")]          public string? Status { get; set; }
        [JsonPropertyName("version")]         public long Version { get; set; }
        [JsonPropertyName("is_public")]       public bool IsPublic { get; set; }
        [References(typeof(AZOA.WebAPI.Persistence.SurrealDb.Models.User))] [JsonPropertyName("origin_user_id")] public string? OriginUserId { get; set; }
        [JsonPropertyName("published_version_hash")] public string? PublishedVersionHash { get; set; }
        [JsonPropertyName("run_access")]      public string? RunAccess { get; set; }
        [JsonPropertyName("invited_user_ids")] public List<string>? InvitedUserIds { get; set; }
        [JsonPropertyName("created_date")]    public DateTimeOffset CreatedDate { get; set; }
    }

    private sealed class ActionNodePoco : ISurrealRecord
    {
        public string SchemaName => ActionNodeTable;

        [JsonPropertyName("id")]                public string Id { get; set; } = string.Empty;
        [References(typeof(AZOA.WebAPI.Persistence.SurrealDb.Models.ActionDefinition))] [JsonPropertyName("action_id")]          public string ActionId { get; set; } = string.Empty;
        [References(typeof(AZOA.WebAPI.Persistence.SurrealDb.Models.ActionNodeTemplate))] [JsonPropertyName("node_template_id")]  public string? NodeTemplateId { get; set; }
        [JsonPropertyName("node_type")]         public string? NodeType { get; set; }
        [JsonPropertyName("name")]              public string? Name { get; set; }
        [JsonPropertyName("config")]            public string? Config { get; set; }
        [JsonPropertyName("is_entry")]          public bool IsEntry { get; set; }
        [JsonPropertyName("is_terminal")]       public bool IsTerminal { get; set; }
        [JsonPropertyName("execution_order")]   public int ExecutionOrder { get; set; }
    }

    private sealed class ActionEdgePoco : ISurrealRecord
    {
        public string SchemaName => ActionEdgeTable;

        [JsonPropertyName("id")]                public string Id { get; set; } = string.Empty;
        [References(typeof(AZOA.WebAPI.Persistence.SurrealDb.Models.ActionDefinition))] [JsonPropertyName("action_id")]          public string ActionId { get; set; } = string.Empty;
        [References(typeof(AZOA.WebAPI.Persistence.SurrealDb.Models.ActionNode))] [JsonPropertyName("source_node_id")]    public string SourceNodeId { get; set; } = string.Empty;
        [References(typeof(AZOA.WebAPI.Persistence.SurrealDb.Models.ActionNode))] [JsonPropertyName("target_node_id")]    public string TargetNodeId { get; set; } = string.Empty;
        [JsonPropertyName("condition")]         public string? Condition { get; set; }
        [JsonPropertyName("edge_type")]         public string? EdgeType { get; set; }
    }

    private sealed class ActionTemplatePoco : ISurrealRecord
    {
        public string SchemaName => ActionTemplateTable;

        [JsonPropertyName("id")]               public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")]             public string? Name { get; set; }
        [JsonPropertyName("description")]      public string? Description { get; set; }
        [JsonPropertyName("author_user_id")] public string AuthorUserId { get; set; } = string.Empty;
        [JsonPropertyName("parameters")]       public string? Parameters { get; set; }
        [JsonPropertyName("version")]          public string? Version { get; set; }
        [JsonPropertyName("is_public")]        public bool IsPublic { get; set; }
        [JsonPropertyName("nodes")]            public List<ActionTemplateNodePoco>? Nodes { get; set; }
        [JsonPropertyName("edges")]            public List<ActionTemplateEdgePoco>? Edges { get; set; }
        [JsonPropertyName("tags")]             public List<string>? Tags { get; set; }
    }

    private sealed class ActionTemplateNodePoco : ISurrealRecord
    {
        public string SchemaName => ActionNodeTemplateTable;

        [JsonPropertyName("id")]               public string Id { get; set; } = string.Empty;
        [JsonPropertyName("slot_id")]          public string? SlotId { get; set; }
        [JsonPropertyName("node_template_id")] public string NodeTemplateId { get; set; } = string.Empty;
        [JsonPropertyName("param_overrides")]  public string? ParamOverrides { get; set; }
        [JsonPropertyName("is_entry")]         public bool IsEntry { get; set; }
        [JsonPropertyName("is_terminal")]      public bool IsTerminal { get; set; }
    }

    private sealed class ActionTemplateEdgePoco : ISurrealRecord
    {
        public string SchemaName => ActionTemplateTable;

        [JsonPropertyName("id")]               public string Id { get; set; } = string.Empty;
        [JsonPropertyName("source_slot_id")]   public string? SourceSlotId { get; set; }
        [JsonPropertyName("target_slot_id")]   public string? TargetSlotId { get; set; }
        [JsonPropertyName("edge_type")]        public string? EdgeType { get; set; }
    }

    private sealed class ActionNodeTemplatePoco : ISurrealRecord
    {
        public string SchemaName => ActionNodeTemplateTable;

        [JsonPropertyName("id")]               public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")]             public string? Name { get; set; }
        [JsonPropertyName("node_type")]        public string? NodeType { get; set; }
        [JsonPropertyName("description")]      public string? Description { get; set; }
        [JsonPropertyName("default_config")]   public string? DefaultConfig { get; set; }
        [JsonPropertyName("config_schema")]    public string? ConfigSchema { get; set; }
        [JsonPropertyName("input_schema")]     public string? InputSchema { get; set; }
        [JsonPropertyName("output_schema")]    public string? OutputSchema { get; set; }
        [JsonPropertyName("version")]          public string? Version { get; set; }
        [JsonPropertyName("author_user_id")] public string AuthorUserId { get; set; } = string.Empty;
        [JsonPropertyName("is_public")]        public bool IsPublic { get; set; }
        [JsonPropertyName("tags")]             public List<string>? Tags { get; set; }
    }

    private sealed class ActionIdProjection : ISurrealRecord
    {
        public string SchemaName => ActionTable;

        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    }
}
