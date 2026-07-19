using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Bridge;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.TestSupport;

/// <summary>
/// In-memory <see cref="IBridgeStore"/> for unit tests. Preserves the exactly-once
/// / single-winner semantics the production stores (EF + SurrealDB) guarantee:
///
/// <list type="bullet">
///   <item>
///   <b>UNIQUE-on-Digest replay protection.</b>
///   <see cref="TryInsertConsumedVaaAsync"/> returns <c>false</c> when a row with
///   the same <see cref="ConsumedVaaRecord.Digest"/> already exists, mirroring
///   the EF unique-constraint-then-reread codepath.
///   </item>
///   <item>
///   <b>Conditional transitions.</b>
///   <see cref="TryTransitionBridgeStatusAsync"/> /
///   <see cref="TryTransitionOperationStatusAsync"/> hold the store lock for the
///   predicate-check + mutation, so under concurrent calls exactly one caller
///   sees affected==1 and the rest see 0. Same arbitration the EF
///   <c>ExecuteUpdateAsync</c> + DB row lock provides.
///   </item>
///   <item>
///   <b>AsNoTracking parity.</b> Every read returns a fresh clone so a test
///   that mutates a returned object cannot accidentally update the store.
///   </item>
/// </list>
///
/// Seeding helpers (<see cref="SeedBridge"/> / <see cref="SeedOperation"/>) are
/// test-only escape hatches that insert without going through the public
/// interface — used to set up "this row was in state X 30 minutes ago" fixtures
/// the reconciliation tests require.
/// </summary>
public sealed class FakeBridgeStore : IBridgeStore
{
    private readonly object _lock = new();

    private readonly Dictionary<string, BridgeTransactionResult> _bridges = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, BlockchainOperation> _operations = new();
    private readonly Dictionary<string, ConsumedVaaRecord> _consumedVaas = new(StringComparer.Ordinal);

    // ── IBridgeStore reads ────────────────────────────────────────────────────

    public Task<BridgeTransactionResult?> GetBridgeAsync(string id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_bridges.TryGetValue(id, out var b) ? Clone(b) : null);
        }
    }

    public Task<IReadOnlyList<BridgeTransactionResult>> GetBridgeHistoryAsync(
        Guid avatarId, bool descending = false, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var matched = _bridges.Values
                .Where(b => b.AvatarId == avatarId)
                .Select(Clone);
            matched = descending
                ? matched.OrderByDescending(b => b.CreatedAt)
                : matched.OrderBy(b => b.CreatedAt);
            return Task.FromResult<IReadOnlyList<BridgeTransactionResult>>(matched.ToList());
        }
    }

    public Task<IReadOnlyList<string>> GetNonTerminalBridgeIdsAsync(
        IReadOnlyCollection<BridgeStatus> nonTerminal, DateTime staleBefore, int batch, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var set = new HashSet<BridgeStatus>(nonTerminal);
            var ids = _bridges.Values
                .Where(b => set.Contains(b.Status) && b.CreatedAt < staleBefore)
                .OrderBy(b => b.CreatedAt)
                .Take(batch)
                .Select(b => b.Id)
                .ToList();
            return Task.FromResult<IReadOnlyList<string>>(ids);
        }
    }

    public Task<IReadOnlyList<Guid>> GetNonTerminalOperationIdsAsync(
        IReadOnlyCollection<string> nonTerminal, DateTime staleBefore, int batch, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var set = new HashSet<string>(nonTerminal, StringComparer.Ordinal);
            var ids = _operations.Values
                .Where(o => set.Contains(o.Status) && o.CreatedDate < staleBefore)
                .OrderBy(o => o.CreatedDate)
                .Take(batch)
                .Select(o => o.Id)
                .ToList();
            return Task.FromResult<IReadOnlyList<Guid>>(ids);
        }
    }

    public Task<BlockchainOperation?> GetOperationAsync(Guid id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_operations.TryGetValue(id, out var o) ? Clone(o) : null);
        }
    }

    public Task<bool> ExistsByIdAsync(string id, CancellationToken ct = default)
    {
        lock (_lock) { return Task.FromResult(_bridges.ContainsKey(id)); }
    }

    public Task<BridgeTransactionResult?> GetBridgeByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return Task.FromResult<BridgeTransactionResult?>(null);
        lock (_lock)
        {
            var hit = _bridges.Values.FirstOrDefault(b =>
                string.Equals(b.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));
            return Task.FromResult(hit is null ? null : Clone(hit));
        }
    }

    // ── IBridgeStore writes ───────────────────────────────────────────────────

    public Task AddBridgeAsync(BridgeTransactionResult tx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        lock (_lock) { _bridges[tx.Id] = Clone(tx); }
        return Task.CompletedTask;
    }

    public Task<bool> TryInsertConsumedVaaAsync(ConsumedVaaRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock)
        {
            if (_consumedVaas.ContainsKey(record.Digest)) return Task.FromResult(false);
            _consumedVaas[record.Digest] = Clone(record);
            return Task.FromResult(true);
        }
    }

    public Task<ConsumedVaaRecord?> GetConsumedVaaAsync(string digest, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(
                _consumedVaas.TryGetValue(digest, out var r) ? Clone(r) : (ConsumedVaaRecord?)null);
        }
    }

    public Task<bool> SaveVaaFetchResultAsync(
        string id, string vaaBytes, int sigCount, string proofData,
        BridgeStatus statusVAAReady, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_bridges.TryGetValue(id, out var row)) return Task.FromResult(false);
            // AC1 guard: only write when the row is still AwaitingVAA (mirrors WHERE predicate).
            if (row.Status != BridgeStatus.AwaitingVAA) return Task.FromResult(false);
            row.VaaBytes = vaaBytes;
            row.VaaSignatureCount = sigCount;
            row.ProofData = proofData;
            row.Status = statusVAAReady;
            return Task.FromResult(true);
        }
    }

    public Task<int> TryTransitionBridgeStatusAsync(
        string id, BridgeStatus expected, BridgeStatus next, BridgeStatusMutation? alsoSet,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_bridges.TryGetValue(id, out var row)) return Task.FromResult(0);
            if (row.Status != expected) return Task.FromResult(0);

            row.Status = next;
            if (alsoSet is not null)
            {
                if (alsoSet.IdempotencyKey is not null)       row.IdempotencyKey = alsoSet.IdempotencyKey;
                if (alsoSet.ErrorMessage is not null)         row.ErrorMessage = alsoSet.ErrorMessage;
                if (alsoSet.LockTxHash is not null)           row.LockTxHash = alsoSet.LockTxHash;
                if (alsoSet.SourceAddress is not null)        row.SourceAddress = alsoSet.SourceAddress;
                if (alsoSet.RedemptionTxHash is not null)     row.RedemptionTxHash = alsoSet.RedemptionTxHash;
                if (alsoSet.MintTxHash is not null)           row.MintTxHash = alsoSet.MintTxHash;
                if (alsoSet.TargetTokenId is not null)        row.TargetTokenId = alsoSet.TargetTokenId;
                if (alsoSet.ProofData is not null)            row.ProofData = alsoSet.ProofData;
                if (alsoSet.WormholeEmitterChainId is not null)  row.WormholeEmitterChainId = alsoSet.WormholeEmitterChainId;
                if (alsoSet.WormholeEmitterAddress is not null)  row.WormholeEmitterAddress = alsoSet.WormholeEmitterAddress;
                if (alsoSet.WormholeSequence is not null)        row.WormholeSequence = alsoSet.WormholeSequence;
                if (alsoSet.SetCompletedAtUtcNow)             row.CompletedAt = DateTime.UtcNow;
                else if (alsoSet.ClearCompletedAt)            row.CompletedAt = null;
            }
            return Task.FromResult(1);
        }
    }

    public Task<int> TryTransitionOperationStatusAsync(
        Guid id, string expected, string next, DateTime? completedDate, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_operations.TryGetValue(id, out var row)) return Task.FromResult(0);
            if (!string.Equals(row.Status, expected, StringComparison.Ordinal)) return Task.FromResult(0);

            row.Status = next;
            if (completedDate is not null) row.CompletedDate = completedDate;
            return Task.FromResult(1);
        }
    }

    public Task RecordVaaFetchErrorAsync(string id, string errorMessage, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_bridges.TryGetValue(id, out var row)) row.ErrorMessage = errorMessage;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetFailedBridgesWithLockedFundsAsync(
        int maxIds, CancellationToken ct = default)
    {
        lock (_lock)
        {
            IReadOnlyList<string> ids = _bridges.Values
                .Where(r => r.Status == BridgeStatus.Failed
                         && r.LockTxHash != null
                         && r.MintTxHash == null)
                .Take(maxIds)
                .Select(r => r.Id)
                .ToList();
            return Task.FromResult(ids);
        }
    }

    // ── Test-only seeding / inspection helpers ────────────────────────────────

    /// <summary>
    /// Insert a bridge row directly, bypassing the public interface — for
    /// fixtures that need "this row was in state X N minutes ago".
    /// </summary>
    public void SeedBridge(BridgeTransactionResult tx)
    {
        ArgumentNullException.ThrowIfNull(tx);
        lock (_lock) { _bridges[tx.Id] = Clone(tx); }
    }

    /// <summary>Insert an operation row directly. Same intent as <see cref="SeedBridge"/>.</summary>
    public void SeedOperation(BlockchainOperation op)
    {
        ArgumentNullException.ThrowIfNull(op);
        lock (_lock) { _operations[op.Id] = Clone(op); }
    }

    /// <summary>Insert a consumed-VAA row directly (UNIQUE-on-digest still enforced).</summary>
    public bool SeedConsumedVaa(ConsumedVaaRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock)
        {
            if (_consumedVaas.ContainsKey(record.Digest)) return false;
            _consumedVaas[record.Digest] = Clone(record);
            return true;
        }
    }

    // ── Cloning ───────────────────────────────────────────────────────────────

    private static BridgeTransactionResult Clone(BridgeTransactionResult b) => new()
    {
        Id = b.Id,
        AvatarId = b.AvatarId,
        SourceChain = b.SourceChain,
        TargetChain = b.TargetChain,
        SourceTokenId = b.SourceTokenId,
        TargetTokenId = b.TargetTokenId,
        SourceAddress = b.SourceAddress,
        TargetAddress = b.TargetAddress,
        Amount = b.Amount,
        Status = b.Status,
        Mode = b.Mode,
        LockTxHash = b.LockTxHash,
        MintTxHash = b.MintTxHash,
        ProofData = b.ProofData,
        ErrorMessage = b.ErrorMessage,
        CreatedAt = b.CreatedAt,
        CompletedAt = b.CompletedAt,
        WormholeEmitterChainId = b.WormholeEmitterChainId,
        WormholeEmitterAddress = b.WormholeEmitterAddress,
        WormholeSequence = b.WormholeSequence,
        VaaBytes = b.VaaBytes,
        VaaSignatureCount = b.VaaSignatureCount,
        RedemptionTxHash = b.RedemptionTxHash,
        IdempotencyKey = b.IdempotencyKey,
        Network = b.Network,
    };

    private static BlockchainOperation Clone(BlockchainOperation o) => new()
    {
        Id = o.Id,
        AvatarId = o.AvatarId,
        WalletId = o.WalletId,
        OperationType = o.OperationType,
        Status = o.Status,
        Parameters = new Dictionary<string, string>(o.Parameters),
        CreatedDate = o.CreatedDate,
        CompletedDate = o.CompletedDate,
        TokenUri = o.TokenUri,
        Amount = o.Amount,
        AssetType = o.AssetType,
        SourceHolonId = o.SourceHolonId,
        TargetHolonId = o.TargetHolonId,
        ExchangeRate = o.ExchangeRate,
        RecipientAddress = o.RecipientAddress,
    };

    private static ConsumedVaaRecord Clone(ConsumedVaaRecord v) => new()
    {
        Digest = v.Digest,
        EmitterChainId = v.EmitterChainId,
        EmitterAddress = v.EmitterAddress,
        Sequence = v.Sequence,
        BridgeTransactionId = v.BridgeTransactionId,
        ConsumedAt = v.ConsumedAt,
    };
}
