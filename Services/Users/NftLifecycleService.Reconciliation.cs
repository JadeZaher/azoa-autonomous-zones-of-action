using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Users;

public sealed partial class NftLifecycleService : INftLifecycleReconciler
{
    /// <inheritdoc/>
    public async Task<AZOAResult<NftLifecycleView>> ReconcileAsync(string operationId, CancellationToken ct = default)
    {
        var row = await store.GetAsync(operationId, ct);
        if (row is null) return Refused("NFT operation not found.");
        if (row.State is not ("Submitted" or "NeedsOperator" or "Confirmed"))
            return AZOAResult<NftLifecycleView>.Success(View(row));
        row.UpdatedAt = clock.GetUtcNow();
        var claimed = await store.UpdateAsync(row, row.Version, ct);
        if (claimed is null) return AZOAResult<NftLifecycleView>.Success(View(await ReloadAsync(operationId, ct)));
        return await AdvanceAsync(claimed, Guid.Empty, false, ct, Guid.Empty, allowPreparation: false);
    }
}
