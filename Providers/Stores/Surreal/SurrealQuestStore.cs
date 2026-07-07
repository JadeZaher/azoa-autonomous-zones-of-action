using System.Text.Json;
using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IQuestStore"/>. Materialises the Quest aggregate
/// across the three definition-side tables introduced by surrealdb-migration
/// task 9: <c>quest</c> (head row), <c>quest_node</c> (one row per step),
/// <c>quest_edge</c> (one row per directed control-flow link). Per-(run, node)
/// runtime state lives on <see cref="SurrealQuestRunStore"/> +
/// <see cref="SurrealQuestNodeExecutionStore"/>; this store is shape-only.
///
/// <para>
/// <b>Pattern</b> mirrors <see cref="SurrealApiKeyStore"/> /
/// <see cref="SurrealQuestTemplateStore"/>: Guid('N') lowercase-hex record ids,
/// inline POCOs (replace with generated POCO when source-gen catches up — see
/// <c>AZOA.WebAPI.Persistence.SurrealDb.Models.Quest</c>), parameter-bound SurrealQL
/// only (G3 / SRDB0001 — no string interpolation of values).
/// </para>
///
/// <para>
/// <b>Aggregate fan-out</b>. <see cref="UpsertQuestAsync"/> writes the parent
/// row + replaces the child node / edge sets in a single multi-statement
/// request. The order is (1) DELETE existing child rows for this quest, (2)
/// UPSERT the parent quest row, (3) CREATE the new node rows, (4) CREATE the
/// new edge rows. <see cref="GetQuestAsync"/> issues three SELECTs (head +
/// nodes + edges) in one combined request and stitches them in C#.
/// </para>
///
/// <para>
/// <b>Dependencies persistence gap</b>: <see cref="Quest.Dependencies"/> is NOT
/// persisted by this store — the matching <c>quest_dependency</c> .surql
/// schema file is outside this round's owned-files set (its mermaid stub at
/// <c>source/180_quest_dependency.mermaid</c> exists but the runtime .surql
/// is owned by a follow-up round). Round-trips deliberately drop the
/// Dependencies list to an empty collection. Pre-launch this is acceptable
/// (no live data, dapp-composition flows are not yet active); when the
/// dependency .surql lands, extend this store to fan-out to a fourth child
/// table.
/// </para>
///
/// <para>
/// <b>Template + node-template</b> CRUD also lives on <see cref="IQuestStore"/>
/// (legacy surface). Reads are implemented by SELECT against the
/// already-shipped <c>quest_template</c> / <c>quest_node_template</c> tables
/// (130 / 140); writes use UPSERT (UPDATE ... CONTENT). The read-only
/// <see cref="IQuestTemplateStore"/> interface is unchanged.
/// </para>
/// </summary>
public sealed class SurrealQuestStore : IQuestStore
{
    private const string QuestTable             = "quest";
    private const string QuestNodeTable         = "quest_node";
    private const string QuestEdgeTable         = "quest_edge";
    private const string QuestTemplateTable     = "quest_template";
    private const string QuestNodeTemplateTable = "quest_node_template";

    private readonly ISurrealExecutor _executor;

    public SurrealQuestStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    // ── Quest CRUD ────────────────────────────────────────────────────────────

    public async Task<AZOAResult<Quest>> GetQuestAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var surrealId = SurrealId.ToSurrealId(id);

            var headQ = SurrealQuery.SelectById(QuestTable, surrealId);

            var rows = await _executor.QueryAsync<QuestPoco>(headQ, ct);
            if (rows.Count == 0)
                return Missing<Quest>($"Quest {id} not found.");

            var quest = ToDomain(rows[0]);
            await HydrateChildrenAsync(quest, ct);
            return Ok(quest);
        }
        catch (Exception ex)
        {
            return Err<Quest>($"SurrealQuestStore.GetQuestAsync({id}) failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<Quest>>> GetQuestsByAvatarAsync(
        Guid avatarId, CancellationToken ct = default)
    {
        try
        {
            var avatarLink = SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(avatarId));
            var q = SurrealQuery<QuestPoco>.From()
                .Where(x => x.AvatarId == avatarLink);

            var rows = await _executor.QueryAsync<QuestPoco>(q, ct);
            return await HydrateManyAsync(rows, ct);
        }
        catch (Exception ex)
        {
            return Err<IEnumerable<Quest>>($"SurrealQuestStore.GetQuestsByAvatarAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<Quest>>> GetPublicQuestsAsync(CancellationToken ct = default)
    {
        try
        {
            // Marketplace discovery: only owner-opted-in public quests. Status is
            // filtered to Active by the caller (QuestManager) since status lives on
            // QuestRun, not the Quest row.
            var q = SurrealQuery<QuestPoco>.From()
                .Where(x => x.IsPublic == true);

            var rows = await _executor.QueryAsync<QuestPoco>(q, ct);
            return await HydrateManyAsync(rows, ct);
        }
        catch (Exception ex)
        {
            return Err<IEnumerable<Quest>>($"SurrealQuestStore.GetPublicQuestsAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<Quest>>> GetQuestsByDappSeriesAsync(
        Guid dappSeriesId, CancellationToken ct = default)
    {
        try
        {
            var seriesLink = SurrealLink.ToLink("dapp_series", SurrealId.ToSurrealId(dappSeriesId));
            var q = SurrealQuery<QuestPoco>.From()
                .Where(x => x.DappSeriesId == seriesLink);

            var rows = await _executor.QueryAsync<QuestPoco>(q, ct);
            return await HydrateManyAsync(rows, ct);
        }
        catch (Exception ex)
        {
            return Err<IEnumerable<Quest>>($"SurrealQuestStore.GetQuestsByDappSeriesAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<Quest>> UpsertQuestAsync(Quest quest, CancellationToken ct = default)
    {
        if (quest is null)
            return Err<Quest>("UpsertQuestAsync: quest must not be null.");

        try
        {
            // Allocate id for freshly-authored quests so child rows can reference it.
            if (quest.Id == Guid.Empty)
                quest.Id = Guid.NewGuid();

            var surrealId = SurrealId.ToSurrealId(quest.Id);
            var poco      = FromDomain(quest);

            // Step 1: clear any prior child rows for this quest so the upsert
            // replaces the graph rather than accumulating stale nodes / edges.
            // Step 2: UPSERT the quest head row (UPDATE ... CONTENT creates if absent).
            // Each step is its own ExecuteAsync call so a SCHEMAFULL field-level
            // assertion failure in step 2 fails fast and does not leave the head
            // row in place with no children.
            // Two single-statement DELETEs combined into one request:
            // SurrealQuery.Of rejects a multi-statement (';'-joined) body, so
            // compose them via Combine (mirrors HydrateChildrenAsync).
            var questLink = SurrealLink.ToLink("quest", surrealId);
            var deleteNodesQ = SurrealQuery
                .Of("DELETE quest_node WHERE quest_id = $_qid")
                .WithParam("_qid", questLink);
            var deleteEdgesQ = SurrealQuery
                .Of("DELETE quest_edge WHERE quest_id = $_qid")
                .WithParam("_qid", questLink);

            var deleteResp = await _executor.ExecuteAsync(
                SurrealQuery.Combine(deleteNodesQ, deleteEdgesQ), ct);
            deleteResp.EnsureAllOk();

            var headQ = SurrealQuery
                .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t",    QuestTable)
                .WithParam("_id",   surrealId)
                .WithParam("_body", poco);

            var headResp = await _executor.ExecuteAsync(headQ, ct);
            headResp.EnsureAllOk();

            // Step 3: insert nodes (CREATE per row keeps SCHEMAFULL validation
            // active per-statement; if any node violates an ASSERT the call
            // fails and the partial state is visible to the caller via the
            // raised exception).
            foreach (var node in quest.Nodes)
            {
                if (node.Id == Guid.Empty) node.Id = Guid.NewGuid();
                if (node.QuestId == Guid.Empty) node.QuestId = quest.Id;

                var nodePoco = FromDomain(node);
                var nodeQ = SurrealQuery
                    .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                    .WithParam("_t",    QuestNodeTable)
                    .WithParam("_id",   nodePoco.Id)
                    .WithParam("_body", nodePoco);

                var nodeResp = await _executor.ExecuteAsync(nodeQ, ct);
                nodeResp.EnsureAllOk();
            }

            // Step 4: insert edges.
            foreach (var edge in quest.Edges)
            {
                if (edge.Id == Guid.Empty) edge.Id = Guid.NewGuid();
                if (edge.QuestId == Guid.Empty) edge.QuestId = quest.Id;

                var edgePoco = FromDomain(edge);
                var edgeQ = SurrealQuery
                    .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                    .WithParam("_t",    QuestEdgeTable)
                    .WithParam("_id",   edgePoco.Id)
                    .WithParam("_body", edgePoco);

                var edgeResp = await _executor.ExecuteAsync(edgeQ, ct);
                edgeResp.EnsureAllOk();
            }

            // Dependencies are intentionally NOT persisted — see the class XML
            // doc "Dependencies persistence gap" section. Round-trip them as
            // empty.

            return new AZOAResult<Quest> { Result = quest, Message = "Upserted." };
        }
        catch (Exception ex)
        {
            return Err<Quest>($"SurrealQuestStore.UpsertQuestAsync({quest.Id}) failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<bool>> DeleteQuestAsync(Guid id, CancellationToken ct = default)
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
                .WithParam("_t",  QuestTable)
                .WithParam("_id", surrealId);
            var existing = await _executor.QueryAsync<QuestIdProjection>(existsQ, ct);
            if (existing.Count == 0)
                return new AZOAResult<bool>
                {
                    Result = false, IsError = true, Message = $"Quest {id} not found.",
                };

            // Delete head + child rows. SurrealQuery.Of rejects a multi-statement
            // (';'-joined) body, so compose three single-statement DELETEs via
            // Combine (mirrors HydrateChildrenAsync / UpsertQuestAsync). SurrealDB
            // does not emit a reliable "affected count" on DELETE across versions,
            // so we check the head row absence after the call.
            var questLink = SurrealLink.ToLink("quest", surrealId);
            var deleteHeadQ = SurrealQuery
                .Of("DELETE type::record($_t, $_id)")
                .WithParam("_t",  QuestTable)
                .WithParam("_id", surrealId);
            var deleteNodesQ = SurrealQuery
                .Of("DELETE quest_node WHERE quest_id = $_qid")
                .WithParam("_qid", questLink);
            var deleteEdgesQ = SurrealQuery
                .Of("DELETE quest_edge WHERE quest_id = $_qid")
                .WithParam("_qid", questLink);

            var resp = await _executor.ExecuteAsync(
                SurrealQuery.Combine(deleteHeadQ, deleteNodesQ, deleteEdgesQ), ct);
            resp.EnsureAllOk();

            // Verify the head row is gone (returns empty set on missing row).
            // raw: narrow `id`-only projection — see §query-surface note above.
            var verify = SurrealQuery
                .Of("SELECT id FROM type::record($_t, $_id)")
                .WithParam("_t",  QuestTable)
                .WithParam("_id", surrealId);

            var rows = await _executor.QueryAsync<QuestIdProjection>(verify, ct);
            var deleted = rows.Count == 0;

            return new AZOAResult<bool>
            {
                Result  = deleted,
                Message = deleted ? "Deleted." : $"Quest {id} not found.",
                IsError = !deleted,
            };
        }
        catch (Exception ex)
        {
            return Err<bool>($"SurrealQuestStore.DeleteQuestAsync({id}) failed: {ex.Message}");
        }
    }

    // ── Definition lifecycle CAS (F6 TOCTOU guard) ────────────────────────────

    public async Task<int> TryTransitionQuestStatusAsync(
        Guid id, QuestStatus expected, QuestStatus next, long expectedVersion, CancellationToken ct = default)
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
                .WithParam("_t",        QuestTable)
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

    public async Task<int> TryConfirmQuestStateAsync(
        Guid id, QuestStatus expected, long expectedVersion, CancellationToken ct = default)
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
                .WithParam("_t",        QuestTable)
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

    // ── QuestTemplate CRUD ────────────────────────────────────────────────────

    public async Task<AZOAResult<QuestTemplate>> GetQuestTemplateAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery.SelectById(QuestTemplateTable, SurrealId.ToSurrealId(id));

            var rows = await _executor.QueryAsync<QuestTemplatePoco>(q, ct);
            return rows.Count == 0
                ? Missing<QuestTemplate>($"QuestTemplate {id} not found.")
                : Ok(ToDomain(rows[0]));
        }
        catch (Exception ex)
        {
            return Err<QuestTemplate>($"SurrealQuestStore.GetQuestTemplateAsync({id}) failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<QuestTemplate>>> GetAllQuestTemplatesAsync(
        CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery<QuestTemplatePoco>.From();
            var rows = await _executor.QueryAsync<QuestTemplatePoco>(q, ct);
            IEnumerable<QuestTemplate> result = rows.Select(ToDomain).ToList();
            return new AZOAResult<IEnumerable<QuestTemplate>> { Result = result, Message = "Success" };
        }
        catch (Exception ex)
        {
            return Err<IEnumerable<QuestTemplate>>($"SurrealQuestStore.GetAllQuestTemplatesAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<QuestTemplate>> UpsertQuestTemplateAsync(
        QuestTemplate template, CancellationToken ct = default)
    {
        if (template is null)
            return Err<QuestTemplate>("UpsertQuestTemplateAsync: template must not be null.");

        try
        {
            if (template.Id == Guid.Empty) template.Id = Guid.NewGuid();
            var poco = FromDomain(template);

            var q = SurrealQuery
                .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t",    QuestTemplateTable)
                .WithParam("_id",   poco.Id)
                .WithParam("_body", poco);

            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();

            return new AZOAResult<QuestTemplate> { Result = template, Message = "Upserted." };
        }
        catch (Exception ex)
        {
            return Err<QuestTemplate>($"SurrealQuestStore.UpsertQuestTemplateAsync({template.Id}) failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<bool>> DeleteQuestTemplateAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var surrealId = SurrealId.ToSurrealId(id);
            var del = SurrealQuery
                .Of("DELETE type::record($_t, $_id)")
                .WithParam("_t",  QuestTemplateTable)
                .WithParam("_id", surrealId);

            var resp = await _executor.ExecuteAsync(del, ct);
            resp.EnsureAllOk();

            // Verify the head row is gone. raw: narrow `id`-only projection —
            // see §query-surface note in Persistence/SurrealDb/CONVENTION.md.
            var verify = SurrealQuery
                .Of("SELECT id FROM type::record($_t, $_id)")
                .WithParam("_t",  QuestTemplateTable)
                .WithParam("_id", surrealId);

            var rows = await _executor.QueryAsync<QuestIdProjection>(verify, ct);
            var deleted = rows.Count == 0;

            return new AZOAResult<bool>
            {
                Result  = deleted,
                Message = deleted ? "Deleted." : $"QuestTemplate {id} not found.",
                IsError = !deleted,
            };
        }
        catch (Exception ex)
        {
            return Err<bool>($"SurrealQuestStore.DeleteQuestTemplateAsync({id}) failed: {ex.Message}");
        }
    }

    // ── QuestNodeTemplate CRUD ────────────────────────────────────────────────

    public async Task<AZOAResult<QuestNodeTemplate>> UpsertQuestNodeTemplateAsync(
        QuestNodeTemplate template, CancellationToken ct = default)
    {
        if (template is null)
            return Err<QuestNodeTemplate>("UpsertQuestNodeTemplateAsync: template must not be null.");

        try
        {
            if (template.Id == Guid.Empty) template.Id = Guid.NewGuid();
            var poco = FromDomain(template);

            var q = SurrealQuery
                .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t",    QuestNodeTemplateTable)
                .WithParam("_id",   poco.Id)
                .WithParam("_body", poco);

            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();

            return new AZOAResult<QuestNodeTemplate> { Result = template, Message = "Upserted." };
        }
        catch (Exception ex)
        {
            return Err<QuestNodeTemplate>(
                $"SurrealQuestStore.UpsertQuestNodeTemplateAsync({template.Id}) failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<QuestNodeTemplate>>> GetAllQuestNodeTemplatesAsync(
        CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery<QuestNodeTemplatePoco>.From();
            var rows = await _executor.QueryAsync<QuestNodeTemplatePoco>(q, ct);
            IEnumerable<QuestNodeTemplate> result = rows.Select(ToDomain).ToList();
            return new AZOAResult<IEnumerable<QuestNodeTemplate>> { Result = result, Message = "Success" };
        }
        catch (Exception ex)
        {
            return Err<IEnumerable<QuestNodeTemplate>>(
                $"SurrealQuestStore.GetAllQuestNodeTemplatesAsync failed: {ex.Message}");
        }
    }

    // ── Hydration: child rows (nodes + edges) for a quest ─────────────────────

    /// <summary>
    /// Loads the child rows for a single quest and hangs them off the supplied
    /// instance. Two SELECTs in one combined request — keeps wire round-trips
    /// to one per quest after the head fetch.
    /// </summary>
    private async Task HydrateChildrenAsync(Quest quest, CancellationToken ct)
    {
        var surrealId = SurrealId.ToSurrealId(quest.Id);

        var questLink = SurrealLink.ToLink("quest", surrealId);

        // raw: both SELECTs are fused into one wire round-trip via Combine();
        // the typed/LINQ tiers have no multi-statement combine surface — see
        // Persistence/SurrealDb/CONVENTION.md §query-surface.
        var nodesQ = SurrealQuery
            .Of("SELECT * FROM quest_node WHERE quest_id = $_qid ORDER BY execution_order ASC")
            .WithParam("_qid", questLink);

        var edgesQ = SurrealQuery
            .Of("SELECT * FROM quest_edge WHERE quest_id = $_qid")
            .WithParam("_qid", questLink);

        var combined = SurrealQuery.Combine(nodesQ, edgesQ);
        var resp = await _executor.ExecuteAsync(combined, ct);
        resp.EnsureAllOk();

        var nodeRows = resp.GetValues<QuestNodePoco>(0);
        var edgeRows = resp.GetValues<QuestEdgePoco>(1);

        quest.Nodes = nodeRows.Select(ToDomain).ToList();
        quest.Edges = edgeRows.Select(ToDomain).ToList();
        // Dependencies are intentionally dropped — see class XML doc.
        quest.Dependencies = new List<QuestDependency>();
    }

    private async Task<AZOAResult<IEnumerable<Quest>>> HydrateManyAsync(
        IReadOnlyList<QuestPoco> rows, CancellationToken ct)
    {
        var result = new List<Quest>(rows.Count);
        foreach (var row in rows)
        {
            var quest = ToDomain(row);
            await HydrateChildrenAsync(quest, ct);
            result.Add(quest);
        }
        return new AZOAResult<IEnumerable<Quest>> { Result = result, Message = "Success" };
    }

    // ── Mapping: Quest ↔ QuestPoco ────────────────────────────────────────────

    private static QuestPoco FromDomain(Quest q) => new()
    {
        Id           = SurrealId.ToSurrealId(q.Id),
        AvatarId     = SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(q.AvatarId)) ?? string.Empty,
        Name         = q.Name ?? string.Empty,
        Description  = q.Description,
        TemplateId   = q.TemplateId.HasValue   ? SurrealLink.ToLink("quest_template", SurrealId.ToSurrealId(q.TemplateId.Value))   : null,
        DappSeriesId = q.DappSeriesId.HasValue ? SurrealLink.ToLink("dapp_series", SurrealId.ToSurrealId(q.DappSeriesId.Value)) : null,
        Metadata     = MetadataToJsonObject(q.Metadata),
        Status       = q.Status.ToString(),
        Version      = q.Version,
        IsPublic     = q.IsPublic,
        OriginAvatarId = q.OriginAvatarId.HasValue ? SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(q.OriginAvatarId.Value)) : null,
        PublishedVersionHash = q.PublishedVersionHash,
        CreatedDate  = ToUtcOffset(q.CreatedDate),
    };

    private static Quest ToDomain(QuestPoco p) => new()
    {
        Id           = FromSurrealIdDirect(p.Id),
        AvatarId     = string.IsNullOrEmpty(p.AvatarId) ? Guid.Empty : FromSurrealId(SurrealLink.FromLink(p.AvatarId)!),
        Name         = p.Name ?? string.Empty,
        Description  = p.Description,
        TemplateId   = string.IsNullOrEmpty(p.TemplateId)   ? null : FromSurrealId(SurrealLink.FromLink(p.TemplateId)!),
        DappSeriesId = string.IsNullOrEmpty(p.DappSeriesId) ? null : FromSurrealId(SurrealLink.FromLink(p.DappSeriesId)!),
        Metadata     = MetadataFromJsonObject(p.Metadata),
        Status       = Enum.TryParse<QuestStatus>(p.Status, out var st) ? st : QuestStatus.Draft,
        Version      = p.Version,
        IsPublic     = p.IsPublic,
        OriginAvatarId = string.IsNullOrEmpty(p.OriginAvatarId) ? null : FromSurrealId(SurrealLink.FromLink(p.OriginAvatarId)!),
        PublishedVersionHash = string.IsNullOrEmpty(p.PublishedVersionHash) ? null : p.PublishedVersionHash,
        CreatedDate  = p.CreatedDate.UtcDateTime,
        Nodes        = new List<QuestNode>(),
        Edges        = new List<QuestEdge>(),
        Dependencies = new List<QuestDependency>(),
    };

    // ── Mapping: QuestNode ↔ QuestNodePoco ────────────────────────────────────

    private static QuestNodePoco FromDomain(QuestNode n) => new()
    {
        Id              = SurrealId.ToSurrealId(n.Id),
        QuestId         = SurrealLink.ToLink("quest", SurrealId.ToSurrealId(n.QuestId)) ?? string.Empty,
        NodeTemplateId  = n.NodeTemplateId.HasValue ? SurrealLink.ToLink("quest_node_template", SurrealId.ToSurrealId(n.NodeTemplateId.Value)) : null,
        NodeType        = n.NodeType.ToString(),
        Name            = n.Name ?? string.Empty,
        Config          = n.Config ?? "{}",
        IsEntry         = n.IsEntry,
        IsTerminal      = n.IsTerminal,
        ExecutionOrder  = n.ExecutionOrder,
    };

    private static QuestNode ToDomain(QuestNodePoco p) => new()
    {
        Id              = FromSurrealIdDirect(p.Id),
        QuestId         = string.IsNullOrEmpty(p.QuestId) ? Guid.Empty : FromSurrealId(SurrealLink.FromLink(p.QuestId)!),
        NodeTemplateId  = string.IsNullOrEmpty(p.NodeTemplateId) ? null : FromSurrealId(SurrealLink.FromLink(p.NodeTemplateId)!),
        NodeType        = ParseNodeType(p.NodeType),
        Name            = p.Name ?? string.Empty,
        Config          = string.IsNullOrEmpty(p.Config) ? "{}" : p.Config,
        IsEntry         = p.IsEntry,
        IsTerminal      = p.IsTerminal,
        ExecutionOrder  = p.ExecutionOrder,
    };

    // ── Mapping: QuestEdge ↔ QuestEdgePoco ────────────────────────────────────

    private static QuestEdgePoco FromDomain(QuestEdge e) => new()
    {
        Id            = SurrealId.ToSurrealId(e.Id),
        QuestId       = SurrealLink.ToLink("quest", SurrealId.ToSurrealId(e.QuestId)) ?? string.Empty,
        SourceNodeId  = SurrealLink.ToLink("quest_node", SurrealId.ToSurrealId(e.SourceNodeId)) ?? string.Empty,
        TargetNodeId  = SurrealLink.ToLink("quest_node", SurrealId.ToSurrealId(e.TargetNodeId)) ?? string.Empty,
        Condition     = e.Condition,
        EdgeType      = e.EdgeType.ToString(),
    };

    private static QuestEdge ToDomain(QuestEdgePoco p) => new()
    {
        Id            = FromSurrealIdDirect(p.Id),
        QuestId       = string.IsNullOrEmpty(p.QuestId)       ? Guid.Empty : FromSurrealId(SurrealLink.FromLink(p.QuestId)!),
        SourceNodeId  = string.IsNullOrEmpty(p.SourceNodeId)  ? Guid.Empty : FromSurrealId(SurrealLink.FromLink(p.SourceNodeId)!),
        TargetNodeId  = string.IsNullOrEmpty(p.TargetNodeId)  ? Guid.Empty : FromSurrealId(SurrealLink.FromLink(p.TargetNodeId)!),
        Condition     = p.Condition,
        EdgeType      = ParseEdgeType(p.EdgeType),
    };

    // ── Mapping: QuestTemplate ↔ QuestTemplatePoco ───────────────────────────

    private static QuestTemplatePoco FromDomain(QuestTemplate t) => new()
    {
        Id             = SurrealId.ToSurrealId(t.Id),
        Name           = t.Name ?? string.Empty,
        Description    = t.Description,
        AuthorAvatarId = SurrealId.ToSurrealId(t.AuthorAvatarId),
        Parameters     = t.Parameters ?? "{}",
        Version        = string.IsNullOrEmpty(t.Version) ? "1.0.0" : t.Version,
        IsPublic       = t.IsPublic,
        Tags           = t.Tags?.ToList() ?? new List<string>(),
        Nodes          = t.Nodes.Select(FromDomainTemplateNode).ToList(),
        Edges          = t.Edges.Select(FromDomainTemplateEdge).ToList(),
    };

    private static QuestTemplate ToDomain(QuestTemplatePoco p)
    {
        var template = new QuestTemplate
        {
            Id             = FromSurrealIdDirect(p.Id),
            Name           = p.Name ?? string.Empty,
            Description    = p.Description,
            AuthorAvatarId = string.IsNullOrEmpty(p.AuthorAvatarId) ? Guid.Empty : FromSurrealId(p.AuthorAvatarId),
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

    private static QuestTemplateNodePoco FromDomainTemplateNode(QuestTemplateNode n) => new()
    {
        Id              = SurrealId.ToSurrealId(n.Id),
        SlotId          = n.SlotId,
        NodeTemplateId  = SurrealId.ToSurrealId(n.NodeTemplateId),
        ParamOverrides  = n.ParamOverrides ?? "{}",
        IsEntry         = n.IsEntry,
        IsTerminal      = n.IsTerminal,
    };

    private static QuestTemplateNode ToDomainTemplateNode(QuestTemplateNodePoco n, Guid templateId) => new()
    {
        Id              = string.IsNullOrEmpty(n.Id) ? Guid.NewGuid() : FromSurrealIdDirect(n.Id),
        TemplateId      = templateId,
        SlotId          = n.SlotId ?? string.Empty,
        NodeTemplateId  = string.IsNullOrEmpty(n.NodeTemplateId) ? Guid.Empty : FromSurrealId(n.NodeTemplateId),
        ParamOverrides  = string.IsNullOrEmpty(n.ParamOverrides) ? "{}" : n.ParamOverrides,
        IsEntry         = n.IsEntry,
        IsTerminal      = n.IsTerminal,
    };

    private static QuestTemplateEdgePoco FromDomainTemplateEdge(QuestTemplateEdge e) => new()
    {
        Id            = SurrealId.ToSurrealId(e.Id),
        SourceSlotId  = e.SourceSlotId,
        TargetSlotId  = e.TargetSlotId,
        EdgeType      = e.EdgeType.ToString(),
    };

    private static QuestTemplateEdge ToDomainTemplateEdge(QuestTemplateEdgePoco e, Guid templateId) => new()
    {
        Id            = string.IsNullOrEmpty(e.Id) ? Guid.NewGuid() : FromSurrealIdDirect(e.Id),
        TemplateId    = templateId,
        SourceSlotId  = e.SourceSlotId ?? string.Empty,
        TargetSlotId  = e.TargetSlotId ?? string.Empty,
        EdgeType      = ParseEdgeType(e.EdgeType),
    };

    // ── Mapping: QuestNodeTemplate ↔ QuestNodeTemplatePoco ───────────────────

    private static QuestNodeTemplatePoco FromDomain(QuestNodeTemplate t) => new()
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
        AuthorAvatarId  = SurrealId.ToSurrealId(t.AuthorAvatarId),
        IsPublic        = t.IsPublic,
        Tags            = t.Tags?.ToList() ?? new List<string>(),
    };

    private static QuestNodeTemplate ToDomain(QuestNodeTemplatePoco p) => new()
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
        AuthorAvatarId  = string.IsNullOrEmpty(p.AuthorAvatarId) ? Guid.Empty : FromSurrealId(p.AuthorAvatarId),
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

    private static QuestNodeType ParseNodeType(string? raw) =>
        Enum.TryParse<QuestNodeType>(raw, ignoreCase: false, out var v)
            ? v
            : throw new InvalidOperationException(
                $"Unrecognised QuestNodeType '{raw}' read from SurrealDB. " +
                "Schema ASSERT INSIDE [...] should have prevented this; refresh the schema.");

    private static QuestEdgeType ParseEdgeType(string? raw) =>
        Enum.TryParse<QuestEdgeType>(raw, ignoreCase: false, out var v)
            ? v
            : QuestEdgeType.Control;

    // ── AZOAResult helpers ───────────────────────────────────────────────────

    private static AZOAResult<T> Ok<T>(T value, string msg = "Success") =>
        new() { Result = value, Message = msg };

    private static AZOAResult<T> Missing<T>(string msg) =>
        new() { IsError = true, Message = msg, Result = default };

    private static AZOAResult<T> Err<T>(string msg) =>
        new() { IsError = true, Message = msg, Result = default };

    // ── POCOs (private — replace with generated POCO when source-gen catches up) ──

    private sealed class QuestPoco : ISurrealRecord
    {
        public string SchemaName => QuestTable;

        [JsonPropertyName("id")]              public string Id { get; set; } = string.Empty;
        [JsonPropertyName("avatar_id")]       public string AvatarId { get; set; } = string.Empty;
        [JsonPropertyName("name")]            public string? Name { get; set; }
        [JsonPropertyName("description")]     public string? Description { get; set; }
        [JsonPropertyName("template_id")]     public string? TemplateId { get; set; }
        [JsonPropertyName("dapp_series_id")]  public string? DappSeriesId { get; set; }
        [JsonPropertyName("metadata")]        public JsonElement Metadata { get; set; }
        [JsonPropertyName("status")]          public string? Status { get; set; }
        [JsonPropertyName("version")]         public long Version { get; set; }
        [JsonPropertyName("is_public")]       public bool IsPublic { get; set; }
        [JsonPropertyName("origin_avatar_id")] public string? OriginAvatarId { get; set; }
        [JsonPropertyName("published_version_hash")] public string? PublishedVersionHash { get; set; }
        [JsonPropertyName("created_date")]    public DateTimeOffset CreatedDate { get; set; }
    }

    private sealed class QuestNodePoco : ISurrealRecord
    {
        public string SchemaName => QuestNodeTable;

        [JsonPropertyName("id")]                public string Id { get; set; } = string.Empty;
        [JsonPropertyName("quest_id")]          public string QuestId { get; set; } = string.Empty;
        [JsonPropertyName("node_template_id")]  public string? NodeTemplateId { get; set; }
        [JsonPropertyName("node_type")]         public string? NodeType { get; set; }
        [JsonPropertyName("name")]              public string? Name { get; set; }
        [JsonPropertyName("config")]            public string? Config { get; set; }
        [JsonPropertyName("is_entry")]          public bool IsEntry { get; set; }
        [JsonPropertyName("is_terminal")]       public bool IsTerminal { get; set; }
        [JsonPropertyName("execution_order")]   public int ExecutionOrder { get; set; }
    }

    private sealed class QuestEdgePoco : ISurrealRecord
    {
        public string SchemaName => QuestEdgeTable;

        [JsonPropertyName("id")]                public string Id { get; set; } = string.Empty;
        [JsonPropertyName("quest_id")]          public string QuestId { get; set; } = string.Empty;
        [JsonPropertyName("source_node_id")]    public string SourceNodeId { get; set; } = string.Empty;
        [JsonPropertyName("target_node_id")]    public string TargetNodeId { get; set; } = string.Empty;
        [JsonPropertyName("condition")]         public string? Condition { get; set; }
        [JsonPropertyName("edge_type")]         public string? EdgeType { get; set; }
    }

    private sealed class QuestTemplatePoco : ISurrealRecord
    {
        public string SchemaName => QuestTemplateTable;

        [JsonPropertyName("id")]               public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")]             public string? Name { get; set; }
        [JsonPropertyName("description")]      public string? Description { get; set; }
        [JsonPropertyName("author_avatar_id")] public string AuthorAvatarId { get; set; } = string.Empty;
        [JsonPropertyName("parameters")]       public string? Parameters { get; set; }
        [JsonPropertyName("version")]          public string? Version { get; set; }
        [JsonPropertyName("is_public")]        public bool IsPublic { get; set; }
        [JsonPropertyName("nodes")]            public List<QuestTemplateNodePoco>? Nodes { get; set; }
        [JsonPropertyName("edges")]            public List<QuestTemplateEdgePoco>? Edges { get; set; }
        [JsonPropertyName("tags")]             public List<string>? Tags { get; set; }
    }

    private sealed class QuestTemplateNodePoco : ISurrealRecord
    {
        public string SchemaName => QuestNodeTemplateTable;

        [JsonPropertyName("id")]               public string Id { get; set; } = string.Empty;
        [JsonPropertyName("slot_id")]          public string? SlotId { get; set; }
        [JsonPropertyName("node_template_id")] public string NodeTemplateId { get; set; } = string.Empty;
        [JsonPropertyName("param_overrides")]  public string? ParamOverrides { get; set; }
        [JsonPropertyName("is_entry")]         public bool IsEntry { get; set; }
        [JsonPropertyName("is_terminal")]      public bool IsTerminal { get; set; }
    }

    private sealed class QuestTemplateEdgePoco : ISurrealRecord
    {
        public string SchemaName => QuestTemplateTable;

        [JsonPropertyName("id")]               public string Id { get; set; } = string.Empty;
        [JsonPropertyName("source_slot_id")]   public string? SourceSlotId { get; set; }
        [JsonPropertyName("target_slot_id")]   public string? TargetSlotId { get; set; }
        [JsonPropertyName("edge_type")]        public string? EdgeType { get; set; }
    }

    private sealed class QuestNodeTemplatePoco : ISurrealRecord
    {
        public string SchemaName => QuestNodeTemplateTable;

        [JsonPropertyName("id")]               public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")]             public string? Name { get; set; }
        [JsonPropertyName("node_type")]        public string? NodeType { get; set; }
        [JsonPropertyName("description")]      public string? Description { get; set; }
        [JsonPropertyName("default_config")]   public string? DefaultConfig { get; set; }
        [JsonPropertyName("config_schema")]    public string? ConfigSchema { get; set; }
        [JsonPropertyName("input_schema")]     public string? InputSchema { get; set; }
        [JsonPropertyName("output_schema")]    public string? OutputSchema { get; set; }
        [JsonPropertyName("version")]          public string? Version { get; set; }
        [JsonPropertyName("author_avatar_id")] public string AuthorAvatarId { get; set; } = string.Empty;
        [JsonPropertyName("is_public")]        public bool IsPublic { get; set; }
        [JsonPropertyName("tags")]             public List<string>? Tags { get; set; }
    }

    private sealed class QuestIdProjection : ISurrealRecord
    {
        public string SchemaName => QuestTable;

        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    }
}
