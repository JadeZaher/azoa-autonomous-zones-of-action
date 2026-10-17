using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealDexQuoteStore(ISurrealExecutor executor) : IDexQuoteStore
{
    /// <inheritdoc/>
    public Task<DexQuoteRecord?> GetAsync(string id, CancellationToken ct = default)
        => executor.QuerySingleAsync<DexQuoteRecord>(SurrealQuery<DexQuoteRecord>.Key(id), ct);

    /// <inheritdoc/>
    public async Task<DexQuoteRecord?> ClaimAsync(DexQuoteRecord candidate, CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(candidate.Id, "N", out var quoteId) || quoteId == Guid.Empty ||
            candidate.ConsumedByOperationId is not null || !ValidIdentity(candidate))
            return null;
        try
        {
            var response = await executor.ExecuteAsync(SurrealWriter.Create(candidate), ct);
            response.EnsureAllOk();
            return response.GetValues<DexQuoteRecord>(0).Single();
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            var existing = await GetAsync(candidate.Id, ct)
                ?? throw new InvalidOperationException("DEX quote claim disappeared after duplicate creation.");
            return existing.ActorUserId == candidate.ActorUserId && existing.TenantId == candidate.TenantId &&
                existing.QuoteHash == candidate.QuoteHash && existing.QuoteJson == candidate.QuoteJson &&
                existing.ExpiresAt == candidate.ExpiresAt ? existing : null;
        }
    }

    private static bool ValidIdentity(DexQuoteRecord candidate)
    {
        try
        {
            using var document = JsonDocument.Parse(candidate.QuoteJson);
            if (CanonicalJson.Canonicalize(document.RootElement) != candidate.QuoteJson ||
                CanonicalJson.HashCanonical(candidate.QuoteJson) != candidate.QuoteHash)
                return false;
            var terms = document.RootElement.Deserialize<DexQuoteTerms>();
            return terms is not null && terms.Version == 1 && terms.ActorUserId != Guid.Empty &&
                terms.TenantId != Guid.Empty && terms.Prerequisites is not null &&
                terms.ActorUserId.ToString("N") == candidate.ActorUserId &&
                (terms.TenantId?.ToString("N") ?? "") == candidate.TenantId &&
                terms.ExpiresAt == candidate.ExpiresAt && candidate.CreatedAt < candidate.ExpiresAt;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
