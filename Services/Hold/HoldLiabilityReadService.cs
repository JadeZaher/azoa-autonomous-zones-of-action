using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Reserve;

namespace AZOA.WebAPI.Services.Hold;

/// <summary>Scoped reads over custody liabilities and reserve assets; see Services/Hold/AGENTS.md §liability-reads.</summary>
public interface IHoldLiabilityReadStore
{
    /// <summary>Lists liabilities the participant owns, receives, or (as a tenant) holds in custody.</summary>
    Task<IReadOnlyList<HoldLedger>> ListLiabilitiesAsync(Guid actor, Guid? tenant, int offset, int limit, CancellationToken ct = default);

    /// <summary>Lists reserve rows registered by <paramref name="ownerTenant"/>, plus untenanted rows when asked.</summary>
    Task<IReadOnlyList<PlatformReserveLedger>> ListReserveAssetsAsync(Guid? ownerTenant, bool includePlatformUnit, int limit,
        CancellationToken ct = default);
}

/// <summary>Participant-safe view of one custody liability.</summary>
public sealed record HoldLiabilityView(string HoldRecordId, string HoldId, HoldAssetIdentity Asset, string QuantityBaseUnits,
    string State, Guid OwnerUserId, Guid BeneficiaryUserId, Guid? FunderUserId, Guid? CustodyTenantId, string FundingSource,
    string ReservePartition, DateTimeOffset? ReleaseNotBefore, bool AutoRelease, string? ActiveReleaseMandateId, bool Purchased,
    string? OpeningCommandId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>One page of liabilities.</summary>
public sealed record HoldLiabilityPage(IReadOnlyList<HoldLiabilityView> Items, int? NextOffset);

/// <summary>A declared partition the caller may open a hold against.</summary>
public sealed record HoldReservePartitionView(string Name, string DeclaredBaseUnits, string AvailableBaseUnits);

/// <summary>A reserve asset the caller's draw authority admits, with the exact identity a hold intent names.</summary>
public sealed record HoldReserveAssetView(HoldAssetIdentity Asset, string UnitName, int Decimals,
    IReadOnlyList<HoldReservePartitionView> Partitions);

/// <summary>Liability and reserve-asset reads that back direct hold admission.</summary>
public sealed class HoldLiabilityReadService(IHoldLiabilityReadStore store, IHoldCommandStore commands, ReserveOptions options)
{
    private const int MaximumReserveAssets = 200;

    /// <summary>Lists the caller's liabilities newest first.</summary>
    public async Task<AZOAResult<HoldLiabilityPage>> ListAsync(Guid actor, Guid? tenant, int offset, int limit, CancellationToken ct = default)
    {
        if (actor == Guid.Empty || tenant == Guid.Empty || offset < 0 || offset > int.MaxValue - 100 || limit is < 1 or > 100)
            return AZOAResult<HoldLiabilityPage>.Failure("A valid participant and bounded page are required.");
        var rows = await store.ListLiabilitiesAsync(actor, tenant, offset, limit, ct);
        var items = rows.Where(row => Visible(row, actor, tenant)).Select(View).OfType<HoldLiabilityView>().ToList();
        return AZOAResult<HoldLiabilityPage>.Success(new(items, rows.Count == limit ? checked(offset + rows.Count) : null));
    }

    /// <summary>Reads one liability when the caller may see it; otherwise the same not-found refusal.</summary>
    public async Task<AZOAResult<HoldLiabilityView>> GetAsync(string holdRecordId, Guid actor, Guid? tenant, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(holdRecordId) || actor == Guid.Empty || tenant == Guid.Empty) return NotFound();
        var row = await commands.GetHoldAsync(holdRecordId.Trim(), ct);
        return row is not null && Visible(row, actor, tenant) && View(row) is { } view
            ? AZOAResult<HoldLiabilityView>.Success(view) : NotFound();
    }

    /// <summary>Lists reserve assets whose partitions this caller's draw authority admits, as <c>Open</c> would.</summary>
    public async Task<AZOAResult<IReadOnlyList<HoldReserveAssetView>>> ListReserveAssetsAsync(Guid actor, Guid? tenant,
        bool isNodeOperator, CancellationToken ct = default)
    {
        if (actor == Guid.Empty || tenant == Guid.Empty)
            return AZOAResult<IReadOnlyList<HoldReserveAssetView>>.Failure("A valid participant is required.");
        var rows = isNodeOperator
            ? await store.ListReserveAssetsAsync(null, includePlatformUnit: true, MaximumReserveAssets, ct)
            : await store.ListReserveAssetsAsync(tenant ?? actor, includePlatformUnit: tenant is not null, MaximumReserveAssets, ct);
        var views = new List<HoldReserveAssetView>();
        foreach (var row in rows)
        {
            var owner = ReservePartitionScope.ReserveRowTenant(row);
            var authority = isNodeOperator ? ReserveDrawAuthority.NodeOperator
                : ReserveDrawAuthority.ForTenant(owner != Guid.Empty ? tenant ?? actor : tenant);
            if (ReservePartitionScope.Refuse(row, authority, row.AssetId, options) is not null) continue;
            if (ReserveView(row) is { } view) views.Add(view);
        }
        return AZOAResult<IReadOnlyList<HoldReserveAssetView>>.Success(views);
    }

    /// <summary>Mirrors the store query: own tenant-less liabilities, received ones, and custody for the caller-as-tenant.</summary>
    internal static bool Visible(HoldLedger row, Guid actor, Guid? tenant)
    {
        var owner = row.OwnerUserId == actor.ToString("N");
        var custody = ParseUser(row.TenantId);
        var beneficiary = ParseUser(row.BeneficiaryUserId) == actor;
        return tenant is { } acting
            ? custody == acting && (owner || beneficiary)
            : (owner && custody is null) || beneficiary || custody == actor;
    }

    private static HoldLiabilityView? View(HoldLedger row)
    {
        if (!Enum.TryParse<ChainNetwork>(row.Network, ignoreCase: false, out var network)
            || row.ProtocolIdentity is null || ParseUser(row.BeneficiaryUserId) is not { } beneficiary
            || !Guid.TryParseExact(row.OwnerUserId, "N", out var owner)) return null;
        return new(SurrealRecordGuid.BareId(row.Id), row.HoldId,
            new(row.Chain, network, row.ProtocolIdentity, row.AssetId, row.HoldingAddress), row.Amount, row.State,
            owner, beneficiary, ParseUser(row.FunderUserId), ParseUser(row.TenantId), row.FundingSource, row.ReservePartition,
            row.ReleaseNotBefore, row.AutoRelease, row.ActiveReleaseMandateId, row.EconomicSourceId is not null,
            row.OpeningCommandId, row.CreatedAt, row.UpdatedAt);
    }

    private static HoldReserveAssetView? ReserveView(PlatformReserveLedger row)
    {
        if (!Enum.TryParse<ChainNetwork>(row.Network, ignoreCase: false, out var network) || string.IsNullOrWhiteSpace(row.ProtocolIdentity))
            return null;
        var partitions = ReservePartitionCodec.Decode(row.Partitions, out _);
        if (partitions is null) return null;
        return new(new(row.Chain, network, row.ProtocolIdentity, row.AssetId, row.ReserveAddress), row.UnitName, row.Decimals,
            partitions.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new HoldReservePartitionView(entry.Key, entry.Value.Declared.ToString(),
                    entry.Value.Available.ToString())).ToList());
    }

    private static Guid? ParseUser(string? link)
        => ReservePartitionScope.ParseRowUserId(link) is var parsed && parsed != Guid.Empty ? parsed : null;

    private static AZOAResult<HoldLiabilityView> NotFound() => AZOAResult<HoldLiabilityView>.Failure("The scoped hold liability was not found.");
}
