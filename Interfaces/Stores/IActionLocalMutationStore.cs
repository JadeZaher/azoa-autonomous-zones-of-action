using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Commits a validated local mutation plan and its immutable command receipt atomically.</summary>
public interface IActionLocalMutationStore
{
    /// <summary>Reads an existing effect receipt without re-executing its mutation.</summary>
    Task<ActionLocalReceipt?> GetReceiptAsync(string commandId, CancellationToken ct = default);

    /// <summary>Requires current action authority, policy version, unchanged rows and query membership before all writes.</summary>
    Task<AZOAResult<ActionLocalReceipt>> CommitAsync(ActionLocalMutationPlan plan, ActionLocalReceipt receipt,
        long? expectedPolicyVersion, bool requiresWalletScope, CancellationToken ct = default);
}
