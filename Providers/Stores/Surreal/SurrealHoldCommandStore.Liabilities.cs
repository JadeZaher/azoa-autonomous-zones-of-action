using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Hold;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealHoldCommandStore : IHoldLiabilityReadStore
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<HoldLedger>> ListLiabilitiesAsync(Guid actor, Guid? tenant, int offset, int limit,
        CancellationToken ct = default)
    {
        var actorId = actor.ToString("N"); var actorLink = SurrealLink.ToLink("user", actorId)!;
        var query = SurrealQuery<HoldLedger>.From();
        if (tenant is { } acting)
        {
            var tenantLink = SurrealLink.ToLink("user", acting.ToString("N"))!;
            query = query.Where(row => row.TenantId == tenantLink && (row.OwnerUserId == actorId || row.BeneficiaryUserId == actorLink));
        }
        else
            query = query.Where(row => (row.OwnerUserId == actorId && row.TenantId == null) || row.BeneficiaryUserId == actorLink
                || row.TenantId == actorLink);
        return await executor.QueryAsync<HoldLedger>(query.OrderByDescending(row => row.CreatedAt).ThenBy(row => row.Id)
            .Start(Math.Max(0, offset)).Limit(Math.Clamp(limit, 1, 100)), ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PlatformReserveLedger>> ListReserveAssetsAsync(Guid? ownerTenant, bool includePlatformUnit,
        int limit, CancellationToken ct = default)
    {
        var bounded = Math.Clamp(limit, 1, 200);
        var rows = new List<PlatformReserveLedger>();
        if (ownerTenant is { } owner)
        {
            var ownerLink = SurrealLink.ToLink("user", owner.ToString("N"))!;
            rows.AddRange(await executor.QueryAsync<PlatformReserveLedger>(SurrealQuery<PlatformReserveLedger>.From()
                .Where(row => row.TenantId == ownerLink).OrderBy(row => row.Id).Limit(bounded), ct));
        }
        if (includePlatformUnit)
            rows.AddRange(await executor.QueryAsync<PlatformReserveLedger>(SurrealQuery<PlatformReserveLedger>.From()
                .Where(row => row.TenantId == null).OrderBy(row => row.Id).Limit(bounded), ct));
        return rows;
    }
}
