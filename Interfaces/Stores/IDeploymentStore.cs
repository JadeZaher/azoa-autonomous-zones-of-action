using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Immutable artifact/admission claims and version-fenced stage changes.</summary>
public interface IDeploymentStore
{
    Task<DeploymentArtifact> AddArtifactAsync(DeploymentArtifact artifact, CancellationToken ct = default);
    Task<DeploymentArtifact?> GetArtifactAsync(string id, CancellationToken ct = default);
    Task AddQuoteAsync(NativeQuote quote, CancellationToken ct = default);
    Task<NativeQuote> ClaimQuoteAsync(NativeQuote quote, CancellationToken ct = default);
    Task<NativeQuote?> GetQuoteAsync(string id, CancellationToken ct = default);
    Task<DeploymentOperation?> ClaimAsync(DeploymentOperation candidate, CancellationToken ct = default);
    Task<DeploymentOperation?> GetAsync(string id, CancellationToken ct = default);
    /// <summary>Atomically links one recovery child after the parent's authorized wires are resolved.</summary>
    Task<DeploymentOperation?> ClaimRecoveryChildAsync(DeploymentOperation parent, DeploymentOperation child, CancellationToken ct = default);
    /// <summary>Projects a confirmed recovery child's immutable evidence into its exclusively linked parent.</summary>
    Task<DeploymentOperation?> CompleteRecoveryChildAsync(string childId, long expectedChildVersion, long expectedParentVersion, CancellationToken ct = default);
    /// <summary>Seals the exact eligible predecessor and atomically swaps its parent link to a newly quoted child.</summary>
    Task<DeploymentOperation?> ReplaceRecoveryChildAsync(DeploymentOperation parent, DeploymentOperation predecessor,
        DeploymentOperation replacement, CancellationToken ct = default);
    /// <summary>Advances a quoted parent's admission version while preserving and fencing its exact current child.</summary>
    Task<DeploymentOperation?> InvalidateReplacementQuoteAsync(DeploymentOperation parent, DeploymentOperation predecessor, CancellationToken ct = default);
    Task<DeploymentOperation?> UpdateAsync(DeploymentOperation row, long expectedVersion, CancellationToken ct = default, bool requireActionAuthority = false);
    Task<IReadOnlyList<DeploymentOperation>> ListActorAsync(string actorId, string tenantId, DateTimeOffset snapshot, int offset, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<DeploymentOperation>> ListPendingAsync(int limit, CancellationToken ct = default);
}
