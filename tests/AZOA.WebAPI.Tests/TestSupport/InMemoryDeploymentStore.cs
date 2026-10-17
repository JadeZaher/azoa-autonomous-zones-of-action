using System.Text.Json;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Tests.TestSupport;

internal sealed class InMemoryDeploymentStore : IDeploymentStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DeploymentArtifact> _artifacts = [];
    private readonly Dictionary<string, NativeQuote> _quotes = [];
    private readonly Dictionary<string, DeploymentOperation> _operations = [];
    internal bool ActionActive { get; set; } = true;
    internal bool LoseNextAdmissionResponse { get; set; }
    /// <summary>Simulates the signing-authority fence throwing inside the update transaction.</summary>
    internal bool SigningRevoked { get; set; }
    internal List<IReadOnlyList<SigningAdmissionAuthority>> FencedSigning { get; } = [];
    internal IReadOnlyList<NativeQuote> Quotes { get { lock (_gate) return _quotes.Values.Select(Copy).ToArray(); } }
    internal IReadOnlyList<DeploymentOperation> Operations { get { lock (_gate) return _operations.Values.Select(Copy).ToArray(); } }
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    public Task<DeploymentArtifact> AddArtifactAsync(DeploymentArtifact artifact, CancellationToken ct = default)
    { lock (_gate) { _artifacts.TryAdd(artifact.Id, Copy(artifact)); return Task.FromResult(Copy(_artifacts[artifact.Id])); } }
    public Task<DeploymentArtifact?> GetArtifactAsync(string id, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult(_artifacts.TryGetValue(id, out var value) ? Copy(value) : null); }
    public Task AddQuoteAsync(NativeQuote quote, CancellationToken ct = default)
    { lock (_gate) _quotes.Add(quote.Id, Copy(quote)); return Task.CompletedTask; }
    public Task<NativeQuote> ClaimQuoteAsync(NativeQuote quote, CancellationToken ct = default)
    { lock (_gate) { _quotes.TryAdd(quote.Id, Copy(quote)); return Task.FromResult(Copy(_quotes[quote.Id])); } }
    public Task<NativeQuote?> GetQuoteAsync(string id, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult(_quotes.TryGetValue(id, out var value) ? Copy(value) : null); }
    public Task<DeploymentOperation?> ClaimAsync(DeploymentOperation candidate, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (candidate.ActionRunId is not null && !ActionActive) return Task.FromResult<DeploymentOperation?>(null);
            _operations.TryAdd(candidate.Id, Copy(candidate));
            if (LoseNextAdmissionResponse) { LoseNextAdmissionResponse = false; throw new IOException("Admission response lost after commit."); }
            return Task.FromResult<DeploymentOperation?>(Copy(_operations[candidate.Id]));
        }
    }
    public Task<DeploymentOperation?> GetAsync(string id, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult(_operations.TryGetValue(id, out var value) ? Copy(value) : null); }
    public Task<DeploymentOperation?> ClaimRecoveryChildAsync(DeploymentOperation parent, DeploymentOperation child, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_operations.TryGetValue(parent.Id, out var current)) return Task.FromResult<DeploymentOperation?>(null);
            if (current.RecoveryChildId == child.Id && _operations.TryGetValue(child.Id, out var existing)
                && existing.RequestJson == child.RequestJson && existing.IntentHash == child.IntentHash
                && existing.RecoveryBindingHash == child.RecoveryBindingHash)
                return Task.FromResult<DeploymentOperation?>(Copy(existing));
            if (current.Version != parent.Version || current.Generation != parent.Generation || current.HistoryJson != parent.HistoryJson
                || current.RequestJson != parent.RequestJson || current.SubmissionAuthorized || current.PreparedJson is not null
                || current.RecoveryChildId is not null || current.ParentOperationId is not null || _operations.ContainsKey(child.Id)
                || child.ParentOperationId != current.Id || child.ActorUserId != current.ActorUserId || child.TenantId != current.TenantId)
                return Task.FromResult<DeploymentOperation?>(null);
            current.RecoveryChildId = child.Id; current.RecoveryMode = child.RecoveryMode;
            current.RecoveryBindingHash = child.RecoveryBindingHash; current.Version++;
            _operations.Add(child.Id, Copy(child));
            return Task.FromResult<DeploymentOperation?>(Copy(child));
        }
    }
    public Task<DeploymentOperation?> UpdateAsync(DeploymentOperation row, long expectedVersion, CancellationToken ct = default, bool requireActionAuthority = false,
        IReadOnlyList<SigningAdmissionAuthority>? signing = null)
    {
        lock (_gate)
        {
            if (signing is not null)
            {
                FencedSigning.Add(signing);
                if (SigningRevoked) return Task.FromResult<DeploymentOperation?>(null);
            }
            if (!_operations.TryGetValue(row.Id, out var current) || current.Version != expectedVersion || current.RecoveryChildId is not null || current.RecoveryProjected || current.ReplacedByOperationId is not null
                || current.ParentOperationId != row.ParentOperationId || current.RecoveryBindingHash != row.RecoveryBindingHash
                || requireActionAuthority && row.ActionRunId is not null && !ActionActive) return Task.FromResult<DeploymentOperation?>(null);
            if (row.ParentOperationId is not null && (!_operations.TryGetValue(row.ParentOperationId, out var parent)
                || parent.RecoveryChildId != row.Id || parent.RecoveryBindingHash != row.RecoveryBindingHash
                || parent.ActorUserId != row.ActorUserId || parent.TenantId != row.TenantId
                || parent.SubmissionAuthorized || parent.PreparedJson is not null)) return Task.FromResult<DeploymentOperation?>(null);
            var saved = Copy(row); saved.Version++; saved.UpdatedAt = DateTimeOffset.UtcNow;
            _operations[row.Id] = saved;
            return Task.FromResult<DeploymentOperation?>(Copy(saved));
        }
    }
    public Task<DeploymentOperation?> ReplaceRecoveryChildAsync(DeploymentOperation parent, DeploymentOperation predecessor,
        DeploymentOperation replacement, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_operations.TryGetValue(parent.Id, out var current) || !_operations.TryGetValue(predecessor.Id, out var old))
                return Task.FromResult<DeploymentOperation?>(null);
            if (_operations.TryGetValue(replacement.Id, out var existing))
                return Task.FromResult(existing.ParentOperationId == parent.Id && existing.ReplacesOperationId == predecessor.Id
                    && existing.RequestJson == replacement.RequestJson && existing.IntentHash == replacement.IntentHash
                    && existing.RecoveryBindingHash == replacement.RecoveryBindingHash ? Copy(existing) : null);
            if (current.Version != parent.Version || current.RecoveryChildId != old.Id || current.RecoveryBindingHash != old.RecoveryBindingHash
                || current.HistoryJson != parent.HistoryJson || current.RequestJson != parent.RequestJson
                || current.PreparedJson is not null || current.SubmissionAuthorized || current.ParentOperationId is not null
                || old.Version != predecessor.Version || old.RequestJson != predecessor.RequestJson || old.HistoryJson != predecessor.HistoryJson
                || old.PreparedJson != predecessor.PreparedJson || old.State != predecessor.State || old.Generation != predecessor.Generation
                || AZOA.WebAPI.Helpers.Deployments.NativeRecoveryReplacement.ProofKind(old) is null
                || replacement.ParentOperationId != parent.Id || replacement.ReplacesOperationId != old.Id
                || replacement.ActorUserId != current.ActorUserId || replacement.TenantId != current.TenantId
                || old.ParentOperationId != current.Id || old.ActorUserId != current.ActorUserId || old.TenantId != current.TenantId
                || replacement.ArtifactId != current.ArtifactId || replacement.ArtifactHash != current.ArtifactHash
                || old.ArtifactId != current.ArtifactId || old.ArtifactHash != current.ArtifactHash
                || replacement.State != "Admitted" || replacement.Generation != 0 || replacement.HistoryJson != "[]"
                || replacement.PreparedJson is not null || replacement.SubmissionAuthorized || replacement.RecoveryProjected
                || replacement.ReplacedByOperationId is not null || replacement.RecoveryChildId is not null || replacement.ActionRunId is not null
                || replacement.Id == parent.Id || replacement.Id == old.Id || string.IsNullOrWhiteSpace(replacement.RecoveryBindingHash)
                || replacement.RecoveryMode is not ("AbortCleanup" or "ContinueCleanup" or "FinalizeAuthority"))
                return Task.FromResult<DeploymentOperation?>(null);
            var quote = JsonSerializer.Deserialize<AZOA.WebAPI.Models.Deployments.DeploymentStartRequest>(replacement.RequestJson)?.NativeIntent?.Quote;
            if (quote is null || !_quotes.TryGetValue(quote.QuoteId, out var savedQuote) || savedQuote.ExpiresAt <= DateTimeOffset.UtcNow
                || savedQuote.ActorUserId != current.ActorUserId || savedQuote.TenantId != current.TenantId
                || savedQuote.IntentHash != quote.IntentHash || savedQuote.QuoteJson != JsonSerializer.Serialize(quote)) return Task.FromResult<DeploymentOperation?>(null);
            old.ReplacedByOperationId = replacement.Id; old.Version++;
            current.RecoveryChildId = replacement.Id; current.RecoveryBindingHash = replacement.RecoveryBindingHash;
            current.RecoveryMode = replacement.RecoveryMode; current.Version++;
            _operations.Add(replacement.Id, Copy(replacement));
            return Task.FromResult<DeploymentOperation?>(Copy(replacement));
        }
    }
    public Task<DeploymentOperation?> InvalidateReplacementQuoteAsync(DeploymentOperation parent, DeploymentOperation predecessor, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_operations.TryGetValue(parent.Id, out var current) || !_operations.TryGetValue(predecessor.Id, out var old)
                || current.Version != parent.Version || current.Generation != parent.Generation
                || current.RequestJson != parent.RequestJson || current.HistoryJson != parent.HistoryJson
                || current.ParentOperationId is not null || current.RecoveryChildId != old.Id || current.RecoveryBindingHash != old.RecoveryBindingHash
                || current.PreparedJson is not null || current.SubmissionAuthorized
                || old.ParentOperationId != current.Id || old.ActorUserId != current.ActorUserId || old.TenantId != current.TenantId
                || old.Version != predecessor.Version || old.RequestJson != predecessor.RequestJson || old.HistoryJson != predecessor.HistoryJson
                || old.State != predecessor.State || old.Generation != predecessor.Generation || old.PreparedJson != predecessor.PreparedJson
                || AZOA.WebAPI.Helpers.Deployments.NativeRecoveryReplacement.ProofKind(old) is null)
                return Task.FromResult<DeploymentOperation?>(null);
            current.Version++; current.Stage = "RecoveryQuoteInvalidated"; current.UpdatedAt = DateTimeOffset.UtcNow;
            return Task.FromResult<DeploymentOperation?>(Copy(current));
        }
    }
    public Task<DeploymentOperation?> CompleteRecoveryChildAsync(string childId, long expectedChildVersion, long expectedParentVersion, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_operations.TryGetValue(childId, out var child) || child.ParentOperationId is null
                || !_operations.TryGetValue(child.ParentOperationId, out var parent)
                || parent.RecoveryChildId != child.Id || parent.RecoveryBindingHash != child.RecoveryBindingHash)
                return Task.FromResult<DeploymentOperation?>(null);
            if (child.RecoveryProjected) return Task.FromResult<DeploymentOperation?>(Copy(parent));
            if (child.Version != expectedChildVersion || parent.Version != expectedParentVersion || child.State != "Confirmed"
                || child.SubmissionAuthorized || child.PreparedJson is not null || string.IsNullOrWhiteSpace(child.EvidenceJson))
                return Task.FromResult<DeploymentOperation?>(null);
            child.RecoveryProjected = true; child.Version++;
            parent.State = child.RecoveryMode switch { "AbortCleanup" => "AbortedClean", "ContinueCleanup" => "FailedClean",
                "FinalizeAuthority" => "Confirmed", _ => throw new InvalidOperationException("Unknown recovery mode.") };
            parent.Stage = "RecoveryComplete"; parent.Version++;
            parent.RecoveryEvidenceJson = JsonSerializer.Serialize(new { child.Id, child.IntentHash, child.RecoveryBindingHash,
                child.EvidenceJson, child.HistoryJson, child.ResourceId, child.RecoveryMode });
            return Task.FromResult<DeploymentOperation?>(Copy(parent));
        }
    }
    public Task<IReadOnlyList<DeploymentOperation>> ListActorAsync(string actorId, string tenantId, DateTimeOffset snapshot, int offset, int limit, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult<IReadOnlyList<DeploymentOperation>>(_operations.Values.Where(row => row.ActorUserId == actorId && row.TenantId == tenantId && row.CreatedAt <= snapshot)
        .OrderByDescending(row => row.CreatedAt).ThenBy(row => row.Id).Skip(offset).Take(limit).Select(Copy).ToArray()); }
    public Task<IReadOnlyList<DeploymentOperation>> ListNeedsOperatorAsync(int limit, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult<IReadOnlyList<DeploymentOperation>>(_operations.Values.Where(row => row.State == "NeedsOperator")
        .OrderBy(row => row.UpdatedAt).ThenBy(row => row.Id).Take(limit).Select(Copy).ToArray()); }
    public Task<IReadOnlyList<DeploymentOperation>> ListPendingAsync(int limit, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult<IReadOnlyList<DeploymentOperation>>(_operations.Values.Where(row => (row.SubmissionAuthorized && row.State is "Submitted" or "NeedsOperator")
        || (row.ParentOperationId is not null && row.State == "Confirmed" && !row.RecoveryProjected))
        .OrderBy(row => row.UpdatedAt).Take(limit).Select(Copy).ToArray()); }
}
