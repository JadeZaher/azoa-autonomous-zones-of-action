using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.NodeAdmin;

/// <summary>One audit event before it is chained.</summary>
public sealed record NodeAdminAuditEntry(
    string Actor,
    IReadOnlyList<string> Amr,
    string Action,
    string Target,
    string Outcome,
    string Detail,
    string Ip,
    string RequestId);

/// <summary>Result of recomputing the chain.</summary>
public sealed record NodeAdminAuditVerification(bool Ok, long VerifiedThroughSeq, string HeadHash, long? FirstBrokenSeq);

/// <summary>Append-only, hash-chained node-admin audit (plan §8 H4/H5); see <c>Services/NodeAdmin/AGENTS.md</c> §audit.</summary>
public sealed class NodeAdminAuditLog(ISurrealExecutor executor, ILogger<NodeAdminAuditLog> logger)
{
    public const string OutcomeIntent = "intent";
    public const string OutcomeOk = "ok";
    public const string OutcomeDenied = "denied";
    public const string OutcomeFailed = "failed";

    /// <summary>A node-admin read of personal or financial data (D-M2); never preceded by an intent row.</summary>
    public const string OutcomeRead = "read";

    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";
    private const string HeadMarker = "NODE_ADMIN_AUDIT_HEAD_MOVED";
    private const int MaxAppendAttempts = 16;

    /// <summary>Rows verified by default (M-3): the tail window, not a full rescan per call.</summary>
    public const long DefaultVerifyWindow = 10_000;
    private static readonly TimeSpan FullVerifyInterval = TimeSpan.FromMinutes(1);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> LastFullVerify = new(StringComparer.Ordinal);

    /// <summary>Full-chain verification throttle: at most one per minute per principal (L-b).</summary>
    public static bool TryBeginFullVerify(string principal, DateTimeOffset now)
    {
        while (true)
        {
            if (!LastFullVerify.TryGetValue(principal, out var last))
            {
                if (LastFullVerify.TryAdd(principal, now.UtcTicks)) return true;
                continue;
            }
            if (now.UtcTicks - last < FullVerifyInterval.Ticks) return false;
            if (LastFullVerify.TryUpdate(principal, now.UtcTicks, last)) return true;
        }
    }

    /// <summary>Canonical row hash: hex(sha256(utf8(prevHash LF seq LF at LF actor LF action LF target LF outcome LF detail))).</summary>
    public static string ComputeHash(string prevHash, long seq, string at, string actor, string action, string target, string outcome, string detail)
    {
        var canonical = string.Join('\n', prevHash, seq.ToString(CultureInfo.InvariantCulture), at, actor, action, target, outcome, detail);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string RecordIdFor(long seq) => seq.ToString("D20", CultureInfo.InvariantCulture);

    /// <summary>Appends one chained row; throws when the chain cannot be extended (callers map that to AUDIT_UNAVAILABLE).</summary>
    public async Task<NodeAdminAudit> AppendAsync(NodeAdminAuditEntry entry, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAppendAttempts; attempt++)
        {
            var head = await executor.QuerySingleAsync<NodeAdminAuditHead>(SurrealQuery<NodeAdminAuditHead>.Key(NodeAdminAuditHead.SingletonId), ct);
            var expectedSeq = head?.Seq ?? 0;
            var prevHash = head?.Hash ?? GenesisHash;
            var seq = expectedSeq + 1;
            var at = NodeAdminCrypto.FormatTimestamp(DateTimeOffset.UtcNow);
            var row = new NodeAdminAudit
            {
                Id = RecordIdFor(seq),
                Seq = seq,
                At = at,
                Actor = entry.Actor,
                Amr = entry.Amr,
                Action = entry.Action,
                Target = entry.Target,
                Outcome = entry.Outcome,
                Detail = entry.Detail,
                Ip = entry.Ip,
                RequestId = entry.RequestId,
                PrevHash = prevHash,
                Hash = ComputeHash(prevHash, seq, at, entry.Actor, entry.Action, entry.Target, entry.Outcome, entry.Detail),
            };
            if (await TryAppendAsync(head, row, ct))
            {
                // Structured mirror of the row (H5): no secrets ever enter an entry.
                logger.LogInformation(
                    "NodeAdminAudit seq={Seq} actor={Actor} action={Action} target={Target} outcome={Outcome} hash={Hash}",
                    row.Seq, row.Actor, row.Action, row.Target, row.Outcome, row.Hash);
                return row;
            }
        }
        throw new InvalidOperationException("The node-admin audit chain could not be extended after repeated head contention.");
    }

    /// <summary>Rows with seq greater than <paramref name="afterSeq"/>, ascending.</summary>
    public async Task<IReadOnlyList<NodeAdminAudit>> ListAsync(long afterSeq, int limit, CancellationToken ct)
        => await executor.QueryAsync<NodeAdminAudit>(SurrealQuery<NodeAdminAudit>.From()
            .Where(row => row.Seq > afterSeq).OrderBy(row => row.Seq).Limit(Math.Clamp(limit, 1, 500)), ct);

    public Task<NodeAdminAuditHead?> GetHeadAsync(CancellationToken ct)
        => executor.QuerySingleAsync<NodeAdminAuditHead>(SurrealQuery<NodeAdminAuditHead>.Key(NodeAdminAuditHead.SingletonId), ct);

    /// <summary>
    /// Recomputes hashes and links from <paramref name="fromSeq"/> to the head and checks the head matches the last row. A window
    /// anchors on row fromSeq-1, whose own hash is recomputed first.
    /// </summary>
    public async Task<NodeAdminAuditVerification> VerifyAsync(long fromSeq, CancellationToken ct)
    {
        var head = await GetHeadAsync(ct);
        var expectedPrev = GenesisHash;
        var expectedSeq = Math.Max(1, fromSeq);
        if (expectedSeq > 1)
        {
            var anchor = (await ListAsync(expectedSeq - 2, 1, ct)).SingleOrDefault();
            if (anchor is null || anchor.Seq != expectedSeq - 1
                || anchor.Hash != ComputeHash(anchor.PrevHash, anchor.Seq, anchor.At, anchor.Actor, anchor.Action, anchor.Target, anchor.Outcome, anchor.Detail))
                return new NodeAdminAuditVerification(false, expectedSeq - 1, head?.Hash ?? GenesisHash, expectedSeq - 1);
            expectedPrev = anchor.Hash;
        }
        while (true)
        {
            var page = await ListAsync(expectedSeq - 1, 500, ct);
            if (page.Count == 0) break;
            foreach (var row in page)
            {
                var recomputed = ComputeHash(row.PrevHash, row.Seq, row.At, row.Actor, row.Action, row.Target, row.Outcome, row.Detail);
                if (row.Seq != expectedSeq || !string.Equals(row.PrevHash, expectedPrev, StringComparison.Ordinal)
                    || !string.Equals(row.Hash, recomputed, StringComparison.Ordinal))
                    return new NodeAdminAuditVerification(false, expectedSeq - 1, head?.Hash ?? GenesisHash, expectedSeq);
                expectedPrev = row.Hash;
                expectedSeq++;
            }
        }
        var verifiedThrough = expectedSeq - 1;
        var headMatches = (head?.Seq ?? 0) == verifiedThrough && string.Equals(head?.Hash ?? GenesisHash, expectedPrev, StringComparison.Ordinal);
        return headMatches
            ? new NodeAdminAuditVerification(true, verifiedThrough, expectedPrev, null)
            : new NodeAdminAuditVerification(false, verifiedThrough, head?.Hash ?? GenesisHash, verifiedThrough + 1);
    }

    private async Task<bool> TryAppendAsync(NodeAdminAuditHead? head, NodeAdminAudit row, CancellationToken ct)
    {
        var advanceHead = head is null
            ? SurrealWriter.Create(new NodeAdminAuditHead { Seq = row.Seq, Hash = row.Hash })
            : SurrealWriter.UpdateOnly<NodeAdminAuditHead>(NodeAdminAuditHead.SingletonId)
                .Where(item => item.Seq == head.Seq && item.Hash == head.Hash)
                .Set(item => item.Seq, row.Seq).Set(item => item.Hash, row.Hash).Build();
        // raw: the head CAS and the chained row must commit together so the chain never forks or gaps.
        var batch = SurrealQuery.Combine(
            SurrealQuery.Of("BEGIN"),
            SurrealGeneratedStatementScope.Assign(advanceHead, "audit_head", "audit_head"),
            SurrealGeneratedStatementScope.RequireResult("audit_head", HeadMarker),
            SurrealGeneratedStatementScope.Assign(SurrealWriter.Create(row), "audit_row", "audit_row"),
            SurrealGeneratedStatementScope.RequireResult("audit_row", HeadMarker),
            SurrealQuery.Of("COMMIT"));
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(batch, ct);
                response.EnsureAllOk();
                return true;
            }, ct);
            return true;
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(HeadMarker, StringComparison.Ordinal)
            || SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            return false;
        }
    }
}
