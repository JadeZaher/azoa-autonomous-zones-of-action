using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Tests.TestSupport;

/// <summary>Isolated durable-store semantics for Ethereum custody tests.</summary>
public sealed class FakeEthereumPreparationStore : IEthereumPreparationStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, EthereumPreparation> _rows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EthereumSignerCheckpoint> _checkpoints = new(StringComparer.Ordinal);

    public IReadOnlyList<EthereumPreparation> Snapshot
    {
        get { lock (_gate) return _rows.Values.Select(Clone).ToArray(); }
    }

    public void SeedCheckpoint(string protocol, string sender, string epoch, string initialNonce = "0")
    {
        sender = sender.ToLowerInvariant();
        var checkpoint = new EthereumSignerCheckpoint
        {
            Id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(protocol + "|" + sender))),
            ProtocolIdentity = protocol, Sender = sender, ActivationEpoch = epoch, InitialNonce = initialNonce,
            BlockHash = "0x" + new string('b', 64), ActorUserId = "fixture", CreatedAt = DateTimeOffset.UtcNow
        };
        lock (_gate) _checkpoints.TryAdd(checkpoint.Id, Clone(checkpoint));
    }

    /// <inheritdoc/>
    public Task<EthereumPreparation?> GetAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(_rows.TryGetValue(id, out var row) ? Clone(row) : null);
    }

    /// <inheritdoc/>
    public Task<EthereumPreparation?> GetHighestNonceAsync(string protocol, string sender, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var row = _rows.Values.Where(row => row.ProtocolIdentity == protocol && row.Sender == sender)
                .OrderByDescending(row => row.Nonce, StringComparer.Ordinal).FirstOrDefault();
            return Task.FromResult(row is null ? null : Clone(row));
        }
    }

    /// <inheritdoc/>
    public Task<EthereumPreparation?> TryClaimAsync(EthereumPreparation candidate, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_rows.TryGetValue(candidate.Id, out var existing)) return Task.FromResult<EthereumPreparation?>(Clone(existing));
            if (_rows.Values.Any(row => row.ProtocolIdentity == candidate.ProtocolIdentity && row.Sender == candidate.Sender && row.Nonce == candidate.Nonce))
                return Task.FromResult<EthereumPreparation?>(null);
            _rows.Add(candidate.Id, Clone(candidate));
            return Task.FromResult<EthereumPreparation?>(Clone(candidate));
        }
    }

    /// <inheritdoc/>
    public Task<EthereumPreparation?> UpdateAsync(EthereumPreparation row, long expectedVersion, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_rows.TryGetValue(row.Id, out var existing) || existing.Version != expectedVersion)
                return Task.FromResult<EthereumPreparation?>(null);
            existing.State = row.State;
            existing.RecoveryCandidatesJson = row.RecoveryCandidatesJson;
            existing.RecoveryAuditJson = row.RecoveryAuditJson;
            existing.Version = expectedVersion + 1;
            existing.UpdatedAt = row.UpdatedAt;
            existing.SignedTransactionBase64 = row.SignedTransactionBase64;
            existing.TransactionId = row.TransactionId;
            return Task.FromResult<EthereumPreparation?>(Clone(existing));
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<EthereumPreparation>> ListOwnerAsync(string owner, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult<IReadOnlyList<EthereumPreparation>>(_rows.Values.Where(row => row.OwnerUserId == owner)
            .OrderByDescending(row => row.CreatedAt).Take(Math.Clamp(limit, 1, 100)).Select(Clone).ToArray());
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<EthereumPreparation>> ListAuthorizedAsync(string network, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult<IReadOnlyList<EthereumPreparation>>(_rows.Values.Where(row => row.Network == network && row.State == "SubmissionAuthorized")
            .OrderBy(row => row.UpdatedAt).Take(50).Select(Clone).ToArray());
    }

    /// <inheritdoc/>
    public Task<EthereumSignerCheckpoint?> GetCheckpointAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(_checkpoints.TryGetValue(id, out var row) ? Clone(row) : null);
    }

    /// <inheritdoc/>
    public Task<EthereumSignerCheckpoint> ClaimCheckpointAsync(EthereumSignerCheckpoint candidate, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _checkpoints.TryAdd(candidate.Id, Clone(candidate));
            return Task.FromResult(Clone(_checkpoints[candidate.Id]));
        }
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
