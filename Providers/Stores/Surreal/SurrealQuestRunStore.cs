using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client;
using Oasis.SurrealDb.Client.Query;
using OASIS.WebAPI.Interfaces.Stores;
using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IQuestRunStore"/>. One row per execution
/// attempt of a <see cref="Quest"/>. Pattern mirrors
/// <see cref="SurrealSagaStore"/> — Guid('N') lowercase-hex record ids,
/// inline POCO (replace with generated POCO when source-gen catches up —
/// <c>OASIS.WebAPI.Persistence.SurrealDb.Models.QuestRun</c> already exists), every
/// value parameter-bound (G3 / SRDB0001).
///
/// <para>
/// <b>Fork write contract</b> (per <c>SURREAL-SCHEMA-HINTS.md</c> §6.1):
/// <see cref="CreateAsync"/> inspects <see cref="QuestRun.ParentRunId"/>; when
/// non-null it CREATEs the child row AND <c>RELATE child -&gt; forked_from
/// -&gt; parent</c> inside a single <c>BEGIN / COMMIT</c> block so either both
/// writes land or neither does. Root runs (no parent) take the single-CREATE
/// path. The scalar <see cref="QuestRun.ParentRunId"/> is the authoritative
/// pointer; the RELATE edge mirrors it for native graph traversal — both must
/// be kept in sync at write time.
/// </para>
///
/// <para>
/// <b>Lineage</b> (<see cref="GetLineageAsync"/>): walks the scalar
/// <see cref="QuestRun.ParentRunId"/> chain client-side rather than dispatching
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
public sealed class SurrealQuestRunStore : IQuestRunStore
{
    private const string RunTable = "quest_run";

    private readonly ISurrealExecutor _executor;

    public SurrealQuestRunStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<OASISResult<QuestRun>> CreateAsync(QuestRun run, CancellationToken ct = default)
    {
        if (run is null)
            return Err<QuestRun>("CreateAsync: run must not be null.");
        if (run.Id == Guid.Empty)
            return Err<QuestRun>("CreateAsync: run.Id must not be empty.");

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

            // Fork path — CREATE quest_run + RELATE forked_from atomically.
            // BUG (flagged 2026-06-18, live smoke test): the BEGIN;...;COMMIT
            // body below is REJECTED by SurrealQuery.Of, which throws on any ';'
            // in the body (single-statement-only; see SurrealQuery.cs:110). This
            // fork path therefore throws the first time a run is forked. The
            // correct fix is the connection's transaction API
            // (HttpSurrealConnection.BeginTransactionAsync → ISurrealTransaction),
            // NOT an inline BEGIN/COMMIT string and NOT SurrealQuery.Combine
            // (which composes single statements, not a transaction). Tracked as a
            // follow-up; not on the create-fresh-run path. Sibling delete-path
            // multi-statement .Of bugs in SurrealQuestStore were fixed the same day.
            var parentSurrId = ToSurrealId(run.ParentRunId.Value);

            var atomic = SurrealQuery
                .Of("BEGIN; CREATE type::record($_t, $_cid) CONTENT $_body RETURN AFTER; RELATE type::record($_t, $_cid)->forked_from->type::record($_t, $_pid); COMMIT")
                .WithParam("_t",    RunTable)
                .WithParam("_cid",  childSurrId)
                .WithParam("_pid",  parentSurrId)
                .WithParam("_body", poco);

            var atomicResp = await _executor.ExecuteAsync(atomic, ct);
            atomicResp.EnsureAllOk();

            return Ok(run, "Created (fork).");
        }
        catch (Exception ex)
        {
            return Err<QuestRun>($"SurrealQuestRunStore.CreateAsync({run.Id}) failed: {ex.Message}");
        }
    }

    // ── GetById ───────────────────────────────────────────────────────────────

    public async Task<OASISResult<QuestRun>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM type::record($_t, $_id)")
                .WithParam("_t",  RunTable)
                .WithParam("_id", ToSurrealId(id));

            var rows = await _executor.QueryAsync<QuestRunPoco>(q, ct);
            return rows.Count == 0
                ? Missing<QuestRun>($"QuestRun {id} not found.")
                : Ok(ToDomain(rows[0]));
        }
        catch (Exception ex)
        {
            return Err<QuestRun>($"SurrealQuestRunStore.GetByIdAsync({id}) failed: {ex.Message}");
        }
    }

    // ── Update ────────────────────────────────────────────────────────────────

    public async Task<OASISResult<QuestRun>> UpdateAsync(
        QuestRun run, QuestRunStatus? expectedStatus = null, CancellationToken ct = default)
    {
        if (run is null)
            return Err<QuestRun>("UpdateAsync: run must not be null.");
        if (run.Id == Guid.Empty)
            return Err<QuestRun>("UpdateAsync: run.Id must not be empty.");

        try
        {
            var surrealId = ToSurrealId(run.Id);

            // Pre-check existence so we can surface "not found" the same way
            // the in-memory store does (rather than silently CREATE on
            // UPDATE-of-missing-id, which is the SurrealDB default).
            var existsQ = SurrealQuery
                .Of("SELECT id FROM type::record($_t, $_id)")
                .WithParam("_t",  RunTable)
                .WithParam("_id", surrealId);

            var existing = await _executor.QueryAsync<QuestRunIdProjection>(existsQ, ct);
            if (existing.Count == 0)
                return Missing<QuestRun>($"QuestRun {run.Id} not found.");

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
                    return Err<QuestRun>(
                        $"QuestRun {run.Id} conditional update lost: status is no longer {expected}.");
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
            return Err<QuestRun>($"SurrealQuestRunStore.UpdateAsync({run.Id}) failed: {ex.Message}");
        }
    }

    // ── List queries ──────────────────────────────────────────────────────────

    public async Task<OASISResult<IEnumerable<QuestRun>>> GetByQuestIdAsync(
        Guid questId, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM quest_run WHERE quest_id = $_qid")
                .WithParam("_qid", SurrealLink.ToLink("quest", ToSurrealId(questId)));

            var rows = await _executor.QueryAsync<QuestRunPoco>(q, ct);
            return OkMany(rows.Select(ToDomain).ToList());
        }
        catch (Exception ex)
        {
            return Err<IEnumerable<QuestRun>>(
                $"SurrealQuestRunStore.GetByQuestIdAsync({questId}) failed: {ex.Message}");
        }
    }

    public async Task<OASISResult<IEnumerable<QuestRun>>> GetByAvatarIdAsync(
        Guid avatarId, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM quest_run WHERE avatar_id = $_aid")
                .WithParam("_aid", SurrealLink.ToLink("avatar", ToSurrealId(avatarId)));

            var rows = await _executor.QueryAsync<QuestRunPoco>(q, ct);
            return OkMany(rows.Select(ToDomain).ToList());
        }
        catch (Exception ex)
        {
            return Err<IEnumerable<QuestRun>>(
                $"SurrealQuestRunStore.GetByAvatarIdAsync({avatarId}) failed: {ex.Message}");
        }
    }

    public async Task<OASISResult<IEnumerable<QuestRun>>> GetByStatusAsync(
        QuestRunStatus status, CancellationToken ct = default)
    {
        try
        {
            // PILOT (surreal-linq-graph-query Phase 1): the typed builder
            // proving the dead ExpressionTranslator path executes against a
            // real store. Emits `SELECT * FROM quest_run WHERE status = $status`
            // — semantically identical to the prior hand-written raw string.
            // Status persists PascalCase (FromDomain does `r.Status.ToString()`;
            // ParseStatus reads it back case-sensitively), and QuestRunPoco.Status
            // is a `string?`, so this is a plain string comparison — the literal
            // binds as `"Running"` with no enum lowercasing.
            var statusText = status.ToString();
            var q = SurrealQuery<QuestRunPoco>
                .From()
                .Where(r => r.Status == statusText);

            var rows = await _executor.QueryAsync<QuestRunPoco>(q, ct);
            return OkMany(rows.Select(ToDomain).ToList());
        }
        catch (Exception ex)
        {
            return Err<IEnumerable<QuestRun>>(
                $"SurrealQuestRunStore.GetByStatusAsync({status}) failed: {ex.Message}");
        }
    }

    // ── Lineage ───────────────────────────────────────────────────────────────

    public async Task<OASISResult<IEnumerable<QuestRun>>> GetLineageAsync(
        Guid runId, CancellationToken ct = default)
    {
        try
        {
            var chain = new List<QuestRun>();
            var visited = new HashSet<Guid>();
            var cursor = runId;

            while (visited.Add(cursor))
            {
                var q = SurrealQuery
                    .Of("SELECT * FROM type::record($_t, $_id)")
                    .WithParam("_t",  RunTable)
                    .WithParam("_id", ToSurrealId(cursor));

                var rows = await _executor.QueryAsync<QuestRunPoco>(q, ct);
                if (rows.Count == 0)
                {
                    // First hop missing → run id is bogus.
                    if (chain.Count == 0)
                        return Missing<IEnumerable<QuestRun>>($"QuestRun {runId} not found.");

                    // Mid-chain missing → parent pointer is dangling; stop and
                    // return what we have rather than throwing (matches the
                    // in-memory store's defensive walk-til-null semantic).
                    break;
                }

                var run = ToDomain(rows[0]);
                chain.Add(run);
                if (run.ParentRunId is null) break;
                cursor = run.ParentRunId.Value;
            }

            return OkMany((IEnumerable<QuestRun>)chain);
        }
        catch (Exception ex)
        {
            return Err<IEnumerable<QuestRun>>(
                $"SurrealQuestRunStore.GetLineageAsync({runId}) failed: {ex.Message}");
        }
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    private static QuestRunPoco FromDomain(QuestRun r) => new()
    {
        Id              = ToSurrealId(r.Id),
        QuestId         = SurrealLink.ToLink("quest", ToSurrealId(r.QuestId)),
        AvatarId        = SurrealLink.ToLink("avatar", ToSurrealId(r.AvatarId)),
        Status          = r.Status.ToString(),
        StartedAt       = ToUtcOffset(r.StartedAt),
        EndedAt         = r.EndedAt.HasValue ? ToUtcOffset(r.EndedAt.Value) : null,
        ParentRunId     = r.ParentRunId.HasValue     ? SurrealLink.ToLink("quest_run", ToSurrealId(r.ParentRunId.Value))     : null,
        ForkedAtNodeId  = r.ForkedAtNodeId.HasValue  ? SurrealLink.ToLink("quest_node", ToSurrealId(r.ForkedAtNodeId.Value))  : null,
        ForkReason      = r.ForkReason,
        FailReason      = r.FailReason,
    };

    private static QuestRun ToDomain(QuestRunPoco p) => new()
    {
        Id              = FromSurrealId(p.Id),
        QuestId         = string.IsNullOrEmpty(p.QuestId)  ? Guid.Empty : FromSurrealIdFk(SurrealLink.FromLink(p.QuestId)!),
        AvatarId        = string.IsNullOrEmpty(p.AvatarId) ? Guid.Empty : FromSurrealIdFk(SurrealLink.FromLink(p.AvatarId)!),
        Status          = ParseStatus(p.Status),
        StartedAt       = p.StartedAt.UtcDateTime,
        EndedAt         = p.EndedAt?.UtcDateTime,
        ParentRunId     = string.IsNullOrEmpty(p.ParentRunId)    ? null : FromSurrealIdFk(SurrealLink.FromLink(p.ParentRunId)!),
        ForkedAtNodeId  = string.IsNullOrEmpty(p.ForkedAtNodeId) ? null : FromSurrealIdFk(SurrealLink.FromLink(p.ForkedAtNodeId)!),
        ForkReason      = p.ForkReason,
        FailReason      = p.FailReason,
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ToSurrealId(Guid id) => id.ToString("N").ToLowerInvariant();

    private static Guid FromSurrealId(string id)
        => Guid.ParseExact(id, "N");

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

    private static QuestRunStatus ParseStatus(string? raw) =>
        Enum.TryParse<QuestRunStatus>(raw, ignoreCase: false, out var v)
            ? v
            : throw new InvalidOperationException(
                $"Unrecognised QuestRunStatus '{raw}' read from SurrealDB. " +
                "Schema ASSERT INSIDE [...] should have prevented this; refresh the schema.");

    private static OASISResult<T> Ok<T>(T value, string msg = "Success") =>
        new() { Result = value, Message = msg };

    private static OASISResult<IEnumerable<T>> OkMany<T>(IEnumerable<T> values, string msg = "Success") =>
        new() { Result = values, Message = msg };

    private static OASISResult<T> Missing<T>(string msg) =>
        new() { IsError = true, Message = msg, Result = default };

    private static OASISResult<T> Err<T>(string msg) =>
        new() { IsError = true, Message = msg, Result = default };

    // ── POCO (private — replace with generated POCO when source-gen catches up) ──

    private sealed class QuestRunPoco : Oasis.SurrealDb.Client.ISurrealRecord
    {
        public string SchemaName => RunTable;

        [JsonPropertyName("id")]                public string Id { get; set; } = string.Empty;
        [JsonPropertyName("quest_id")]          public string QuestId { get; set; } = string.Empty;
        [JsonPropertyName("avatar_id")]         public string AvatarId { get; set; } = string.Empty;
        [JsonPropertyName("status")]            public string? Status { get; set; }
        [JsonPropertyName("started_at")]        public DateTimeOffset StartedAt { get; set; }
        [JsonPropertyName("ended_at")]          public DateTimeOffset? EndedAt { get; set; }
        [JsonPropertyName("parent_run_id")]     public string? ParentRunId { get; set; }
        [JsonPropertyName("forked_at_node_id")] public string? ForkedAtNodeId { get; set; }
        [JsonPropertyName("fork_reason")]       public string? ForkReason { get; set; }
        [JsonPropertyName("fail_reason")]       public string? FailReason { get; set; }
    }

    private sealed class QuestRunIdProjection
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    }
}
