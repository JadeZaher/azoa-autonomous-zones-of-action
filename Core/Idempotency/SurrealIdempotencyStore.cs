using System.Security.Cryptography;
using System.Text;
using Oasis.SurrealDb.Client.Query;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Models.Idempotency;
using GeneratedPoco = OASIS.WebAPI.Persistence.SurrealDb.Models.IdempotencyKeyStore;

namespace OASIS.WebAPI.Core.Idempotency;

/// <summary>
/// SurrealDB-backed <see cref="IIdempotencyStore"/>.
///
/// Atomicity model:
///   The <c>idempotency_key_store</c> table has a UNIQUE index on the <c>key</c>
///   field (<c>DEFINE INDEX idempotency_key_unique</c>). <see cref="TryClaimAsync"/>
///   attempts to INSERT a fresh InProgress row via a <c>CREATE</c> statement.
///   When SurrealDB rejects the INSERT due to the UNIQUE violation the HTTP
///   response returns a statement result with <c>status="ERR"</c>; this is
///   detected by inspecting <see cref="Oasis.SurrealDb.Client.SurrealStatementResult.IsOk"/>
///   on <c>response[0]</c> (the C5 fix — per-statement inspection via
///   <see cref="ISurrealExecutor.ExecuteAsync"/> rather than
///   <see cref="ISurrealExecutor.QueryAsync{T}"/> which auto-throws).
///
/// Record-ID encoding (Option A — deterministic):
///   The SurrealDB record id is derived from the caller-supplied idempotency
///   key as follows:
///     1. Encode the key as UTF-8 bytes.
///     2. Compute SHA-256.
///     3. Hex-encode the 32 bytes → 64 lowercase hex chars.
///   The resulting id is safe for SurrealDB identifiers (only [0-9a-f]).
///   This makes every <see cref="GetAsync"/> a single O(1) record-id lookup
///   instead of a secondary-index scan, and allows the conditional UPDATE in
///   <see cref="CompleteAsync"/> / <see cref="FailAsync"/> to address the row
///   without a preceding SELECT.
///
/// Isolation: <see cref="ISurrealExecutor"/> is injected directly (no
///   <see cref="IServiceScopeFactory"/> required — the executor is not bound to
///   a shared request-scoped EF context, so there is no "flush unrelated
///   tracked entities" risk).
///
/// State-transition guard:
///   <see cref="CompleteAsync"/> uses a multi-field conditional UPDATE that only
///   fires when <c>state = InProgress</c>.  Zero affected rows → the claim was
///   already resolved (race-lost); the method is a no-op (mirrors EF behaviour).
///   <see cref="FailAsync"/> applies the same guard.
/// </summary>
public sealed class SurrealIdempotencyStore : IIdempotencyStore
{
    private const string Table = "idempotency_key_store";

    private readonly ISurrealExecutor _executor;

    public SurrealIdempotencyStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    // ── TryClaimAsync ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IdempotencyClaim> TryClaimAsync(
        string key,
        string operationType,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Idempotency key must be non-empty.", nameof(key));

        var recordId  = DeterministicId(key);
        var now       = DateTimeOffset.UtcNow;

        // Fast-path: if the record already exists, return it without attempting
        // an INSERT. CancellationToken.None is intentional here — honouring a
        // cancelled request token would defeat exactly-once (a duplicate must
        // still replay, never surface a raw cancellation error).
        var existing = await FetchByRecordIdAsync(recordId, CancellationToken.None);
        if (existing is not null)
            return new IdempotencyClaim(false, FromPoco(existing));

        // Attempt INSERT-wins. SurrealDB rejects a duplicate on the UNIQUE index
        // on `key` with status="ERR" in the per-statement slot.
        // We use ExecuteAsync (not QueryAsync) so the ERR is surfaced as
        // response[0].IsOk == false rather than thrown — this is the C5 fix.
        var content = BuildContentDict(recordId, key, operationType, now);
        var insertQ = SurrealQuery
            .Of("CREATE type::record($_t, $_id) CONTENT $_content RETURN AFTER")
            .WithParam("_t",       Table)
            .WithParam("_id",      recordId)
            .WithParam("_content", content);

        var response = await _executor.ExecuteAsync(insertQ, ct);

        if (response[0].IsOk)
        {
            // INSERT succeeded — this caller wins the claim.
            var inserted = response[0].GetValues<GeneratedPoco>();
            var poco = inserted.Count > 0 ? inserted[0] : null;

            // If RETURN AFTER gave us the row, use it; otherwise construct a
            // synthetic record from what we sent.
            var won = poco is not null
                ? FromPoco(poco)
                : new IdempotencyRecord
                {
                    Key           = key,
                    OperationType = operationType,
                    State         = IdempotencyState.InProgress,
                    CreatedAt     = now.UtcDateTime,
                    UpdatedAt     = now.UtcDateTime
                };
            return new IdempotencyClaim(true, won);
        }

        // The INSERT was rejected — positively confirm it was a UNIQUE violation
        // by re-reading the winning row. On any other error, the Detail text is
        // non-null and we check for index name or "duplicate" / "Unique" markers.
        // SurrealDB UNIQUE violation detail contains the index name.
        var detail = response[0].Detail ?? string.Empty;
        if (!IsUniqueViolation(detail))
        {
            // Genuine error (not a UNIQUE collision) — surface it.
            throw new InvalidOperationException(
                $"SurrealIdempotencyStore.TryClaimAsync failed for key '{key}': " +
                $"SurrealDB returned ERR: {detail}");
        }

        // UNIQUE violation: re-read the winning row. CancellationToken.None —
        // same rationale as the fast-path read above.
        var winner = await FetchByRecordIdAsync(recordId, CancellationToken.None);
        if (winner is not null)
            return new IdempotencyClaim(false, FromPoco(winner));

        // UNIQUE violation but the winning row vanished (concurrent delete).
        // Surface the original error rather than fabricating a claim.
        throw new InvalidOperationException(
            $"SurrealIdempotencyStore.TryClaimAsync: UNIQUE violation for key '{key}' " +
            "but the winning row was not found on re-read. " +
            $"Original detail: {detail}");
    }

    // ── CompleteAsync ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task CompleteAsync(string key, string resultPayload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Idempotency key must be non-empty.", nameof(key));

        var recordId = DeterministicId(key);

        // Conditional multi-field UPDATE: only fires when state = InProgress.
        // UpdateOnlyBuilder.Set() is single-field only; we use SurrealQuery.Of
        // for multi-field state transitions. All values are bound as $params —
        // no interpolation.
        var q = SurrealQuery
            .Of("UPDATE type::record($_t, $_id) WHERE state = $_expected SET state = $_next, result_payload = $_payload, updated_at = $_now RETURN AFTER")
            .WithParam("_t",        Table)
            .WithParam("_id",       recordId)
            .WithParam("_expected", "InProgress")
            .WithParam("_next",     "Completed")
            .WithParam("_payload",  resultPayload)
            .WithParam("_now",      DateTimeOffset.UtcNow);

        var response = await _executor.ExecuteAsync(q, ct);

        if (!response[0].IsOk)
        {
            // Record does not exist or DB error.
            var detail = response[0].Detail ?? string.Empty;
            throw new InvalidOperationException(
                $"Cannot complete idempotency key '{key}': " +
                $"SurrealDB returned ERR: {detail}. " +
                "CompleteAsync must follow a winning TryClaimAsync.");
        }

        // Zero affected rows → the state was not InProgress (already Completed
        // or Failed). Mirror EF behaviour: this is a no-op (the caller lost
        // the race or is re-calling after a prior transition).
        // We do NOT throw here; the state is already terminal.
    }

    // ── FailAsync ─────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task FailAsync(string key, string error, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Idempotency key must be non-empty.", nameof(key));

        var recordId = DeterministicId(key);

        var q = SurrealQuery
            .Of("UPDATE type::record($_t, $_id) WHERE state = $_expected SET state = $_next, error = $_error, updated_at = $_now RETURN AFTER")
            .WithParam("_t",        Table)
            .WithParam("_id",       recordId)
            .WithParam("_expected", "InProgress")
            .WithParam("_next",     "Failed")
            .WithParam("_error",    error)
            .WithParam("_now",      DateTimeOffset.UtcNow);

        var response = await _executor.ExecuteAsync(q, ct);

        if (!response[0].IsOk)
        {
            var detail = response[0].Detail ?? string.Empty;
            throw new InvalidOperationException(
                $"Cannot fail idempotency key '{key}': " +
                $"SurrealDB returned ERR: {detail}. " +
                "FailAsync must follow a winning TryClaimAsync.");
        }

        // Zero affected rows → not InProgress (already Completed or Failed).
        // No-op by design (same contract as EF impl).
    }

    // ── GetAsync ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IdempotencyRecord?> GetAsync(string key, CancellationToken ct)
    {
        var recordId = DeterministicId(key);
        var poco = await FetchByRecordIdAsync(recordId, ct);
        return poco is not null ? FromPoco(poco) : null;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Derives the SurrealDB record id from an idempotency key.
    ///
    /// Encoding: SHA-256(UTF-8(key)) → 64-char lowercase hex string.
    /// The output is safe for SurrealDB record ids (only [0-9a-f]).
    /// Deterministic: same key always produces the same id, enabling O(1)
    /// record-id lookups without a secondary-index scan on <c>key</c>.
    /// </summary>
    public static string DeterministicId(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Fetches a row by its deterministic record id. Returns null when not found.
    /// </summary>
    private async Task<GeneratedPoco?> FetchByRecordIdAsync(string recordId, CancellationToken ct)
    {
        var q = SurrealQuery
            .Of("SELECT * FROM type::record($_t, $_id)")
            .WithParam("_t",  Table)
            .WithParam("_id", recordId);

        var response = await _executor.ExecuteAsync(q, ct);
        if (!response[0].IsOk)
            return null;

        var values = response[0].GetValues<GeneratedPoco>();
        return values.Count > 0 ? values[0] : null;
    }

    /// <summary>
    /// Detects a SurrealDB UNIQUE-index violation from the statement
    /// <c>detail</c> string. SurrealDB surfaces the error message containing
    /// the index name (e.g. <c>"idempotency_key_unique"</c>) or the words
    /// "Unique" / "duplicate" / "already exists".
    ///
    /// This is a positive-identification check — if the detail does NOT match
    /// any of these patterns the caller rethrows the original error rather than
    /// masking it as an idempotent replay.
    /// </summary>
    private static bool IsUniqueViolation(string detail)
    {
        if (string.IsNullOrEmpty(detail)) return false;

        return detail.Contains("idempotency_key_unique", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("Unique", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("already exists", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("index", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds the content dictionary for the INSERT / CREATE statement.
    /// Uses explicit string keys matching the SurrealDB schema field names.
    /// </summary>
    private static Dictionary<string, object?> BuildContentDict(
        string recordId,
        string key,
        string operationType,
        DateTimeOffset now)
        => new(StringComparer.Ordinal)
        {
            ["id"]             = recordId,
            ["key"]            = key,
            ["operation_type"] = operationType,
            ["state"]          = "InProgress",
            ["result_payload"] = null,
            ["error"]          = null,
            ["created_at"]     = now,
            ["updated_at"]     = now,
            ["ttl_expires_at"] = null,
        };

    /// <summary>Maps the generated POCO to the legacy domain model.</summary>
    private static IdempotencyRecord FromPoco(GeneratedPoco p) => new()
    {
        Key           = p.Key,
        OperationType = p.OperationType,
        State         = p.State switch
        {
            GeneratedPoco.StateKind.InProgress => IdempotencyState.InProgress,
            GeneratedPoco.StateKind.Completed  => IdempotencyState.Completed,
            GeneratedPoco.StateKind.Failed     => IdempotencyState.Failed,
            _                                  => IdempotencyState.InProgress
        },
        ResultPayload = p.ResultPayload,
        Error         = p.Error,
        CreatedAt     = p.CreatedAt.UtcDateTime,
        UpdatedAt     = p.UpdatedAt.UtcDateTime,
    };
}
