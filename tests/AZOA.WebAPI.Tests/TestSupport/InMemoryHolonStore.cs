using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.TestSupport;

/// <summary>Copy-on-read in-memory <see cref="IHolonStore"/> for manager/controller flow tests.</summary>
public sealed class InMemoryHolonStore : IHolonStore
{
    private readonly Dictionary<Guid, Holon> _rows = new();

    /// <summary>The stored row, copied, or null.</summary>
    public Holon? Row(Guid id) => _rows.TryGetValue(id, out var row) ? Copy(row) : null;

    /// <summary>Every stored row, copied.</summary>
    public IReadOnlyList<Holon> Rows => _rows.Values.Select(Copy).ToList();

    public Holon Seed(Holon holon)
    {
        _rows[holon.Id] = Copy(holon);
        return holon;
    }

    public Task<AZOAResult<IHolon>> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_rows.TryGetValue(id, out var row)
            ? AZOAResult<IHolon>.Success(Copy(row))
            : AZOAResult<IHolon>.Failure("Holon not found."));

    public Task<AZOAResult<IEnumerable<IHolon>>> QueryAsync(HolonQueryRequest? query = null, CancellationToken ct = default)
    {
        IEnumerable<Holon> rows = _rows.Values;
        if (query?.ParentHolonId is { } parentId)
            rows = rows.Where(row => row.ParentHolonId == parentId);
        return Task.FromResult(AZOAResult<IEnumerable<IHolon>>.Success(rows.Select(Copy).Cast<IHolon>().ToList()));
    }

    public Task<AZOAResult<IHolon>> UpsertAsync(IHolon holon, CancellationToken ct = default)
    {
        _rows[holon.Id] = Copy((Holon)holon);
        return Task.FromResult(AZOAResult<IHolon>.Success(Copy((Holon)holon)));
    }

    public Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(AZOAResult<bool>.Success(_rows.Remove(id)));

    public Task<AZOAResult<bool>> TryReserveNftTransferAsync(Guid holonId, Guid sourceUserId, Guid targetUserId, string settlementKey, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<AZOAResult<bool>> FinalizeReservedNftTransferAsync(Guid holonId, Guid sourceUserId, Guid targetUserId, string settlementKey, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<AZOAResult<bool>> ReleaseNftTransferReservationAsync(Guid holonId, Guid sourceUserId, string settlementKey, CancellationToken ct = default) =>
        throw new NotSupportedException();

    private static Holon Copy(Holon row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Description = row.Description,
        ParentHolonId = row.ParentHolonId,
        UserId = row.UserId,
        ProviderName = row.ProviderName,
        ChainId = row.ChainId,
        AssetType = row.AssetType,
        TokenId = row.TokenId,
        Metadata = new Dictionary<string, string>(row.Metadata),
        PeerHolonIds = new List<Guid>(row.PeerHolonIds),
        CreatedDate = row.CreatedDate,
        ModifiedDate = row.ModifiedDate,
        IsActive = row.IsActive,
        SourceHolonId = row.SourceHolonId,
        OriginUserId = row.OriginUserId,
        IsPublic = row.IsPublic,
    };
}
