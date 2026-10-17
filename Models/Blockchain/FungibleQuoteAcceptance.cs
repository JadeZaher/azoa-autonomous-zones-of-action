namespace AZOA.WebAPI.Models.Blockchain;

/// <summary>Exact displayed creation quote and operation version accepted by its participant.</summary>
public sealed record FungibleQuoteAcceptance(long Version, string QuoteId);
