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
/// SurrealDB-backed <see cref="IActionRunStore"/>. One row per execution
/// attempt of a <see cref="ActionDefinition"/>. Pattern mirrors
/// <see cref="SurrealSagaStore"/> — Guid('N') lowercase-hex record ids,
/// inline POCO (replace with generated POCO when source-gen catches up —
/// <c>AZOA.WebAPI.Persistence.SurrealDb.Models.ActionRun</c> already exists), every
/// value parameter-bound (G3 / SRDB0001).
///
/// <para>
/// <b>Fork write contract</b> (per <c>SURREAL-SCHEMA-HINTS.md</c> §6.1):
/// <see cref="CreateAsync"/> inspects <see cref="ActionRun.ParentRunId"/>; when
/// non-null it CREATEs the child row AND <c>RELATE child -&gt; forked_from
/// -&gt; parent</c> inside a single <c>BEGIN / COMMIT</c> block (composed via
/// <see cref="SurrealQuery.Combine"/>, since <see cref="SurrealQuery.Of"/>
/// rejects multi-statement bodies) so either both writes land or neither
/// does. Root runs (no parent) take the single-CREATE path. The scalar <see cref="ActionRun.ParentRunId"/> is the authoritative
/// pointer; the RELATE edge mirrors it for native graph traversal — both must
/// be kept in sync at write time.
/// </para>
///
/// <para>
/// <b>Lineage</b> (<see cref="GetLineageAsync"/>): walks the scalar
/// <see cref="ActionRun.ParentRunId"/> chain client-side rather than dispatching
/// a native <c>-&gt;forked_from-&gt;</c> graph query. Chosen because the
/// scalar walk is simpler in <see cref="SurrealQuery"/> (each hop is a
/// straightforward <c>SELECT * FROM type::record(...)</c>; the typed builder
/// does not yet have a fluent graph-traversal helper), and the
/// <c>parent_run_id</c> scalar is the authoritative pointer anyway. Returns
/// the chain in <b>child-to-root</b> order with a structural visited-set
/// guard so a malformed cyclic parent chain cannot spin forever. The RELATE
/// edge is still maintained on write so a future round can swap this for a
/// single-statement graph query without a data backfill.
/// </para>
/// </summary>
public sealed class SurrealActionRunStore : IActionRunStore
{
    private const string RunTable = "action_run";

    private readonly ISurrealExecutor _executor;

    public SurrealActionRunStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<AZOAResult<ActionRun>> CreateAsync(ActionRun run, CancellationToken ct = default)
    {
        if (run is null)
            return Err<ActionRun>("CreateAsync: run must not be null.");
        if (run.Id == Guid.Empty)
            return Err<ActionRun>("CreateAsync: run.Id must not be empty.");

        try
        {
            var poco        = FromDomain(run);
            var childSurrId = poco.Id;

            if (run.ParentRunId is null)
            {
                // Root run path — single CREATE.
                var q = SurrealQuery
                    .Of("CREATE type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                    .WithParam("_t",    RunTable)
                    .WithParam("_id",   childSurrId)
                    .WithParam("_body", poco);

                var resp = await _executor.ExecuteAsync(q, ct);
                resp.EnsureAllOk();
                return Ok(run, "Created.");
            }

            // Fork path — CREATE action_run + RELATE forked_from atomically.
            // SurrealQuery.Of rejects a ';'-joined body (single-statement only),
            // so compose the BEGIN / CREATE / RELATE / COMMIT sequence via
            // SurrealQuery.Combine (mirrors the SurrealActionStore delete-path
            // fix). SurrealDB executes a BEGIN; …; COMMIT; sequence in one
            // request atomically — an error in either inner statement rolls
            // back both, satisfying the §6.1 fork write contract. The params
            // merge across the combined statements.
            var parentSurrId = SurrealId.ToSurrealId(run.ParentRunId.Value);

            // RELATE cannot parse an inline type::record(...) on either side of
            // the ->edge-> arrow (SurrealDB rejects it: "Unexpected token `::`").
            // Bind the two endpoints to LET variables first, then RELATE the
            // variables. CREATE accepts the inline type::record() form fine.
            var createChild = SurrealQuery
                .Of("CREATE type::record($_t, $_cid) CONTENT $_body RETURN AFTER")
                .WithParam("_t",    RunTable)
                .WithParam("_cid",  childSurrId)
                .WithParam("_body", poco);
            var letChild = SurrealQuery
                .Of("LET $_child = type::record($_t, $_cid)")
                .WithParam("_t",   RunTable)
                .WithParam("_cid", childSurrId);
            var letParent = SurrealQuery
                .Of("LET $_parent = type::record($_t, $_pid)")
                .WithParam("_t",   RunTable)
                .WithParam("_pid", parentSurrId);
            var relateEdge = SurrealQuery
                .Of("RELATE $_child->forked_from->$_parent");

            var atomic = SurrealQuery.Combine(
                SurrealQuery.Of("BEGIN"),
                createChild,
                letChild,
                letParent,
                relateEdge,
                SurrealQuery.Of("COMMIT"));

            var atomicResp = await _executor.ExecuteAsync(atomic, ct);
            atomicResp.EnsureAllOk();

            return Ok(run, "Created (fork).");
        }
        catch (Exception ex)
        {
            return Err<ActionRun>($"SurrealActionRunStore.CreateAsync({run.Id}) failed: {ex.Message}");
        }
    }

    // ── GetById ───────────────────────────────────────────────────────────────

    public async Task<AZOAResult<ActionRun>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM type::record($_t, $_id)")
                .WithParam("_t",  RunTable)
                .WithParam("_id", SurrealId.ToSurrealId(id));

            var rows = await _executor.QueryAsync<ActionRunPoco>(q, ct);
            return rows.Count == 0
                ? Missing<ActionRun>($"ActionRun {id} not found.")
                : Ok(ToDomain(rows[0]));
        }
        catch (Exception ex)
        {
            return Err<ActionRun>($"SurrealActionRunStore.GetByIdAsync({id}) failed: {ex.Message}");
        }
    }

    // ── Update ────────────────────────────────────────────────────────────────

    public async Task<AZOAResult<ActionRun>> UpdateAsync(
        ActionRun run, ActionRunStatus? expectedStatus = null, CancellationToken ct = default)
    {
        if (run is null)
            return Err<ActionRun>("UpdateAsync: run must not be null.");
        if (run.Id == Guid.Empty)
            return Err<ActionRun>("UpdateAsync: run.Id must not be empty.");

        try
        {
            var surrealId = SurrealId.ToSurrealId(run.Id);

            // Pre-check existence so we can surface "not found" the same way
            // the in-memory store does (rather than silently CREATE on
            // UPDATE-of-missing-id, which is the SurrealDB default).
            var existsQ = SurrealQuery
                .Of("SELECT id FROM type::record($_t, $_id)")
                .WithParam("_t",  RunTable)
                .WithParam("_id", surrealId);

            var existing = await _executor.QueryAsync<ActionRunIdProjection>(existsQ, ct);
            if (existing.Count == 0)
                return Missing<ActionRun>($"ActionRun {run.Id} not found.");

            var poco = FromDomain(run);

            if (expectedStatus is { } expected)
            {
                // G2 single-winner conditional UPDATE: apply only if the
                // persisted status is still `expected`. A concurrent projector
                // that already moved the run loses (zero rows affected).
                var condQ = SurrealQuery
                    .Of("UPDATE type::record($_t, $_id) CONTENT $_body WHERE status = $_expected RETURN AFTER")
                    .WithParam("_t",        RunTable)
                    .WithParam("_id",       surrealId)
                    .WithParam("_body",     poco)
                    .WithParam("_expected", expected.ToString());

                var condResp = await _executor.ExecuteAsync(condQ, ct);
                if (condResp.Count == 0 || !condResp[0].IsOk || condResp[0].AffectedCount() != 1)
                    return Err<ActionRun>(
                        $"ActionRun {run.Id} conditional update lost: status is no longer {expected}.");
                return Ok(run, "Updated.");
            }

            var q = SurrealQuery
                .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t",    RunTable)
                .WithParam("_id",   surrealId)
                .WithParam("_body", poco);

            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();
            return Ok(run, "Updated.");
        }
        catch (Exception ex)
        {
            return Err<ActionRun>($"SurrealActionRunStore.UpdateAsync({run.Id}) failed: {ex.Message}");
        }
    }

    // ── List queries ──────────────────────────────────────────────────────────

    public async Task<AZOAResult<IEnumerable<ActionRun>>> GetByActionIdAsync(
        Guid actionId, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM action_run WHERE action_id = $_qid")
                .WithParam("_qid", SurrealRecordParam.Of("action", SurrealId.ToSurrealId(actionId)));

            var rows = await _executor.QueryAsync<ActionRunPoco>(q, ct);
            return OkMany(rows.Select(ToDomain).ToList());
        }
        catch (Exception ex)
        {
            return Err<IEnumerable<ActionRun>>(
                $"SurrealActionRunStore.GetByActionIdAsync({actionId}) failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<ActionRun>>> GetByUserIdAsync(
        Guid userId, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM action_run WHERE user_id = $_aid")
                .WithParam("_aid", SurrealRecordParam.Of("user", SurrealId.ToSurrealId(userId)));

            var rows = await _executor.QueryAsync<ActionRunPoco>(q, ct);
            return OkMany(rows.Select(ToDomain).ToList());
        }
        catch (Exception ex)
        {
            return Err<IEnumerable<ActionRun>>(
                $"SurrealActionRunStore.GetByUserIdAsync({userId}) failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<ActionRun>>> GetByStatusAsync(
        ActionRunStatus status, CancellationToken ct = default)
    {
        try
        {
            // PILOT (surreal-linq-graph-query Phase 1): the typed builder
            // proving the dead ExpressionTranslator path executes against a
            // real store. Emits `SELECT * FROM action_run WHERE status = $status`
            // — semantically identical to the prior hand-written raw string.
            // Status persists PascalCase (FromDomain does `r.Status.ToString()`;
            // ParseStatus reads it back case-sensitively), and ActionRunPoco.Status
            // is a `string?`, so this is a plain string comparison — the literal
            // binds as `"Running"` with no enum lowercasing.
            var statusText = status.ToString();
            var q = SurrealQuery<ActionRunPoco>
                .From()
                .Where(r => r.Status == statusText);

            var rows = await _executor.QueryAsync<ActionRunPoco>(q, ct);
            return OkMany(rows.Select(ToDomain).ToList());
        }
        catch (Exception ex)
        {
            return Err<IEnumerable<ActionRun>>(
                $"SurrealActionRunStore.GetByStatusAsync({status}) failed: {ex.Message}");
        }
    }

    // ── Lineage ───────────────────────────────────────────────────────────────

    public async Task<AZOAResult<IEnumerable<ActionRun>>> GetLineageAsync(
        Guid runId, CancellationToken ct = default)
    {
        try
        {
            // GRAPH READ (surreal-linq-graph-query Phase 4 marquee): walk the
            // `forked_from` RELATE edges via the typed graph-traversal surface
            // instead of the `parent_run_id` SCALAR. The fork write relates
            // `child->forked_from->parent`, so each ancestor hop is one typed
            // outgoing traversal: `SELECT VALUE ->forked_from->action_run FROM <run>`.
            // We still iterate per level (one hop per query) because unbounded
            // single-statement recursive collection is gated on the SurrealDB
            // version pin (plan D10 / surrealdb-major-upgrade) — but every hop
            // is now a real graph read, not a scalar join. Child-to-root order
            // + the defensive walk-til-missing semantics are preserved.
            var chain = new List<ActionRun>();
            var visited = new HashSet<Guid>();

            // Seed: fetch the starting run directly (need its data + the
            // bogus-id check). Each subsequent ancestor is reached by ONE typed
            // outgoing `->forked_from->action_run` hop anchored on the PREVIOUS
            // node — so we read the parent's full row via the graph edge, never
            // the parent_run_id scalar.
            var seed = await _executor.QueryAsync<ActionRunPoco>(
                SurrealQuery<ActionRunPoco>.Key(SurrealId.ToSurrealId(runId)), ct);
            if (seed.Count == 0)
                return Missing<IEnumerable<ActionRun>>($"ActionRun {runId} not found.");

            var current = ToDomain(seed[0]);
            while (true)
            {
                if (!visited.Add(current.Id)) break; // cycle guard
                chain.Add(current);

                // One graph hop to the parent. The edge mirrors parent_run_id;
                // if the scalar is null there is no parent edge to follow.
                if (current.ParentRunId is null) break;

                var parentRows = await _executor.QueryAsync<ActionRunPoco>(
                    SurrealQuery<ActionRunPoco>
                        .Key(SurrealId.ToSurrealId(current.Id))
                        .Traverse<ActionRunPoco>(r => r.Out<ForkedFromEdge>().To<ActionRunPoco>()),
                    ct);

                // Dangling edge mid-chain → stop and return what we have
                // (matches the in-memory store's walk-til-null semantic).
                if (parentRows.Count == 0) break;
                current = ToDomain(parentRows[0]);
            }

            return OkMany((IEnumerable<ActionRun>)chain);
        }
        catch (Exception ex)
        {
            return Err<IEnumerable<ActionRun>>(
                $"SurrealActionRunStore.GetLineageAsync({runId}) failed: {ex.Message}");
        }
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    private static ActionRunPoco FromDomain(ActionRun r) => new()
    {
        Id              = SurrealId.ToSurrealId(r.Id),
        ActionId         = SurrealLink.ToLink("action", SurrealId.ToSurrealId(r.ActionId))!,
        UserId        = SurrealLink.ToLink("user", SurrealId.ToSurrealId(r.UserId))!,
        // tenant-consent-delegation AC4: persist the acting tenant as a nullable
        // record<user> link so the seam's live consent check survives the async
        // saga hop. Mirrors SurrealBlockchainOperationStore's acting_tenant_id.
        ActingTenantId  = r.ActingTenantId.HasValue
                          ? SurrealLink.ToLink("user", SurrealId.ToSurrealId(r.ActingTenantId.Value))
                          : null,
        Status          = r.Status.ToString(),
        StartedAt       = ToUtcOffset(r.StartedAt),
        EndedAt         = r.EndedAt.HasValue ? ToUtcOffset(r.EndedAt.Value) : null,
        ParentRunId     = r.ParentRunId.HasValue     ? SurrealLink.ToLink("action_run", SurrealId.ToSurrealId(r.ParentRunId.Value))     : null,
        ForkedAtNodeId  = r.ForkedAtNodeId.HasValue  ? SurrealLink.ToLink("action_node", SurrealId.ToSurrealId(r.ForkedAtNodeId.Value))  : null,
        ForkReason      = r.ForkReason,
        FailReason      = r.FailReason,
        SourceActionId   = r.SourceActionId.HasValue   ? SurrealLink.ToLink("action", SurrealId.ToSurrealId(r.SourceActionId.Value))       : null,
        OriginUserId  = r.OriginUserId.HasValue  ? SurrealLink.ToLink("user", SurrealId.ToSurrealId(r.OriginUserId.Value))     : null,
        PublishedVersionHash = r.PublishedVersionHash,
    };

    private static ActionRun ToDomain(ActionRunPoco p) => new()
    {
        Id              = SurrealId.FromSurrealId(p.Id),
        ActionId         = string.IsNullOrEmpty(p.ActionId)  ? Guid.Empty : FromSurrealIdFk(SurrealLink.FromLink(p.ActionId)!),
        UserId        = string.IsNullOrEmpty(p.UserId) ? Guid.Empty : FromSurrealIdFk(SurrealLink.FromLink(p.UserId)!),
        // tenant-consent-delegation AC4: read the acting tenant back from the
        // nullable record<user> link.
        ActingTenantId  = string.IsNullOrEmpty(p.ActingTenantId) ? null : FromSurrealIdFk(SurrealLink.FromLink(p.ActingTenantId)!),
        Status          = ParseStatus(p.Status),
        StartedAt       = p.StartedAt.UtcDateTime,
        EndedAt         = p.EndedAt?.UtcDateTime,
        ParentRunId     = string.IsNullOrEmpty(p.ParentRunId)    ? null : FromSurrealIdFk(SurrealLink.FromLink(p.ParentRunId)!),
        ForkedAtNodeId  = string.IsNullOrEmpty(p.ForkedAtNodeId) ? null : FromSurrealIdFk(SurrealLink.FromLink(p.ForkedAtNodeId)!),
        ForkReason      = p.ForkReason,
        FailReason      = p.FailReason,
        SourceActionId   = string.IsNullOrEmpty(p.SourceActionId)  ? null : FromSurrealIdFk(SurrealLink.FromLink(p.SourceActionId)!),
        OriginUserId  = string.IsNullOrEmpty(p.OriginUserId) ? null : FromSurrealIdFk(SurrealLink.FromLink(p.OriginUserId)!),
        PublishedVersionHash = string.IsNullOrEmpty(p.PublishedVersionHash) ? null : p.PublishedVersionHash,
    };

    // ── Helpers ───────────────────────────────────────────────────────────────



    private static Guid FromSurrealIdFk(string id)
    {
        var stripped = StripIdPrefix(id);
        return Guid.TryParseExact(stripped, "N", out var g) ? g : Guid.Empty;
    }

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

    private static ActionRunStatus ParseStatus(string? raw) =>
        Enum.TryParse<ActionRunStatus>(raw, ignoreCase: false, out var v)
            ? v
            : throw new InvalidOperationException(
                $"Unrecognised ActionRunStatus '{raw}' read from SurrealDB. " +
                "Schema ASSERT INSIDE [...] should have prevented this; refresh the schema.");

    private static AZOAResult<T> Ok<T>(T value, string msg = "Success") =>
        new() { Result = value, Message = msg };

    private static AZOAResult<IEnumerable<T>> OkMany<T>(IEnumerable<T> values, string msg = "Success") =>
        new() { Result = values, Message = msg };

    private static AZOAResult<T> Missing<T>(string msg) =>
        new() { IsError = true, Message = msg, Result = default };

    private static AZOAResult<T> Err<T>(string msg) =>
        new() { IsError = true, Message = msg, Result = default };

    // ── POCO (private — replace with generated POCO when source-gen catches up) ──

    private sealed class ActionRunPoco : SurrealForge.Client.ISurrealRecord
    {
        public string SchemaName => RunTable;

        [JsonPropertyName("id")]                public string Id { get; set; } = string.Empty;
        [References(typeof(AZOA.WebAPI.Persistence.SurrealDb.Models.ActionDefinition))] [JsonPropertyName("action_id")]          public string ActionId { get; set; } = string.Empty;
        [References(typeof(AZOA.WebAPI.Persistence.SurrealDb.Models.User))] [JsonPropertyName("user_id")]         public string UserId { get; set; } = string.Empty;
        [References(typeof(AZOA.WebAPI.Persistence.SurrealDb.Models.User))] [JsonPropertyName("acting_tenant_id")]  public string? ActingTenantId { get; set; }
        [JsonPropertyName("status")]            public string? Status { get; set; }
        [JsonPropertyName("started_at")]        public DateTimeOffset StartedAt { get; set; }
        [JsonPropertyName("ended_at")]          public DateTimeOffset? EndedAt { get; set; }
        [References(typeof(AZOA.WebAPI.Persistence.SurrealDb.Models.ActionRun))] [JsonPropertyName("parent_run_id")]     public string? ParentRunId { get; set; }
        [References(typeof(AZOA.WebAPI.Persistence.SurrealDb.Models.ActionNode))] [JsonPropertyName("forked_at_node_id")] public string? ForkedAtNodeId { get; set; }
        [JsonPropertyName("fork_reason")]       public string? ForkReason { get; set; }
        [JsonPropertyName("fail_reason")]       public string? FailReason { get; set; }
        [References(typeof(AZOA.WebAPI.Persistence.SurrealDb.Models.ActionDefinition))] [JsonPropertyName("source_action_id")]   public string? SourceActionId { get; set; }
        [References(typeof(AZOA.WebAPI.Persistence.SurrealDb.Models.User))] [JsonPropertyName("origin_user_id")]  public string? OriginUserId { get; set; }
        [JsonPropertyName("published_version_hash")] public string? PublishedVersionHash { get; set; }
    }

    private sealed class ActionRunIdProjection
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    }

    // Edge POCO for the typed `->forked_from->` graph traversal in
    // GetLineageAsync. Only its schema name is consumed by the traversal emit
    // (the edge body isn't deserialized here), but it must satisfy
    // ISurrealRecord,new() so the graph operators can resolve "forked_from".
    private sealed class ForkedFromEdge : SurrealForge.Client.ISurrealRecord
    {
        public string SchemaName => "forked_from";
        [JsonPropertyName("id")]  public string Id  { get; set; } = string.Empty;
        [JsonPropertyName("in")]  public string In  { get; set; } = string.Empty;
        [JsonPropertyName("out")] public string Out { get; set; } = string.Empty;
    }
}
