using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>Stages local wallet metadata mutations with original read and membership witnesses.</summary>
public sealed class ActionLocalWalletOverlay(IWalletStore source, ActionLocalMutationPlan plan) : IWalletStore
{
    /// <inheritdoc/>
    public Task<AZOAResult<IWallet>> BootstrapTenantAsync(IWallet wallet, string externalUserId,
        AZOA.WebAPI.Models.Signing.SigningConsentWitness authority, CancellationToken ct = default)
    {
        plan.Reject("Tenant custody cannot be staged as a local wallet metadata mutation.");
        return Task.FromResult(AZOAResult<IWallet>.Failure(plan.Refusal!));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IWallet>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        if (plan.WalletWrites.TryGetValue(id, out var staged)) return Read(staged);
        if (plan.WalletReads.TryGetValue(id, out var cached)) return Read(cached);
        var read = await source.GetByIdAsync(id, ct);
        if (read.IsError && read.Message != "Wallet not found.")
        {
            plan.Reject(read.Message);
            return read;
        }
        plan.WalletReads[id] = read.Result is null ? null : ActionLocalSnapshot.Copy(read.Result);
        return plan.IsWithinBounds() ? Read(plan.WalletReads[id]) : AZOAResult<IWallet>.Failure(plan.Refusal!);
    }

    /// <inheritdoc/>
    public Task<AZOAResult<IEnumerable<IWallet>>> GetAllAsync(CancellationToken ct = default) => QueryAsync(null, ct);

    /// <inheritdoc/>
    public Task<AZOAResult<IEnumerable<IWallet>>> GetByUserAsync(Guid userId, CancellationToken ct = default) => QueryAsync(userId, ct);

    private async Task<AZOAResult<IEnumerable<IWallet>>> QueryAsync(Guid? owner, CancellationToken ct)
    {
        var read = owner.HasValue ? await source.GetByUserAsync(owner.Value, ct) : await source.GetAllAsync(ct);
        if (read.IsError || read.Result is null)
        {
            plan.Reject(read.Message);
            return read;
        }
        var rows = read.Result.ToArray();
        if (rows.Length > ActionLocalMutationPlan.MaximumRows) plan.Reject("The local wallet query exceeds the atomic read limit.");
        plan.WalletQueries.Add(new(owner, rows.Select(row => row.Id).Order().ToArray()));
        foreach (var row in rows.Take(ActionLocalMutationPlan.MaximumRows + 1))
            plan.WalletReads.TryAdd(row.Id, ActionLocalSnapshot.Copy(row));
        if (!plan.IsWithinBounds()) return AZOAResult<IEnumerable<IWallet>>.Failure(plan.Refusal!);
        var merged = rows.ToDictionary(row => row.Id, ActionLocalSnapshot.Copy);
        foreach (var (id, row) in plan.WalletWrites)
        {
            merged.Remove(id);
            if (row is not null && (!owner.HasValue || row.UserId == owner)) merged[id] = ActionLocalSnapshot.Copy(row);
        }
        return AZOAResult<IEnumerable<IWallet>>.Success(merged.Values.OrderBy(row => row.Id).Cast<IWallet>().ToArray());
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IWallet>> UpsertAsync(IWallet wallet, CancellationToken ct = default)
    {
        if (!plan.WalletReads.ContainsKey(wallet.Id)) await GetByIdAsync(wallet.Id, ct);
        if (!plan.IsWithinBounds()) return AZOAResult<IWallet>.Failure(plan.Refusal!);
        plan.WalletWrites[wallet.Id] = ActionLocalSnapshot.Copy(wallet);
        return AZOAResult<IWallet>.Success(ActionLocalSnapshot.Copy(wallet));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IWallet>> CreateIfAbsentAsync(IWallet wallet, CancellationToken ct = default)
    {
        var existing = await GetByIdAsync(wallet.Id, ct);
        return existing.Result is not null ? existing : await UpsertAsync(wallet, ct);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var existing = await GetByIdAsync(id, ct);
        if (existing.IsError || existing.Result is null) return AZOAResult<bool>.Failure(existing.Message);
        if (!plan.IsWithinBounds()) return AZOAResult<bool>.Failure(plan.Refusal!);
        plan.WalletWrites[id] = null;
        return AZOAResult<bool>.Success(true);
    }

    private static AZOAResult<IWallet> Read(Wallet? row) => row is null
        ? AZOAResult<IWallet>.Failure("Wallet not found.") : AZOAResult<IWallet>.Success(ActionLocalSnapshot.Copy(row));
}
