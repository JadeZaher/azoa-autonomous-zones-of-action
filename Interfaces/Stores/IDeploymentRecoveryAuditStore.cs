using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Durable bounded-page verification of sealed deployment recovery ancestry.</summary>
public interface IDeploymentRecoveryAuditStore
{
    Task<DeploymentRecoveryAudit> ClaimAsync(DeploymentOperation parent, DeploymentOperation head, CancellationToken ct = default);
    Task<DeploymentRecoveryAudit?> GetAsync(string id, CancellationToken ct = default);
    Task<DeploymentRecoveryAudit?> AdvanceAsync(string id, long expectedVersion, CancellationToken ct = default);
}
