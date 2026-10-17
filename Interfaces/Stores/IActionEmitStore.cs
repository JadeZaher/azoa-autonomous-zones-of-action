using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Commits immutable Emit acceptance or deterministic rejection against its frozen action.</summary>
public interface IActionEmitStore
{
    /// <summary>Reads an immutable receipt; callers enforce original actor and tenant scope.</summary>
    Task<ActionEmitReceipt?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Verifies that a tenant receipt still has its exact immutable outbox event, independent of delivery state.</summary>
    Task<bool> HasAcceptedEventAsync(ActionEmitReceipt receipt, CancellationToken ct = default);

    /// <summary>Replays exact input or atomically accepts under current original workflow authority; null is unaccepted.</summary>
    Task<ActionEmitReceipt?> AcceptAsync(string canonicalInput, WorkflowSigningWitness authority, CancellationToken ct = default);
}
