using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Durable one-time binding for the configured initial node governor.</summary>
public interface IAdminBootstrapStateStore
{
    /// <summary>Loads the local bootstrap binding, if it exists.</summary>
    Task<AZOAResult<AdminBootstrapState?>> GetAsync(CancellationToken ct = default);

    /// <summary>Creates the local binding once; an existing binding is returned for comparison.</summary>
    Task<AZOAResult<AdminBootstrapState>> BindOnceAsync(
        AdminBootstrapState state,
        CancellationToken ct = default);

    /// <summary>Atomically rotates the bound operator avatar hash and monotonic revision.</summary>
    Task<AZOAResult<AdminBootstrapState>> RotateCredentialsAsync(
        Guid avatarId,
        string username,
        string passwordHash,
        long expectedRevision,
        long nextRevision,
        DateTimeOffset changedAt,
        CancellationToken ct = default);

    /// <summary>Atomically advances only the reserved operator session watermark.</summary>
    Task<AZOAResult<long>> AdvanceSessionWatermarkAsync(
        long expectedCredentialRevision,
        long expectedSessionRevision,
        DateTimeOffset changedAt,
        CancellationToken ct = default)
        => Task.FromResult(AZOAResult<long>.Failure(
            "Node operator session revocation is not supported by this store."));
}
