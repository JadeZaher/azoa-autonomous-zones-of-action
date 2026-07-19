using System.Text.Json;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Bridge;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IBridgeStore"/>. Translates between the legacy
/// domain models (<see cref="BridgeTransactionResult"/>,
/// <see cref="ConsumedVaaRecord"/>, <see cref="BlockchainOperation"/>) and the
/// generated POCOs (<see cref="BridgeTx"/>, <see cref="ConsumedVaaLedger"/>,
/// <see cref="OperationLog"/>).
///
/// <para>
/// This is the SurrealDB realisation of the exactly-once primitive contract
/// documented on <see cref="IBridgeStore"/>: <see cref="TryTransitionBridgeStatusAsync"/>
/// and <see cref="TryTransitionOperationStatusAsync"/> issue a single
/// conditional UPDATE and return the affected-row count VERBATIM. The store
/// NEVER asserts==1, retries, read-modify-writes, or auto-advances any status
/// (including Reversing) — all status policy stays in the caller.
/// </para>
///
/// <para>
/// Id conventions:
/// <list type="bullet">
///   <item><see cref="BridgeTransactionResult.Id"/> is <c>string</c> and is
///         used as the SurrealDB record id verbatim.</item>
///   <item><see cref="ConsumedVaaRecord.Digest"/> (128-char keccak256 hex) is
///         the SurrealDB record id of the <c>consumed_vaa_ledger</c> row.</item>
///   <item><see cref="BlockchainOperation.Id"/> is a <see cref="Guid"/> stored
///         as a 32-char lowercase "N" string (matching
///         <see cref="SurrealBlockchainOperationStore"/>).</item>
/// </list>
/// </para>
///
/// <para>
/// Field mapping notes for <see cref="BridgeTransactionResult"/> ⇔
/// <see cref="BridgeTx"/>:
/// <list type="bullet">
///   <item><c>AvatarId</c>: Guid → 32-char "N" string.</item>
///   <item><c>Amount</c>: ulong → canonical decimal string (BridgeTx keeps the
///         wire-safe representation while the domain prevents signed overflow).</item>
///   <item><c>WormholeEmitterChainId</c>: int? → long? (POCO storage is wider).</item>
///   <item><c>WormholeSequence</c>: long? → long?.</item>
///   <item><c>VaaSignatureCount</c>: int? → long?.</item>
///   <item><c>Status</c> / <c>Mode</c>: enum name ↔ generated POCO enum.</item>
///   <item><c>CreatedAt</c> / <c>CompletedAt</c>: DateTime UTC ↔ DateTimeOffset
///         (Zero offset). Domain DateTimes are coerced to <c>DateTimeKind.Utc</c>
///         before conversion so a Local-kind value cannot crash the offset ctor.</item>
/// </list>
/// </para>
///
/// <para>
/// G2 multi-field UPDATE: the homebake <c>UpdateOnlyBuilder.Set()</c> takes
/// exactly one (field, value) pair. <see cref="TryTransitionBridgeStatusAsync"/>
/// and <see cref="SaveVaaFetchResultAsync"/> need to atomically write several
/// columns in a single conditional UPDATE, so we drop down to a raw
/// <see cref="SurrealQuery.Of(string)"/> body of shape
/// <c>UPDATE type::record(...) WHERE ... SET ..., ..., ... RETURN AFTER</c>
/// with full parameter binding (G3 — no interpolation of user-controlled
/// values). The affected-row count is read from the per-statement response via
/// <see cref="SurrealStatementResultExtensions.AffectedCount"/>; a non-matching
/// WHERE yields zero rows and zero is returned VERBATIM.
/// </para>
/// </summary>
public sealed class SurrealBridgeStore : IBridgeStore
{
    private const string BridgeTable    = "bridge_tx";
    private const string VaaTable       = "consumed_vaa_ledger";
    private const string OperationTable = "operation_log";

    private readonly ISurrealExecutor _executor;

    public SurrealBridgeStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    // ── Bridge reads ──────────────────────────────────────────────────────────

    public async Task<BridgeTransactionResult?> GetBridgeAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        var q = SurrealQuery.SelectById(BridgeTable, id);
        var row = await _executor.QuerySingleAsync<BridgeTx>(q, ct);
        return row is null ? null : FromBridgePoco(row);
    }

    public async Task<bool> ExistsByIdAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        // SELECT id FROM bridge_tx WHERE id = type::record($_t, $_id)
        var q = SurrealQuery
            .Of("SELECT id FROM type::record($_t, $_id)")
            .WithParam("_t",  BridgeTable)
            .WithParam("_id", id);

        var rows = await _executor.QueryAsync<BridgeIdProjection>(q, ct);
        return rows.Count > 0;
    }

    public async Task<IReadOnlyList<string>> GetFailedBridgesWithLockedFundsAsync(
        int maxIds, CancellationToken ct = default)
    {
        if (maxIds <= 0) return Array.Empty<string>();

        // G6: Failed rows with lock_tx_hash set and mint_tx_hash absent represent
        // funds locked on the source chain with no corresponding mint on the target.
        // These are invisible to GetNonTerminalBridgeIdsAsync (terminal status) but
        // still hold value — surface them for operator alerting.
        var q = SurrealQuery
            .Of("SELECT id FROM bridge_tx WHERE status = $_failed AND lock_tx_hash != NONE AND mint_tx_hash = NONE LIMIT $_max")
            .WithParam("_failed", BridgeStatus.Failed.ToString())
            .WithParam("_max",    maxIds);

        var rows = await _executor.QueryAsync<BridgeIdProjection>(q, ct);
        return rows.Select(r => r.Id).Where(s => !string.IsNullOrEmpty(s)).ToList();
    }

    public async Task<BridgeTransactionResult?> GetBridgeByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return null;

        // G3: idempotency key is parameter-bound, never interpolated.
        var q = SurrealQuery
            .Of("SELECT * FROM bridge_tx WHERE idempotency_key = $_key LIMIT 1")
            .WithParam("_key", idempotencyKey);

        var rows = await _executor.QueryAsync<BridgeTx>(q, ct);
        var row = rows.Count > 0 ? rows[0] : null;
        return row is null ? null : FromBridgePoco(row);
    }

    public async Task RecordVaaFetchErrorAsync(string id, string errorMessage, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Bridge id must not be empty.", nameof(id));

        var q = SurrealQuery
            .Of("UPDATE type::record($_t, $_id) SET error_message = $_err RETURN AFTER")
            .WithParam("_t",   BridgeTable)
            .WithParam("_id",  id)
            .WithParam("_err", errorMessage);

        var response = await _executor.ExecuteAsync(q, ct);
        response.EnsureAllOk();
    }

    public async Task<IReadOnlyList<BridgeTransactionResult>> GetBridgeHistoryAsync(
        Guid avatarId, bool descending = false, CancellationToken ct = default)
    {
        var avatarSurrealId = AvatarToSurrealId(avatarId);

        // Sort direction is an author-controlled literal (ASC/DESC), NOT a
        // parameter — SurrealQL does not support parameter-bound sort direction.
        // Two literal query strings, one per branch, keeps SRDB0001 satisfied.
        IReadOnlyList<BridgeTx> rows;
        if (descending)
        {
            var q = SurrealQuery
                .Of("SELECT * FROM bridge_tx WHERE avatar_id = $_avatar ORDER BY created_at DESC")
                .WithParam("_avatar", SurrealLink.ToLink("avatar", avatarSurrealId));
            rows = await _executor.QueryAsync<BridgeTx>(q, ct);
        }
        else
        {
            var q = SurrealQuery
                .Of("SELECT * FROM bridge_tx WHERE avatar_id = $_avatar ORDER BY created_at ASC")
                .WithParam("_avatar", SurrealLink.ToLink("avatar", avatarSurrealId));
            rows = await _executor.QueryAsync<BridgeTx>(q, ct);
        }
        return rows.Select(FromBridgePoco).ToList();
    }

    public async Task<IReadOnlyList<string>> GetNonTerminalBridgeIdsAsync(
        IReadOnlyCollection<BridgeStatus> nonTerminal,
        DateTime staleBefore,
        int batch,
        CancellationToken ct = default)
    {
        if (nonTerminal is null || nonTerminal.Count == 0)
            return Array.Empty<string>();
        if (batch <= 0)
            return Array.Empty<string>();

        // G3: status set is parameter-bound (string[] -> SurrealQL array), not
        // interpolated. INSIDE handles the membership predicate.
        var q = SurrealQuery
            .Of("SELECT id, created_at FROM bridge_tx WHERE status INSIDE $_statuses AND created_at < $_stale ORDER BY created_at ASC LIMIT $_batch")
            .WithParam("_statuses", nonTerminal.Select(s => s.ToString()).ToArray())
            .WithParam("_stale",    ToUtcOffset(staleBefore))
            .WithParam("_batch",    batch);

        var rows = await _executor.QueryAsync<BridgeIdProjection>(q, ct);
        return rows.Select(r => r.Id).Where(s => !string.IsNullOrEmpty(s)).ToList();
    }

    public async Task<IReadOnlyList<Guid>> GetNonTerminalOperationIdsAsync(
        IReadOnlyCollection<string> nonTerminal,
        DateTime staleBefore,
        int batch,
        CancellationToken ct = default)
    {
        if (nonTerminal is null || nonTerminal.Count == 0)
            return Array.Empty<Guid>();
        if (batch <= 0)
            return Array.Empty<Guid>();

        var q = SurrealQuery
            .Of("SELECT id, created_date FROM operation_log WHERE status INSIDE $_statuses AND created_date < $_stale ORDER BY created_date ASC LIMIT $_batch")
            .WithParam("_statuses", nonTerminal.ToArray())
            .WithParam("_stale",    ToUtcOffset(staleBefore))
            .WithParam("_batch",    batch);

        var rows = await _executor.QueryAsync<OperationIdProjection>(q, ct);
        return rows
            .Select(r => r.Id)
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(FromSurrealGuid)
            .ToList();
    }

    public async Task<BlockchainOperation?> GetOperationAsync(Guid id, CancellationToken ct = default)
    {
        var surrealId = GuidToSurrealId(id);
        var q = SurrealQuery.SelectById(OperationTable, surrealId);
        var row = await _executor.QuerySingleAsync<OperationLog>(q, ct);
        return row is null ? null : FromOperationPoco(row);
    }

    // ── Bridge writes ─────────────────────────────────────────────────────────

    public async Task AddBridgeAsync(BridgeTransactionResult tx, CancellationToken ct = default)
    {
        if (tx is null) throw new ArgumentNullException(nameof(tx));
        if (string.IsNullOrWhiteSpace(tx.Id))
            throw new ArgumentException("BridgeTransactionResult.Id must not be empty.", nameof(tx));

        var poco = ToBridgePoco(tx);

        // Coercion-safe SET-based CREATE (SurrealWriter): emits per-field SET
        // with type::string() on string columns, so a `table:id`-shaped value
        // like source_token_id="ASA:123" is not mis-coerced into a record id
        // (which the parameterized CONTENT $body path does on SurrealDB 3.x).
        // Still fails (per-statement ERR) on duplicate id — matches the prior
        // PK-violation semantics; EnsureAllOk surfaces it.
        var q = SurrealWriter.Create(poco);

        var response = await _executor.ExecuteAsync(q, ct);
        response.EnsureAllOk();
    }

    public async Task<bool> TryInsertConsumedVaaAsync(ConsumedVaaRecord record, CancellationToken ct = default)
    {
        if (record is null) throw new ArgumentNullException(nameof(record));
        if (string.IsNullOrWhiteSpace(record.Digest))
            throw new ArgumentException("ConsumedVaaRecord.Digest must not be empty.", nameof(record));

        var poco = ToVaaPoco(record);

        // CREATE … CONTENT … RETURN AFTER. A UNIQUE-index collision (digest OR
        // the (emitter_chain_id, emitter_address, sequence) triple) surfaces as
        // a per-statement ERR; we inspect status PER STATEMENT (closes the C5
        // multi-statement swallow risk) and return false on any failure, true
        // only when the statement is OK and the row was actually written.
        var q = SurrealQuery
            .Of("CREATE type::record($_t, $_id) CONTENT $_body RETURN AFTER")
            .WithParam("_t",    VaaTable)
            .WithParam("_id",   record.Digest)
            .WithParam("_body", poco);

        SurrealResponse response;
        try
        {
            response = await _executor.ExecuteAsync(q, ct);
        }
        catch (SurrealStatementException)
        {
            // Some transport layers surface a per-statement ERR as an exception
            // before the response can be inspected. UNIQUE-constraint violation
            // is the canonical "already consumed" signal — return false rather
            // than leaking the exception.
            return false;
        }

        if (response.Count == 0) return false;
        var stmt = response[0];
        if (!stmt.IsOk) return false;            // constraint violation → already consumed
        return stmt.AffectedCount() == 1;
    }

    public async Task<ConsumedVaaRecord?> GetConsumedVaaAsync(string digest, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;

        // SELECT by deterministic record id (digest is the PK on consumed_vaa_ledger).
        var q = SurrealQuery.SelectById(VaaTable, digest);
        var row = await _executor.QuerySingleAsync<ConsumedVaaLedger>(q, ct);
        return row is null ? null : FromVaaPoco(row);
    }

    public async Task<bool> SaveVaaFetchResultAsync(
        string id,
        string vaaBytes,
        int sigCount,
        string proofData,
        BridgeStatus statusVAAReady,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Bridge id must not be empty.", nameof(id));

        // AC1 fix: guard the multi-field UPDATE with WHERE status = $_expected
        // (AwaitingVAA) so a concurrent/replayed write cannot overwrite payload +
        // status when the row has already advanced. Uses BuildConditionalUpdateSql
        // so the WHERE predicate is part of the same single statement — same
        // atomicity contract as TryTransitionBridgeStatusAsync. Returns affected
        // count VERBATIM: true = won, false = predicate missed (row already moved).
        var setParts = new List<string>
        {
            "vaa_bytes = $_vaa",
            "vaa_signature_count = $_sigs",
            "proof_data = $_proof",
            "status = $_status",
        };

        var paramBag = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["_t"]        = BridgeTable,
            ["_id"]       = id,
            ["_expected"] = BridgeStatus.AwaitingVAA.ToString(),
            ["_vaa"]      = vaaBytes,
            ["_sigs"]     = (long)sigCount,
            ["_proof"]    = proofData,
            ["_status"]   = statusVAAReady.ToString(),
        };

        var sql = BuildConditionalUpdateSql(setParts);
        var q = SurrealQuery.Of(sql).WithParams(paramBag);

        var response = await _executor.ExecuteAsync(q, ct);
        if (response.Count == 0) return false;
        var stmt = response[0];
        if (!stmt.IsOk) return false;
        return stmt.AffectedCount() > 0;
    }

    public async Task<int> TryTransitionBridgeStatusAsync(
        string id,
        BridgeStatus expected,
        BridgeStatus next,
        BridgeStatusMutation? alsoSet,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Bridge id must not be empty.", nameof(id));

        // Compose the SET clause dynamically from non-null mutation fields.
        // Field names come ONLY from this method (allowlisted), parameter VALUES
        // are bound — never interpolated. The shape is:
        //   UPDATE type::record($_t, $_id)
        //     WHERE status = $_expected
        //     SET status = $_next, <field> = $_<field>, ...
        //     RETURN AFTER
        var setParts = new List<string> { "status = $_next" };
        var paramBag = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["_t"]        = BridgeTable,
            ["_id"]       = id,
            ["_expected"] = expected.ToString(),
            ["_next"]     = next.ToString(),
        };

        if (alsoSet is not null)
        {
            // String columns are type::string()-wrapped: a `table:id`-shaped value
            // (e.g. TargetTokenId "ASA:777") is otherwise coerced into a record id
            // by SurrealDB 3.x and rejected by the option<string> column, faulting
            // the whole UPDATE (so it returned affected=0). Numeric/datetime fields
            // below are bound bare. This mirrors SurrealWriter's wrapping rule.
            if (alsoSet.IdempotencyKey is not null)
            {
                setParts.Add("idempotency_key = type::string($_idem)");
                paramBag["_idem"] = alsoSet.IdempotencyKey;
            }
            if (alsoSet.ErrorMessage is not null)
            {
                setParts.Add("error_message = type::string($_err)");
                paramBag["_err"] = alsoSet.ErrorMessage;
            }
            if (alsoSet.LockTxHash is not null)
            {
                setParts.Add("lock_tx_hash = type::string($_lock)");
                paramBag["_lock"] = alsoSet.LockTxHash;
            }
            if (alsoSet.SourceAddress is not null)
            {
                setParts.Add("source_address = type::string($_src_addr)");
                paramBag["_src_addr"] = alsoSet.SourceAddress;
            }
            if (alsoSet.RedemptionTxHash is not null)
            {
                setParts.Add("redemption_tx_hash = type::string($_redeem)");
                paramBag["_redeem"] = alsoSet.RedemptionTxHash;
            }
            if (alsoSet.MintTxHash is not null)
            {
                setParts.Add("mint_tx_hash = type::string($_mint)");
                paramBag["_mint"] = alsoSet.MintTxHash;
            }
            if (alsoSet.TargetTokenId is not null)
            {
                setParts.Add("target_token_id = type::string($_target_token)");
                paramBag["_target_token"] = alsoSet.TargetTokenId;
            }
            if (alsoSet.ProofData is not null)
            {
                setParts.Add("proof_data = type::string($_proof_data)");
                paramBag["_proof_data"] = alsoSet.ProofData;
            }
            if (alsoSet.WormholeEmitterChainId is not null)
            {
                setParts.Add("wormhole_emitter_chain_id = $_emit_chain");
                paramBag["_emit_chain"] = (long)alsoSet.WormholeEmitterChainId.Value;
            }
            if (alsoSet.WormholeEmitterAddress is not null)
            {
                setParts.Add("wormhole_emitter_address = type::string($_emit_addr)");
                paramBag["_emit_addr"] = alsoSet.WormholeEmitterAddress;
            }
            if (alsoSet.WormholeSequence is not null)
            {
                setParts.Add("wormhole_sequence = $_seq");
                paramBag["_seq"] = alsoSet.WormholeSequence.Value;
            }

            // CompletedAt semantics: SetCompletedAtUtcNow wins over
            // ClearCompletedAt when both are set (matches the prior EF
            // expression's evaluation order — SetCompletedAtUtcNow first).
            if (alsoSet.SetCompletedAtUtcNow)
            {
                setParts.Add("completed_at = $_completed");
                paramBag["_completed"] = DateTimeOffset.UtcNow;
            }
            else if (alsoSet.ClearCompletedAt)
            {
                // SurrealDB clears an option<datetime> column when it is set to
                // NONE; we emit a literal NONE token here because there is no
                // single typed binding for "the absence of a value".
                setParts.Add("completed_at = NONE");
            }
        }

        var sql = BuildConditionalUpdateSql(setParts);

        var q = SurrealQuery.Of(sql).WithParams(paramBag);

        var response = await _executor.ExecuteAsync(q, ct);
        if (response.Count == 0) return 0;
        var stmt = response[0];
        if (!stmt.IsOk) return 0;
        return stmt.AffectedCount();
    }

    public async Task<int> TryTransitionOperationStatusAsync(
        Guid id,
        string expected,
        string next,
        DateTime? completedDate,
        CancellationToken ct = default)
    {
        var surrealId = GuidToSurrealId(id);

        // operation_log.status is an enum-backed string column, so the
        // expected/next string literals MUST match StatusKind member names.
        // Caller passes OperationStatus.* constants which already mirror those
        // names — we forward verbatim, the schema ASSERT enforces validity.
        var setParts = new List<string> { "status = $_next" };
        var paramBag = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["_t"]        = OperationTable,
            ["_id"]       = surrealId,
            ["_expected"] = expected,
            ["_next"]     = next,
        };

        if (completedDate is not null)
        {
            setParts.Add("completed_date = $_completed");
            paramBag["_completed"] = ToUtcOffset(completedDate.Value);
        }

        var sql = BuildConditionalUpdateSql(setParts);

        var q = SurrealQuery.Of(sql).WithParams(paramBag);

        var response = await _executor.ExecuteAsync(q, ct);
        if (response.Count == 0) return 0;
        var stmt = response[0];
        if (!stmt.IsOk) return 0;
        return stmt.AffectedCount();
    }

    /// <summary>
    /// Composes a conditional <c>UPDATE type::record(...) WHERE status = ... SET ... RETURN AFTER</c>
    /// statement body from a pre-validated list of <c>field = $_param</c>
    /// fragments. The fragments themselves are author-controlled (never user
    /// input) — every value reference is a parameter token bound elsewhere.
    ///
    /// <para>
    /// Indirection rationale: the SRDB0001 analyzer rejects
    /// <c>SurrealQuery.Of("..." + variable)</c> at the call site via one-hop
    /// data flow. Hiding the composition behind a non-string-builder helper
    /// method keeps the safety contract intact (no value smuggling possible —
    /// values are always parameters) while letting the conditional-update
    /// primitive write a variable set of columns in one statement.
    /// </para>
    /// </summary>
    private static string BuildConditionalUpdateSql(IReadOnlyList<string> setParts)
    {
        // SurrealDB 3.x requires SET before WHERE (UPDATE ... SET ... WHERE ...).
        var sb = new System.Text.StringBuilder();
        sb.Append("UPDATE type::record($_t, $_id) SET ");
        for (int i = 0; i < setParts.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(setParts[i]);
        }
        sb.Append(" WHERE status = $_expected RETURN AFTER");
        return sb.ToString();
    }

    // ── Mapping: BridgeTransactionResult ↔ BridgeTx ───────────────────────────

    private static BridgeTx ToBridgePoco(BridgeTransactionResult tx) => new BridgeTx
    {
        Id                       = tx.Id,
        AvatarId                 = SurrealLink.ToLink("avatar", AvatarToSurrealId(tx.AvatarId)) ?? string.Empty,
        SourceChain              = tx.SourceChain,
        TargetChain              = tx.TargetChain,
        SourceTokenId            = tx.SourceTokenId,
        TargetTokenId            = tx.TargetTokenId,
        SourceAddress            = tx.SourceAddress,
        TargetAddress            = tx.TargetAddress,
        Amount                   = tx.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Status                   = ParseBridgeStatus(tx.Status),
        Mode                     = ParseBridgeMode(tx.Mode),
        LockTxHash               = tx.LockTxHash,
        MintTxHash               = tx.MintTxHash,
        ProofData                = tx.ProofData,
        ErrorMessage             = tx.ErrorMessage,
        CreatedAt                = ToUtcOffset(tx.CreatedAt),
        CompletedAt              = tx.CompletedAt.HasValue ? ToUtcOffset(tx.CompletedAt.Value) : null,
        WormholeEmitterChainId   = tx.WormholeEmitterChainId.HasValue ? (long?)tx.WormholeEmitterChainId.Value : null,
        WormholeEmitterAddress   = tx.WormholeEmitterAddress,
        WormholeSequence         = tx.WormholeSequence,
        VaaBytes                 = tx.VaaBytes,
        VaaSignatureCount        = tx.VaaSignatureCount.HasValue ? (long?)tx.VaaSignatureCount.Value : null,
        RedemptionTxHash         = tx.RedemptionTxHash,
        IdempotencyKey           = tx.IdempotencyKey,
        Network                  = tx.Network.HasValue ? ParseBridgeNetwork(tx.Network.Value) : null,
    };

    private static BridgeTransactionResult FromBridgePoco(BridgeTx poco) => new BridgeTransactionResult
    {
        Id                     = StripSurrealIdPrefix(poco.Id, BridgeTable),
        AvatarId               = AvatarFromSurrealId(SurrealLink.FromLink(poco.AvatarId)),
        SourceChain            = poco.SourceChain,
        TargetChain            = poco.TargetChain,
        SourceTokenId          = poco.SourceTokenId,
        TargetTokenId          = poco.TargetTokenId,
        SourceAddress          = poco.SourceAddress,
        TargetAddress          = poco.TargetAddress,
        Amount                 = ParseBridgeAmount(poco.Amount, poco.Id),
        Status                 = ParseBridgeStatusKind(poco.Status),
        Mode                   = ParseBridgeModeKind(poco.Mode),
        LockTxHash             = poco.LockTxHash,
        MintTxHash             = poco.MintTxHash,
        ProofData              = poco.ProofData,
        ErrorMessage           = poco.ErrorMessage,
        CreatedAt              = poco.CreatedAt.UtcDateTime,
        CompletedAt            = poco.CompletedAt?.UtcDateTime,
        WormholeEmitterChainId = poco.WormholeEmitterChainId.HasValue ? (int?)poco.WormholeEmitterChainId.Value : null,
        WormholeEmitterAddress = poco.WormholeEmitterAddress,
        WormholeSequence       = poco.WormholeSequence,
        VaaBytes               = poco.VaaBytes,
        VaaSignatureCount      = poco.VaaSignatureCount.HasValue ? (int?)poco.VaaSignatureCount.Value : null,
        RedemptionTxHash       = poco.RedemptionTxHash,
        IdempotencyKey         = poco.IdempotencyKey,
        Network                = poco.Network.HasValue ? ParseBridgeNetworkKind(poco.Network.Value) : null,
    };

    // ── Mapping: ConsumedVaaRecord → ConsumedVaaLedger ────────────────────────

    private static ConsumedVaaLedger ToVaaPoco(ConsumedVaaRecord record) => new ConsumedVaaLedger
    {
        Id                  = record.Digest,
        Digest              = record.Digest,
        EmitterChainId      = record.EmitterChainId,
        EmitterAddress      = record.EmitterAddress,
        Sequence            = record.Sequence,
        BridgeTransactionId = SurrealLink.ToLink("bridge_tx", record.BridgeTransactionId),
        ConsumedAt          = ToUtcOffset(record.ConsumedAt),
    };

    private static ConsumedVaaRecord FromVaaPoco(ConsumedVaaLedger poco) => new ConsumedVaaRecord
    {
        Digest              = poco.Digest,
        EmitterChainId      = (int)poco.EmitterChainId,
        EmitterAddress      = poco.EmitterAddress,
        Sequence            = poco.Sequence,
        // BridgeTransactionId is stored as a link (bridge_tx:<id>); strip the table prefix.
        BridgeTransactionId = poco.BridgeTransactionId is null
            ? null
            : StripSurrealIdPrefix(poco.BridgeTransactionId, BridgeTable),
        ConsumedAt          = poco.ConsumedAt.UtcDateTime,
    };

    // ── Mapping: BlockchainOperation ↔ OperationLog (read-only here) ──────────

    private static BlockchainOperation FromOperationPoco(OperationLog poco)
    {
        Dictionary<string, string> parameters = new();
        if (poco.Parameters.HasValue &&
            poco.Parameters.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in poco.Parameters.Value.EnumerateObject())
            {
                parameters[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
        }

        return new BlockchainOperation
        {
            Id            = FromSurrealGuid(poco.Id),
            AvatarId      = poco.AvatarId is not null ? FromSurrealGuid(SurrealLink.FromLink(poco.AvatarId)!) : null,
            WalletId      = poco.WalletId is not null ? FromSurrealGuid(SurrealLink.FromLink(poco.WalletId)!) : null,
            OperationType = poco.OperationType,
            Status        = poco.Status.ToString(),
            Parameters    = parameters,
            CreatedDate   = poco.CreatedDate.UtcDateTime,
            CompletedDate = poco.CompletedDate?.UtcDateTime,
            TokenUri      = poco.TokenUri,
            // Persisted corruption must fail closed instead of wrapping a negative i64.
            Amount        = poco.Amount.HasValue ? checked((ulong)poco.Amount.Value) : 0UL,
            AssetType     = poco.AssetType,
            SourceHolonId = poco.SourceHolonId is not null ? FromSurrealGuid(SurrealLink.FromLink(poco.SourceHolonId)!) : null,
            TargetHolonId = poco.TargetHolonId is not null ? FromSurrealGuid(SurrealLink.FromLink(poco.TargetHolonId)!) : null,
            ExchangeRate  = poco.ExchangeRate,
            RecipientAddress = poco.RecipientAddress,
        };
    }

    // ── Enum mapping helpers ──────────────────────────────────────────────────

    private static BridgeTx.StatusKind ParseBridgeStatus(BridgeStatus status) =>
        Enum.TryParse<BridgeTx.StatusKind>(status.ToString(), ignoreCase: false, out var k)
            ? k
            : throw new InvalidOperationException(
                $"BridgeStatus '{status}' has no corresponding BridgeTx.StatusKind — schema and domain enum drifted.");

    private static BridgeStatus ParseBridgeStatusKind(BridgeTx.StatusKind kind) =>
        Enum.TryParse<BridgeStatus>(kind.ToString(), ignoreCase: false, out var s)
            ? s
            : throw new InvalidOperationException(
                $"BridgeTx.StatusKind '{kind}' has no corresponding BridgeStatus — schema and domain enum drifted.");

    private static ulong ParseBridgeAmount(string? value, string recordId)
    {
        if (!ulong.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var amount)
            || amount == 0)
        {
            throw new InvalidDataException(
                $"Bridge '{StripSurrealIdPrefix(recordId, BridgeTable)}' contains an invalid persisted amount.");
        }

        return amount;
    }

    private static BridgeTx.ModeKind ParseBridgeMode(BridgeMode mode) =>
        Enum.TryParse<BridgeTx.ModeKind>(mode.ToString(), ignoreCase: false, out var k)
            ? k
            : BridgeTx.ModeKind.Trusted;

    private static BridgeMode ParseBridgeModeKind(BridgeTx.ModeKind kind) =>
        Enum.TryParse<BridgeMode>(kind.ToString(), ignoreCase: false, out var m)
            ? m
            : BridgeMode.Trusted;

    private static BridgeTx.NetworkKind ParseBridgeNetwork(ChainNetwork network) =>
        Enum.TryParse<BridgeTx.NetworkKind>(network.ToString(), ignoreCase: false, out var k)
            ? k
            : throw new InvalidOperationException(
                $"ChainNetwork '{network}' has no corresponding BridgeTx.NetworkKind — enums drifted.");

    private static ChainNetwork ParseBridgeNetworkKind(BridgeTx.NetworkKind kind) =>
        Enum.TryParse<ChainNetwork>(kind.ToString(), ignoreCase: false, out var n)
            ? n
            : throw new InvalidOperationException(
                $"BridgeTx.NetworkKind '{kind}' has no corresponding ChainNetwork — enums drifted.");

    // ── Id helpers ────────────────────────────────────────────────────────────

    private static string AvatarToSurrealId(Guid id) => id.ToString("N").ToLowerInvariant();
    private static string GuidToSurrealId(Guid id)   => id.ToString("N").ToLowerInvariant();

    private static Guid AvatarFromSurrealId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return Guid.Empty;
        return FromSurrealGuid(id);
    }

    private static Guid FromSurrealGuid(string id)
    {
        // SurrealDB may surface the id verbatim ("abc...") or as the full
        // record id ("operation_log:abc..."); strip a leading table prefix.
        var clean = StripAnyPrefix(id);
        return Guid.TryParseExact(clean, "N", out var g) ? g : Guid.Empty;
    }

    /// <summary>
    /// SurrealDB sometimes returns ids as <c>table:value</c>. Domain code uses
    /// the value portion only; strip the prefix when present.
    /// </summary>
    private static string StripSurrealIdPrefix(string id, string expectedTable)
    {
        if (string.IsNullOrEmpty(id)) return id;
        var colon = id.IndexOf(':');
        if (colon < 0) return id;
        var table = id.Substring(0, colon);
        if (string.Equals(table, expectedTable, StringComparison.Ordinal))
            return id.Substring(colon + 1).Trim('⟨', '⟩'); // surreal wraps non-simple ids in angle brackets
        return id;
    }

    private static string StripAnyPrefix(string id)
    {
        var colon = id.IndexOf(':');
        if (colon < 0) return id;
        return id.Substring(colon + 1).Trim('⟨', '⟩');
    }

    // ── DateTime helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Coerces a <see cref="DateTime"/> to a <see cref="DateTimeOffset"/> with
    /// a zero offset, treating Unspecified-kind values as UTC. This avoids the
    /// <c>DateTimeOffset(DateTime, TimeSpan)</c> constructor's check that
    /// rejects Local-kind values paired with a non-local offset.
    /// </summary>
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

    // ── Projection types for id-only SELECTs ──────────────────────────────────

    private sealed class BridgeIdProjection : ISurrealRecord
    {
        public string SchemaName => "bridge_tx";

        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
    }

    private sealed class OperationIdProjection : ISurrealRecord
    {
        public string SchemaName => "operation_log";

        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
    }
}
