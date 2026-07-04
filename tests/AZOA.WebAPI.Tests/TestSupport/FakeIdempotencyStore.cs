using System.Collections.Concurrent;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Idempotency;

namespace AZOA.WebAPI.Tests.TestSupport;

/// <summary>
/// Faithful thread-safe in-process <see cref="IIdempotencyStore"/> mirroring
/// the production UNIQUE-key INSERT-WINS contract without a database.
///
/// <see cref="TryClaimAsync"/> uses the atomic factory-free
/// <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/> with a pre-built
/// candidate: under N racing callers exactly one TryAdd returns true (the
/// single INSERT winner ⇒ Won=true) and every other re-reads the stored winner
/// (Won=false). TryAdd, NOT GetOrAdd — GetOrAdd's value factory can run on
/// multiple racing threads, each wrongly believing it created the entry.
/// </summary>
public sealed class FakeIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, IdempotencyRecord> _records = new();

    public int RecordCount => _records.Count;
    public IEnumerable<string> Keys => _records.Keys;

    public Task<IdempotencyClaim> TryClaimAsync(string key, string operationType, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Idempotency key must be non-empty.", nameof(key));

        var now = DateTime.UtcNow;
        var mine = new IdempotencyRecord
        {
            Key = key,
            OperationType = operationType,
            State = IdempotencyState.InProgress,
            CreatedAt = now,
            UpdatedAt = now
        };

        var won = _records.TryAdd(key, mine);
        var stored = _records.TryGetValue(key, out var rec) ? rec : mine;
        return Task.FromResult(new IdempotencyClaim(won, stored));
    }

    public Task CompleteAsync(string key, string resultPayload, CancellationToken ct)
    {
        if (!_records.TryGetValue(key, out var rec))
            throw new InvalidOperationException($"No idempotency claim exists for key '{key}'.");
        lock (rec)
        {
            rec.State = IdempotencyState.Completed;
            rec.ResultPayload = resultPayload;
            rec.Error = null;
            rec.UpdatedAt = DateTime.UtcNow;
        }
        return Task.CompletedTask;
    }

    public Task FailAsync(string key, string error, CancellationToken ct)
    {
        if (!_records.TryGetValue(key, out var rec))
            throw new InvalidOperationException($"No idempotency claim exists for key '{key}'.");
        lock (rec)
        {
            rec.State = IdempotencyState.Failed;
            rec.Error = error;
            rec.UpdatedAt = DateTime.UtcNow;
        }
        return Task.CompletedTask;
    }

    public Task<IdempotencyRecord?> GetAsync(string key, CancellationToken ct)
        => Task.FromResult(_records.TryGetValue(key, out var rec) ? rec : null);

    /// <summary>Pre-seed a terminal record (e.g. a prior Completed dispense) so
    /// a fresh claim returns Won=false + this row.</summary>
    public void Seed(string key, string operationType, IdempotencyState state,
        string? resultPayload = null, string? error = null)
    {
        _records[key] = new IdempotencyRecord
        {
            Key = key,
            OperationType = operationType,
            State = state,
            ResultPayload = resultPayload,
            Error = error,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Seed a record with an explicit <paramref name="createdAt"/> so stale-claim
    /// tests can place the claim in the past without sleeping.
    /// </summary>
    public void SeedAged(string key, string operationType, IdempotencyState state,
        DateTime createdAt, string? resultPayload = null, string? error = null)
    {
        _records[key] = new IdempotencyRecord
        {
            Key = key,
            OperationType = operationType,
            State = state,
            ResultPayload = resultPayload,
            Error = error,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
    }
}
