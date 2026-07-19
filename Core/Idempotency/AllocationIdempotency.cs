// SPDX-License-Identifier: UNLICENSED

using System.Security.Cryptography;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Core.Idempotency;

/// <summary>
/// Internal allocation-ledger identity and its safe external correlation.
/// See <c>Core/Idempotency/AGENTS.md</c>.
/// </summary>
public static class AllocationIdempotency
{
    private const string ReceiptCorrelationDomain = "azoa:allocation-receipt:v1:";

    /// <summary>
    /// Builds the legacy allocation ledger key and a domain-separated receipt
    /// correlation. A supplied client key wins; otherwise the legacy deterministic
    /// content fallback is used.
    /// </summary>
    public static AllocationIdempotencyValue Create(
        Guid apiKeyId,
        Guid avatarId,
        AllocationRequest request,
        string? clientIdempotencyKey)
    {
        if (apiKeyId == Guid.Empty)
            throw new ArgumentException("API key id is required.", nameof(apiKeyId));
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrWhiteSpace(clientIdempotencyKey))
            return CreateFromClientKey(apiKeyId, clientIdempotencyKey);

        var ledgerKey = BuildLedgerKey(apiKeyId, DeterministicContentKey(avatarId, request));
        return new AllocationIdempotencyValue(ledgerKey, CorrelationFor(ledgerKey));
    }

    /// <summary>
    /// Recreates the exact ledger identity for a required client-supplied key.
    /// Receipt endpoints intentionally use this path because they cannot safely
    /// reconstruct the request-content fallback.
    /// </summary>
    public static AllocationIdempotencyValue CreateFromClientKey(
        Guid apiKeyId,
        string clientIdempotencyKey)
    {
        if (apiKeyId == Guid.Empty)
            throw new ArgumentException("API key id is required.", nameof(apiKeyId));
        if (string.IsNullOrWhiteSpace(clientIdempotencyKey))
            throw new ArgumentException("Idempotency-Key is required.", nameof(clientIdempotencyKey));

        var ledgerKey = BuildLedgerKey(apiKeyId, clientIdempotencyKey.Trim());
        return new AllocationIdempotencyValue(ledgerKey, CorrelationFor(ledgerKey));
    }

    /// <summary>
    /// Produces a lower-case SHA-256 external correlation without exposing a
    /// caller-supplied ledger key.
    /// </summary>
    public static string CorrelationFor(string ledgerKey)
    {
        if (string.IsNullOrWhiteSpace(ledgerKey))
            throw new ArgumentException("Ledger key is required.", nameof(ledgerKey));

        var input = System.Text.Encoding.UTF8.GetBytes(ReceiptCorrelationDomain + ledgerKey);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private static string BuildLedgerKey(Guid apiKeyId, string tail)
        => $"alloc:{apiKeyId}:{tail}";

    private static string DeterministicContentKey(Guid avatarId, AllocationRequest request)
    {
        var canonical = string.Join('|',
            avatarId.ToString("N"),
            request.Kind.ToString(),
            request.ChainType.ToLowerInvariant(),
            request.Amount,
            request.AssetId ?? string.Empty,
            request.AssetRecordId?.ToString("N") ?? string.Empty);
        return IdempotencyReplay.ContentHash(canonical);
    }
}

/// <summary>Allocation ledger identity for internal use plus a public-safe correlation.</summary>
public sealed class AllocationIdempotencyValue
{
    internal AllocationIdempotencyValue(string ledgerKey, string correlation)
    {
        LedgerKey = ledgerKey;
        Correlation = correlation;
    }

    /// <summary>Raw idempotency ledger identity; never return this from an HTTP response.</summary>
    internal string LedgerKey { get; }

    /// <summary>Opaque domain-separated receipt correlation safe for response contracts.</summary>
    public string Correlation { get; }
}
