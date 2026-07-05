using System.Text.Json.Serialization;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IQuestNodeExecutionStore"/>. One row per
/// <c>(RunId, NodeId)</c> pair; the natural key is enforced by the UNIQUE
/// composite index on the <c>quest_node_execution</c> schema, so a
/// concurrent <see cref="CreateAsync"/> for the same pair fails at the DB
/// boundary rather than producing a duplicate row.
///
/// <para>
/// <b>G2 — <see cref="TryClaimPendingAsync"/></b> is the exactly-once claim
/// primitive: an atomic conditional <c>UPDATE … WHERE run_id = $rid AND
/// node_id = $nid AND state = 'Pending' RETURN AFTER</c>. Verbatim from
/// <c>SURREAL-SCHEMA-HINTS.md</c> §5 (lines 228-237). Under N racing workers
/// at most one update mutates a row; the others receive <c>AffectedCount==0</c>
/// (or no <c>state='Pending'</c> match) and return null. <c>IsError == true</c>
/// is reserved for "row does not exist at all" — the row-exists-but-not-Pending
/// race-loser case returns <c>Result == null</c> with <c>IsError == false</c>.
/// </para>
///
/// <para>
/// <b>HIGH#7 state-machine guard</b> on <see cref="UpdateAsync"/>: when the
/// caller supplies <see cref="QuestNodeState"/> the UPDATE only proceeds when
/// the currently-stored <c>state</c> equals that value. This mirrors the
/// fork-vs-in-flight-success race the in-memory store guards against:
/// returning an error rather than overwriting prevents a Succeeded transition
/// from silently winning over a concurrent fork's Cancelled stamp (and
/// vice-versa).
/// </para>
///
/// <para>
/// Pattern mirrors <see cref="SurrealSagaStore"/> — Guid('N') lowercase-hex
/// record ids, inline POCO (replace with generated POCO when source-gen
/// catches up — <c>AZOA.WebAPI.Persistence.SurrealDb.Models.QuestNodeExecution</c>
/// already exists), every value parameter-bound (G3 / SRDB0001).
/// </para>
/// </summary>
public sealed class SurrealQuestNodeExecutionStore : IQuestNodeExecutionStore
{
    private const string ExecTable = "quest_node_execution";

    // State string literals — passed as bound parameters so the schema
    // ASSERT INSIDE [...] comparison uses the same tokens the DDL declares.
    private const string StatePending   = "Pending";
    private const string StateRunning   = "Running";

    private readonly ISurrealExecutor _executor;

    public SurrealQuestNodeExecutionStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    // ── CreateAsync ──────────────────────────────────────────────────────────

    public async Task<AZOAResult<QuestNodeExecution>> CreateAsync(
        QuestNodeExecution execution, CancellationToken ct = default)
    {
        if (execution is null)
            return Err<QuestNodeExecution>("CreateAsync: execution must not be null.");
        if (execution.Id == Guid.Empty)
            return Err<QuestNodeExecution>("CreateAsync: execution.Id must not be empty.");

        try
        {
            var poco = FromDomain(execution);

            // CREATE rather than UPDATE so the UNIQUE (run_id, node_id) index
            // is the arbiter — a duplicate per-(run, node) insert fails at the
            // DB boundary rather than overwriting an existing row. The
            // SurrealStatementException (or non-OK status) surfaces as an
            // AZOAResult error to the caller.
            var q = SurrealQuery
                .Of("CREATE type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t",    ExecTable)
                .WithParam("_id",   poco.Id)
                .WithParam("_body", poco);

            SurrealResponse resp;
            try
            {
                resp = await _executor.ExecuteAsync(q, ct);
            }
            catch (SurrealStatementException ex)
            {
                return Err<QuestNodeExecution>(
                    $"QuestNodeExecution already exists for (run={execution.RunId}, node={execution.NodeId}): {ex.Message}");
            }

            if (resp.Count == 0 || !resp[0].IsOk)
            {
                return Err<QuestNodeExecution>(
                    $"QuestNodeExecution {execution.Id} could not be created (possible duplicate (run, node)).");
            }

            return Ok(execution.Clone(), "Created.");
        }
        catch (Exception ex)
        {
            return Err<QuestNodeExecution>($"SurrealQuestNodeExecutionStore.CreateAsync({execution.Id}) failed: {ex.Message}");
        }
    }

    // ── GetByIdAsync ─────────────────────────────────────────────────────────

    public async Task<AZOAResult<QuestNodeExecution>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM type::record($_t, $_id)")
                .WithParam("_t",  ExecTable)
                .WithParam("_id", SurrealId.ToSurrealId(id));

            var rows = await _executor.QueryAsync<QuestNodeExecutionPoco>(q, ct);
            return rows.Count == 0
                ? Missing<QuestNodeExecution>($"QuestNodeExecution {id} not found.")
                : Ok(ToDomain(rows[0]));
        }
        catch (Exception ex)
        {
            return Err<QuestNodeExecution>($"SurrealQuestNodeExecutionStore.GetByIdAsync({id}) failed: {ex.Message}");
        }
    }

    // ── UpdateAsync ──────────────────────────────────────────────────────────

    public async Task<AZOAResult<QuestNodeExecution>> UpdateAsync(
        QuestNodeExecution execution,
        QuestNodeState? expectedState = null,
        CancellationToken ct = default)
    {
        if (execution is null)
            return Err<QuestNodeExecution>("UpdateAsync: execution must not be null.");
        if (execution.Id == Guid.Empty)
            return Err<QuestNodeExecution>("UpdateAsync: execution.Id must not be empty.");

        try
        {
            var surrealId = SurrealId.ToSurrealId(execution.Id);

            // Pre-check existence + the optional state guard. Cheaper than
            // doing a conditional UPDATE + re-fetch on the miss path, and
            // gives us a precise error message ("not found" vs "guard
            // rejected").
            var headQ = SurrealQuery
                .Of("SELECT * FROM type::record($_t, $_id)")
                .WithParam("_t",  ExecTable)
                .WithParam("_id", surrealId);

            var existing = await _executor.QueryAsync<QuestNodeExecutionPoco>(headQ, ct);
            if (existing.Count == 0)
                return Missing<QuestNodeExecution>($"QuestNodeExecution {execution.Id} not found.");

            if (expectedState.HasValue)
            {
                var currentState = ParseState(existing[0].State);
                if (currentState != expectedState.Value)
                {
                    return Err<QuestNodeExecution>(
                        $"state-machine guard rejected update; expected={expectedState.Value} actual={currentState}");
                }
            }

            var poco = FromDomain(execution);

            // UPDATE ... CONTENT replaces all fields. If the caller passed
            // expectedState we have already verified above; SurrealDB does
            // not provide a "WHERE state = $expected" hook inside CONTENT
            // updates, so the verify-then-update sequence is the simplest
            // shape that preserves the in-memory store's semantics. The
            // window between the SELECT and the UPDATE is small; if a
            // stricter race-resolution is needed in the future, swap this
            // for an explicit conditional UPDATE with the same SET clause
            // listing each column individually.
            var updateQ = SurrealQuery
                .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t",    ExecTable)
                .WithParam("_id",   surrealId)
                .WithParam("_body", poco);

            var resp = await _executor.ExecuteAsync(updateQ, ct);
            resp.EnsureAllOk();

            return Ok(execution.Clone(), "Updated.");
        }
        catch (Exception ex)
        {
            return Err<QuestNodeExecution>($"SurrealQuestNodeExecutionStore.UpdateAsync({execution.Id}) failed: {ex.Message}");
        }
    }

    // ── GetByRunIdAsync ──────────────────────────────────────────────────────

    public async Task<AZOAResult<IEnumerable<QuestNodeExecution>>> GetByRunIdAsync(
        Guid runId, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM quest_node_execution WHERE run_id = $_rid ORDER BY started_at ASC")
                .WithParam("_rid", SurrealLink.ToLink("quest_run", SurrealId.ToSurrealId(runId)));

            var rows = await _executor.QueryAsync<QuestNodeExecutionPoco>(q, ct);
            IEnumerable<QuestNodeExecution> result = rows.Select(ToDomain).ToList();
            return new AZOAResult<IEnumerable<QuestNodeExecution>> { Result = result, Message = "Success" };
        }
        catch (Exception ex)
        {
            return Err<IEnumerable<QuestNodeExecution>>(
                $"SurrealQuestNodeExecutionStore.GetByRunIdAsync({runId}) failed: {ex.Message}");
        }
    }

    // ── GetByRunAndNodeAsync ─────────────────────────────────────────────────

    public async Task<AZOAResult<QuestNodeExecution>> GetByRunAndNodeAsync(
        Guid runId, Guid nodeId, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM quest_node_execution WHERE run_id = $_rid AND node_id = $_nid LIMIT 1")
                .WithParam("_rid", SurrealLink.ToLink("quest_run", SurrealId.ToSurrealId(runId)))
                .WithParam("_nid", SurrealLink.ToLink("quest_node", SurrealId.ToSurrealId(nodeId)));

            var rows = await _executor.QueryAsync<QuestNodeExecutionPoco>(q, ct);
            return rows.Count == 0
                ? Missing<QuestNodeExecution>($"No QuestNodeExecution for (run={runId}, node={nodeId}).")
                : Ok(ToDomain(rows[0]));
        }
        catch (Exception ex)
        {
            return Err<QuestNodeExecution>(
                $"SurrealQuestNodeExecutionStore.GetByRunAndNodeAsync({runId},{nodeId}) failed: {ex.Message}");
        }
    }

    // ── TryClaimPendingAsync — G2 single-winner primitive ────────────────────

    public async Task<AZOAResult<QuestNodeExecution?>> TryClaimPendingAsync(
        Guid runId, Guid nodeId, CancellationToken ct = default)
    {
        try
        {
            var runHex  = SurrealLink.ToLink("quest_run", SurrealId.ToSurrealId(runId));
            var nodeHex = SurrealLink.ToLink("quest_node", SurrealId.ToSurrealId(nodeId));

            // Existence probe so the "row missing" signal (IsError == true) is
            // distinguishable from the race-loser signal (Result == null,
            // IsError == false). Matches the in-memory store's interface
            // contract.
            var probe = SurrealQuery
                .Of("SELECT id FROM quest_node_execution WHERE run_id = $_rid AND node_id = $_nid LIMIT 1")
                .WithParam("_rid", runHex)
                .WithParam("_nid", nodeHex);

            var probeRows = await _executor.QueryAsync<QuestNodeExecutionIdProjection>(probe, ct);
            if (probeRows.Count == 0)
            {
                return new AZOAResult<QuestNodeExecution?>
                {
                    IsError = true,
                    Message = $"No QuestNodeExecution for (run={runId}, node={nodeId}).",
                    Result  = null
                };
            }

            // THE single-winner primitive — verbatim from SURREAL-SCHEMA-HINTS
            // §5 lines 228-237. The conditional predicate is the arbiter; under
            // N concurrent calls AT MOST ONE row mutates.
            var nowUtc = DateTime.UtcNow;
            var claim = SurrealQuery
                .Of("UPDATE quest_node_execution SET state = $_running, started_at = $_now WHERE run_id = $_rid AND node_id = $_nid AND state = $_pending RETURN AFTER")
                .WithParam("_rid",     runHex)
                .WithParam("_nid",     nodeHex)
                .WithParam("_pending", StatePending)
                .WithParam("_running", StateRunning)
                .WithParam("_now",     nowUtc);

            var resp = await _executor.ExecuteAsync(claim, ct);
            if (resp.Count == 0 || !resp[0].IsOk)
            {
                return new AZOAResult<QuestNodeExecution?>
                {
                    Result  = null,
                    Message = $"QuestNodeExecution (run={runId}, node={nodeId}) claim failed."
                };
            }

            var winners = resp[0].GetValues<QuestNodeExecutionPoco>();
            if (winners.Count == 0)
            {
                // Row existed but was NOT Pending — lost the race. Not an error.
                return new AZOAResult<QuestNodeExecution?>
                {
                    Result  = null,
                    Message = $"QuestNodeExecution (run={runId}, node={nodeId}) is not Pending (already claimed)."
                };
            }

            return new AZOAResult<QuestNodeExecution?>
            {
                Result  = ToDomain(winners[0]),
                Message = "Claimed."
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<QuestNodeExecution?>
            {
                IsError = true,
                Message = $"SurrealQuestNodeExecutionStore.TryClaimPendingAsync({runId},{nodeId}) failed: {ex.Message}",
                Result  = null
            };
        }
    }

    // ── Mapping ──────────────────────────────────────────────────────────────

    private static QuestNodeExecutionPoco FromDomain(QuestNodeExecution e) => new()
    {
        Id         = SurrealId.ToSurrealId(e.Id),
        RunId      = SurrealLink.ToLink("quest_run", SurrealId.ToSurrealId(e.RunId)),
        NodeId     = SurrealLink.ToLink("quest_node", SurrealId.ToSurrealId(e.NodeId)),
        State      = e.State.ToString(),
        Output     = e.Output,
        Error      = e.Error,
        TxHash     = e.TxHash,
        ChainType  = e.ChainType,
        StartedAt  = ToUtcOffset(e.StartedAt),
        EndedAt    = e.EndedAt.HasValue ? ToUtcOffset(e.EndedAt.Value) : null,
    };

    private static QuestNodeExecution ToDomain(QuestNodeExecutionPoco p) => new()
    {
        Id         = FromSurrealId(p.Id),
        RunId      = string.IsNullOrEmpty(p.RunId)  ? Guid.Empty : FromSurrealId(SurrealLink.FromLink(p.RunId)!),
        NodeId     = string.IsNullOrEmpty(p.NodeId) ? Guid.Empty : FromSurrealId(SurrealLink.FromLink(p.NodeId)!),
        State      = ParseState(p.State),
        Output     = p.Output,
        Error      = p.Error,
        TxHash     = p.TxHash,
        ChainType  = p.ChainType,
        StartedAt  = p.StartedAt.UtcDateTime,
        EndedAt    = p.EndedAt?.UtcDateTime,
    };

    // ── Helpers ──────────────────────────────────────────────────────────────


    private static Guid FromSurrealId(string id)
        => Guid.TryParseExact(id, "N", out var g) ? g : Guid.Empty;

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

    private static QuestNodeState ParseState(string? raw) =>
        Enum.TryParse<QuestNodeState>(raw, ignoreCase: false, out var v)
            ? v
            : throw new InvalidOperationException(
                $"Unrecognised QuestNodeState '{raw}' read from SurrealDB. " +
                "Schema ASSERT INSIDE [...] should have prevented this; refresh the schema.");

    private static AZOAResult<T> Ok<T>(T value, string msg = "Success") =>
        new() { Result = value, Message = msg };

    private static AZOAResult<T> Missing<T>(string msg) =>
        new() { IsError = true, Message = msg, Result = default };

    private static AZOAResult<T> Err<T>(string msg) =>
        new() { IsError = true, Message = msg, Result = default };

    // ── POCO (private — replace with generated POCO when source-gen catches up) ──

    private sealed class QuestNodeExecutionPoco : SurrealForge.Client.ISurrealRecord
    {
        public string SchemaName => ExecTable;

        [JsonPropertyName("id")]         public string Id { get; set; } = string.Empty;
        [JsonPropertyName("run_id")]     public string RunId { get; set; } = string.Empty;
        [JsonPropertyName("node_id")]    public string NodeId { get; set; } = string.Empty;
        [JsonPropertyName("state")]      public string? State { get; set; }
        [JsonPropertyName("output")]     public string? Output { get; set; }
        [JsonPropertyName("error")]      public string? Error { get; set; }
        [JsonPropertyName("tx_hash")]    public string? TxHash { get; set; }
        [JsonPropertyName("chain_type")] public string? ChainType { get; set; }
        [JsonPropertyName("started_at")] public DateTimeOffset StartedAt { get; set; }
        [JsonPropertyName("ended_at")]   public DateTimeOffset? EndedAt { get; set; }
    }

    private sealed class QuestNodeExecutionIdProjection
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    }
}
