using System.Text.Json;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealNftProtocolCostQuoteStore(ISurrealExecutor executor) : INftProtocolCostQuoteStore
{
    /// <inheritdoc/>
    public async Task<NftProtocolCostQuote?> GetAsync(string quoteId, Guid actor, Guid actingTenantId, CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(quoteId, "N", out var id) || id == Guid.Empty || actor == Guid.Empty) return null;
        var row = await executor.QuerySingleAsync<NftProtocolCostQuoteRecord>(SurrealQuery<NftProtocolCostQuoteRecord>.Key(quoteId), ct);
        if (row is null || row.ActorUserId != actor.ToString("N") || row.ActingTenantId != actingTenantId.ToString("N")) return null;
        var quote = JsonSerializer.Deserialize<NftProtocolCostQuote>(row.QuoteJson);
        if (!NftProtocolCostContract.IsValid(quote) || quote!.QuoteId != quoteId || quote.ActorUserId != actor ||
            quote.ActingTenantId != actingTenantId || quote.LifecycleId != row.LifecycleId ||
            quote.CreatedAt != row.CreatedAt || quote.ExpiresAt != row.ExpiresAt ||
            NftProtocolCostContract.Hash(quote) != row.QuoteHash ||
            CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(quote)) != row.QuoteJson)
            throw new InvalidOperationException("NFT protocol cost receipt integrity failed.");
        return quote;
    }

    /// <inheritdoc/>
    public async Task<NftProtocolCostQuote?> ClaimAsync(NftProtocolCostQuote quote, CancellationToken ct = default)
    {
        if (!NftProtocolCostContract.IsValid(quote)) return null;
        var row = new NftProtocolCostQuoteRecord
        {
            Id = quote.QuoteId, ActorUserId = quote.ActorUserId.ToString("N"), ActingTenantId = quote.ActingTenantId.ToString("N"),
            LifecycleId = quote.LifecycleId, QuoteHash = NftProtocolCostContract.Hash(quote),
            QuoteJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(quote)),
            CreatedAt = quote.CreatedAt, ExpiresAt = quote.ExpiresAt,
        };
        try
        {
            var response = await executor.ExecuteAsync(SurrealWriter.Create(row), ct);
            response.EnsureAllOk();
            return quote;
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            var existing = await GetAsync(quote.QuoteId, quote.ActorUserId, quote.ActingTenantId, ct);
            return existing == quote ? existing : null;
        }
    }
}
