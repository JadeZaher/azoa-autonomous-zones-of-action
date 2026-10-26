using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Simulated;

/// <summary>Deterministic, network-free fungible creation; see Providers/Blockchain/AGENTS.md §simulated-fungible-creation.</summary>
public sealed partial class SimulatedBlockchainProvider : IFungibleCreationModule
{
    /// <summary>Native currency code quoted by simulated creation (always zero cost).</summary>
    public const string SimCurrency = "SIM";

    // Recorded submissions (preparationId → transactionId); in-process only, like the balance ledger.
    private readonly ConcurrentDictionary<string, string> _fungibleSubmissions = new(StringComparer.Ordinal);

    /// <summary>Protocol identity binding quotes, preparations and evidence to this simulated network.</summary>
    public string SimProtocolIdentity => $"{SimPrefix}{ActiveNetwork.ToString().ToLowerInvariant()}";

    /// <summary>Deterministic ASA-shaped asset id (positive 63-bit integer text) for a preparation.</summary>
    public static string SimFungibleAssetId(string preparationId)
    {
        var value = BinaryPrimitives.ReadUInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes("sim-asset/1|" + preparationId)));
        return Math.Max(1UL, value & long.MaxValue).ToString(CultureInfo.InvariantCulture);
    }

    /// <inheritdoc/>
    public Task<AZOAResult<FungibleCreationQuote>> QuoteFungibleCreationAsync(FungibleCreationIntent intent, CancellationToken ct = default)
    {
        if (!ValidSimFungibleIntent(intent))
            return Task.FromResult(AZOAResult<FungibleCreationQuote>.Failure("Valid simulated creation intent is required."));
        var quote = new FungibleCreationQuote("", FungibleCreationIdentity.IntentHash(intent), SimProtocolIdentity,
            "0", "0", SimCurrency, 0, intent.CreatorAddress, DateTimeOffset.UtcNow.AddMinutes(10));
        return Task.FromResult(AZOAResult<FungibleCreationQuote>.Success(quote with { QuoteId = FungibleCreationIdentity.QuoteId(quote) }));
    }

    /// <inheritdoc/>
    public Task<AZOAResult<PreparedFungibleCreation>> PrepareFungibleCreationAsync(FungibleCreationIntent intent, CancellationToken ct = default)
    {
        if (!ValidSimFungibleIntent(intent) || !ValidSimFungibleQuote(intent))
            return Task.FromResult(AZOAResult<PreparedFungibleCreation>.Failure("Creation quote or immutable intent is invalid."));
        return Task.FromResult(AZOAResult<PreparedFungibleCreation>.Success(ExpectedSimPreparation(intent)));
    }

    /// <inheritdoc/>
    public Task SubmitFungibleCreationAsync(PreparedFungibleCreation prepared, CancellationToken ct = default)
    {
        if (!IsExpectedSimPreparation(prepared))
            throw new InvalidOperationException("Creation wire is not its deterministic simulated candidate.");
        _fungibleSubmissions.TryAdd(prepared.PreparationId, prepared.TransactionId);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<AZOAResult<FungibleCreationObservation>> ObserveFungibleCreationAsync(PreparedFungibleCreation prepared, CancellationToken ct = default)
    {
        if (!IsExpectedSimPreparation(prepared))
            return Task.FromResult(AZOAResult<FungibleCreationObservation>.Failure("Creation wire is not its deterministic simulated candidate."));
        if (!_fungibleSubmissions.ContainsKey(prepared.PreparationId))
            return Task.FromResult(AZOAResult<FungibleCreationObservation>.Success(
                new FungibleCreationObservation("Pending", prepared.TransactionId, prepared.ProtocolIdentity)));
        var assetId = SimFungibleAssetId(prepared.PreparationId);
        var evidence = JsonSerializer.Serialize(new
        {
            simulated = true, protocolIdentity = prepared.ProtocolIdentity, transactionId = prepared.TransactionId,
            assetId, creator = prepared.Intent.CreatorAddress, intentHash = FungibleCreationIdentity.IntentHash(prepared.Intent),
            total = prepared.Intent.TotalBaseUnits, decimals = prepared.Intent.Decimals, unitName = prepared.Intent.UnitName,
        });
        return Task.FromResult(AZOAResult<FungibleCreationObservation>.Success(
            new FungibleCreationObservation("Confirmed", prepared.TransactionId, prepared.ProtocolIdentity, assetId, "1", evidence)));
    }

    private PreparedFungibleCreation ExpectedSimPreparation(FungibleCreationIntent intent)
    {
        var preparationId = FungibleCreationIdentity.PreparationId(intent);
        var transactionId = SimTxPrefix + Base32(Sha256(Canonical("fungible-create", preparationId)));
        var wire = Convert.ToBase64String(Encoding.UTF8.GetBytes(Canonical("sim-signed:", transactionId)));
        return new PreparedFungibleCreation(preparationId, transactionId, wire, SimProtocolIdentity, intent);
    }

    // Field-wise: the intent is the input, so only its derived identities need re-deriving (records with collections compare by reference).
    private bool IsExpectedSimPreparation(PreparedFungibleCreation prepared)
    {
        if (!ValidSimFungibleIntent(prepared.Intent) || !ValidSimFungibleQuote(prepared.Intent)) return false;
        var expected = ExpectedSimPreparation(prepared.Intent);
        return expected.PreparationId == prepared.PreparationId && expected.TransactionId == prepared.TransactionId
            && expected.SignedTransactionBase64 == prepared.SignedTransactionBase64 && expected.ProtocolIdentity == prepared.ProtocolIdentity;
    }

    private bool ValidSimFungibleIntent(FungibleCreationIntent intent)
        => intent.ChainType == ChainType && intent.Network == ActiveNetwork && intent.ActorUserId != Guid.Empty
            && !string.IsNullOrWhiteSpace(intent.PreparationKey) && intent.Generation >= 0
            && !string.IsNullOrWhiteSpace(intent.CreatorAddress)
            && UInt64Text.TryParse(intent.TotalBaseUnits, out var supply) && supply > 0
            && intent.Decimals is >= 0 and <= 19 && !string.IsNullOrWhiteSpace(intent.Name) && !string.IsNullOrWhiteSpace(intent.UnitName)
            && intent.SigningContext.Scope == AzoaScopes.TokenCreateSign;

    private bool ValidSimFungibleQuote(FungibleCreationIntent intent)
        => FungibleCreationIdentity.IsValidQuote(intent) && intent.Quote is { } quote
            && quote.ProtocolIdentity == SimProtocolIdentity && quote.Currency == SimCurrency && quote.Decimals == 0
            && quote.NetworkFeeBaseUnits == "0" && quote.MinimumBalanceBaseUnits == "0";
}
