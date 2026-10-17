using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Governance;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>Stages manager writes and captures original read witnesses without mutating storage.</summary>
public sealed class ActionLocalHolonOverlay(IHolonStore source, ActionLocalMutationPlan plan) : IHolonStore
{
    /// <inheritdoc/>
    public async Task<AZOAResult<IHolon>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        if (plan.HolonWrites.TryGetValue(id, out var staged)) return Read(staged);
        if (plan.HolonReads.TryGetValue(id, out var cached)) return Read(cached);
        var read = await source.GetByIdAsync(id, ct);
        if (read.IsError && read.Message != "Holon not found.")
        {
            plan.Reject(read.Message);
            return read;
        }
        plan.HolonReads[id] = read.Result is null ? null : ActionLocalSnapshot.Copy(read.Result);
        Check(read.Result);
        return plan.IsWithinBounds() ? Read(plan.HolonReads[id]) : AZOAResult<IHolon>.Failure(plan.Refusal!);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<IHolon>>> QueryAsync(HolonQueryRequest? query = null, CancellationToken ct = default)
    {
        var read = await source.QueryAsync(query, ct);
        if (read.IsError || read.Result is null)
        {
            plan.Reject(read.Message);
            return read;
        }
        var rows = read.Result.ToArray();
        if (rows.Length > ActionLocalMutationPlan.MaximumRows)
            plan.Reject("The local action query exceeds the atomic read limit.");
        plan.HolonQueries.Add(new(CloneQuery(query), rows.Select(row => row.Id).Order().ToArray()));
        foreach (var row in rows.Take(ActionLocalMutationPlan.MaximumRows + 1))
        {
            Check(row);
            plan.HolonReads.TryAdd(row.Id, ActionLocalSnapshot.Copy(row));
        }
        if (!plan.IsWithinBounds()) return AZOAResult<IEnumerable<IHolon>>.Failure(plan.Refusal!);
        var merged = rows.ToDictionary(row => row.Id, ActionLocalSnapshot.Copy);
        foreach (var (id, row) in plan.HolonWrites)
        {
            merged.Remove(id);
            if (row is not null && Matches(row, query)) merged[id] = ActionLocalSnapshot.Copy(row);
        }
        return AZOAResult<IEnumerable<IHolon>>.Success(merged.Values.OrderBy(row => row.Id).Cast<IHolon>().ToArray());
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IHolon>> UpsertAsync(IHolon holon, CancellationToken ct = default)
    {
        if (!plan.HolonReads.ContainsKey(holon.Id)) await GetByIdAsync(holon.Id, ct);
        Check(holon);
        if (!plan.IsWithinBounds()) return AZOAResult<IHolon>.Failure(plan.Refusal!);
        plan.HolonWrites[holon.Id] = ActionLocalSnapshot.Copy(holon);
        return AZOAResult<IHolon>.Success(ActionLocalSnapshot.Copy(holon));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var existing = await GetByIdAsync(id, ct);
        if (existing.IsError || existing.Result is null) return AZOAResult<bool>.Failure(existing.Message);
        if (!plan.IsWithinBounds()) return AZOAResult<bool>.Failure(plan.Refusal!);
        plan.HolonWrites[id] = null;
        return AZOAResult<bool>.Success(true);
    }

    /// <inheritdoc/>
    public Task<AZOAResult<bool>> TryReserveNftTransferAsync(Guid holonId, Guid sourceUserId, Guid targetUserId,
        string settlementKey, CancellationToken ct = default) => RefuseValueMutation();

    /// <inheritdoc/>
    public Task<AZOAResult<bool>> FinalizeReservedNftTransferAsync(Guid holonId, Guid sourceUserId, Guid targetUserId,
        string settlementKey, CancellationToken ct = default) => RefuseValueMutation();

    /// <inheritdoc/>
    public Task<AZOAResult<bool>> ReleaseNftTransferReservationAsync(Guid holonId, Guid sourceUserId,
        string settlementKey, CancellationToken ct = default) => RefuseValueMutation();

    private Task<AZOAResult<bool>> RefuseValueMutation()
    {
        plan.Reject("A local action mutation cannot perform value settlement.", deterministic: true);
        return Task.FromResult(AZOAResult<bool>.Failure(plan.Refusal!));
    }

    private void Check(IHolon? holon)
    {
        if (holon?.AssetType == ConsumerGovernanceHolonTypes.ProposalAssetType)
            plan.Reject("Election authority cannot participate in a general holon mutation plan.", deterministic: true);
    }

    private static AZOAResult<IHolon> Read(Holon? row) => row is null
        ? AZOAResult<IHolon>.Failure("Holon not found.") : AZOAResult<IHolon>.Success(ActionLocalSnapshot.Copy(row));

    private static HolonQueryRequest? CloneQuery(HolonQueryRequest? query) => query is null ? null : new()
    {
        Name = query.Name, UserId = query.UserId, ProviderName = query.ProviderName,
        ChainId = query.ChainId, AssetType = query.AssetType, IsActive = query.IsActive, ParentHolonId = query.ParentHolonId
    };

    public static bool Matches(IHolon row, HolonQueryRequest? query) => query is null
        || ((!query.UserId.HasValue || row.UserId == query.UserId)
            && (string.IsNullOrEmpty(query.Name) || row.Name.Contains(query.Name, StringComparison.Ordinal))
            && (string.IsNullOrEmpty(query.ProviderName) || row.ProviderName == query.ProviderName)
            && (string.IsNullOrEmpty(query.ChainId) || row.ChainId == query.ChainId)
            && (string.IsNullOrEmpty(query.AssetType) || row.AssetType == query.AssetType)
            && (!query.IsActive.HasValue || row.IsActive == query.IsActive)
            && (!query.ParentHolonId.HasValue || row.ParentHolonId == query.ParentHolonId));
}
