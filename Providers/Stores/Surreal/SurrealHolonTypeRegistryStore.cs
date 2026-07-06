// SPDX-License-Identifier: UNLICENSED

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IHolonTypeRegistryStore"/> (final-hardening-cutover F5).
/// The AssetType name is the record id AND the unique-indexed column, so a registration
/// is looked up directly by <c>type::record</c> (mirrors <see cref="SurrealDataMigrationLedgerStore"/>).
/// Operates on the attributed <see cref="HolonType"/> POCO directly — no domain/POCO split,
/// since the registry has no legacy domain type to bridge to.
///
/// <para><b>No-throw.</b> Every method captures exceptions into an
/// <see cref="AZOAResult{T}"/> rather than throwing.</para>
/// </summary>
/// <remarks>See <c>Providers/Stores/Surreal/AGENTS.md</c> §holon-type-registry.</remarks>
public sealed class SurrealHolonTypeRegistryStore : IHolonTypeRegistryStore
{
    private const string Table = "holon_type_registry";

    private readonly ISurrealExecutor _executor;

    public SurrealHolonTypeRegistryStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task<AZOAResult<IEnumerable<HolonType>>> ListAsync(CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery.Of("SELECT * FROM holon_type_registry ORDER BY created_at DESC");
            var rows = await _executor.QueryAsync<HolonType>(q, ct);
            return new AZOAResult<IEnumerable<HolonType>> { Result = rows.Select(Normalize).ToList(), Message = "Success" };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IEnumerable<HolonType>>().CaptureException(ex,
                $"SurrealHolonTypeRegistryStore.ListAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<HolonType>> GetByAssetTypeAsync(string assetType, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM type::record($_t, $_id)")
                .WithParam("_t", Table)
                .WithParam("_id", assetType);
            var row = await _executor.QuerySingleAsync<HolonType>(q, ct);
            return new AZOAResult<HolonType>
            {
                IsError = row == null,
                Message = row == null ? "Holon type not registered." : "Success",
                Result = row == null ? null : Normalize(row),
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<HolonType>().CaptureException(ex,
                $"SurrealHolonTypeRegistryStore.GetByAssetTypeAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<HolonType>> UpsertAsync(HolonType type, CancellationToken ct = default)
    {
        try
        {
            // The AssetType name IS the id: a re-register of the same type replaces the
            // existing row rather than creating a duplicate (the unique index would reject
            // a second row anyway).
            type.Id = type.AssetType;
            var q = SurrealWriter.Upsert(type);
            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();
            var saved = resp.GetValues<HolonType>(0).FirstOrDefault();
            return new AZOAResult<HolonType> { Result = saved is not null ? Normalize(saved) : type, Message = "Saved." };
        }
        catch (Exception ex)
        {
            return new AZOAResult<HolonType>().CaptureException(ex,
                $"SurrealHolonTypeRegistryStore.UpsertAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<bool>> DeleteAsync(string assetType, CancellationToken ct = default)
    {
        try
        {
            var checkQ = SurrealQuery
                .Of("SELECT * FROM type::record($_t, $_id)")
                .WithParam("_t", Table)
                .WithParam("_id", assetType);
            var existing = await _executor.QuerySingleAsync<HolonType>(checkQ, ct);
            if (existing == null)
                return new AZOAResult<bool> { IsError = true, Message = "Holon type not registered.", Result = false };

            var q = SurrealQuery
                .Of("DELETE type::record($_t, $_id)")
                .WithParam("_t", Table)
                .WithParam("_id", assetType);
            await _executor.ExecuteAsync(q, ct);
            return new AZOAResult<bool> { Result = true, Message = "Deleted." };
        }
        catch (Exception ex)
        {
            return new AZOAResult<bool>().CaptureException(ex,
                $"SurrealHolonTypeRegistryStore.DeleteAsync failed: {ex.Message}");
        }
    }

    // The record id round-trips as `holon_type_registry:<asset_type>`; strip the table
    // prefix so callers see the bare AssetType name in both Id and AssetType.
    private static HolonType Normalize(HolonType row)
    {
        if (string.IsNullOrEmpty(row.AssetType) && !string.IsNullOrEmpty(row.Id))
            row.AssetType = StripPrefix(row.Id);
        row.Id = StripPrefix(row.Id);
        return row;
    }

    private static string StripPrefix(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var colon = raw.IndexOf(':');
        return colon >= 0 && colon < raw.Length - 1 ? raw[(colon + 1)..] : raw;
    }
}
