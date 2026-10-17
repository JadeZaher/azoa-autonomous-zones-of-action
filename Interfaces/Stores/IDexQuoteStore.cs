using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Persists immutable server-issued quotes without granting execution authority.</summary>
public interface IDexQuoteStore
{
    /// <summary>Reads a private keyed quote; the caller must enforce actor and tenant ownership.</summary>
    Task<DexQuoteRecord?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Creates once, returns an identical immutable replay, or null for a conflicting identity.</summary>
    Task<DexQuoteRecord?> ClaimAsync(DexQuoteRecord candidate, CancellationToken ct = default);
}
