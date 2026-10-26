using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Simulated;

/// <summary>Deterministic, network-free transferable-NFT lifecycle; see Providers/Blockchain/AGENTS.md §simulated-nft.</summary>
public sealed partial class SimulatedBlockchainProvider : INftTransactionModule
{
    /// <summary>Contract identity of every simulated NFT (the analogue of Algorand's <c>"ASA"</c>).</summary>
    public const string SimNftContract = SimPrefix + "nft";

    /// <summary>Token standard recorded for simulated NFTs.</summary>
    public const string SimNftStandard = "SimNFT";

    private sealed record SimNftHolding(string OwnerAddress, bool Burned);

    private sealed record SimNftOutcome(bool Applied, string? AssetId, string? OwnerAddress);

    // assetId → holding and transactionId → outcome; in-process only, like the balance ledger.
    private readonly ConcurrentDictionary<string, SimNftHolding> _nftHoldings = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SimNftOutcome> _nftSubmissions = new(StringComparer.Ordinal);
    private readonly object _nftGate = new();

    /// <inheritdoc/>
    /// <remarks>Null on purpose: the simulated rail has no credential authority, so soulbound issuance refuses.</remarks>
    public string? CredentialAuthorityAddress => null;

    /// <inheritdoc/>
    public string NftTokenStandard => SimNftStandard;

    /// <inheritdoc/>
    public string NativeCurrencyCode => SimCurrency;

    /// <inheritdoc/>
    public int NativeCurrencyDecimals => 0;

    /// <summary>Deterministic positive 63-bit asset id minted by a lifecycle's Create stage.</summary>
    public static string SimNftAssetId(string lifecycleId)
    {
        var value = BinaryPrimitives.ReadUInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes("sim-nft/1|" + lifecycleId)));
        return Math.Max(1UL, value & long.MaxValue).ToString(CultureInfo.InvariantCulture);
    }

    /// <inheritdoc/>
    public Task<AZOAResult<PreparedNftTransaction>> PrepareNftTransactionAsync(NftTransactionIntent intent, CancellationToken ct = default)
        => Task.FromResult(intent is not null && ValidSimNftIntent(intent)
            ? AZOAResult<PreparedNftTransaction>.Success(ExpectedSimNft(intent))
            : AZOAResult<PreparedNftTransaction>.Failure("A transferable, zero-fee simulated NFT intent on this network is required."));

    /// <inheritdoc/>
    public Task SubmitNftTransactionAsync(PreparedNftTransaction prepared, CancellationToken ct = default)
    {
        if (!IsExpectedSimNft(prepared))
            throw new InvalidOperationException("NFT wire is not its deterministic simulated candidate.");
        lock (_nftGate)
        {
            // Applies at most once per transaction id; an unmet precondition is recorded as a failed-on-chain effect.
            if (!_nftSubmissions.ContainsKey(prepared.TransactionId))
                _nftSubmissions[prepared.TransactionId] = ApplySimNft(prepared.Intent);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<AZOAResult<NftTransactionObservation>> ObserveNftTransactionAsync(PreparedNftTransaction prepared, CancellationToken ct = default)
    {
        if (!IsExpectedSimNft(prepared))
            return Task.FromResult(AZOAResult<NftTransactionObservation>.Failure("NFT wire is not its deterministic simulated candidate."));
        var status = !_nftSubmissions.TryGetValue(prepared.TransactionId, out var outcome) ? "Unseen"
            : outcome.Applied ? "Confirmed" : "FailedOnChain";
        return Task.FromResult(AZOAResult<NftTransactionObservation>.Success(new(status, prepared.TransactionId,
            outcome?.AssetId ?? prepared.Intent.AssetId, outcome?.OwnerAddress, prepared.ProtocolIdentity, prepared.ContractAddress)));
    }

    /// <inheritdoc/>
    public Task<AZOAResult<NftOwnershipObservation>> ReadNftOwnershipAsync(string assetId, string ownerAddress, string protocolIdentity,
        CancellationToken ct = default, string contractAddress = "")
    {
        if (protocolIdentity != SimProtocolIdentity || contractAddress != SimNftContract || string.IsNullOrWhiteSpace(assetId))
            return Task.FromResult(AZOAResult<NftOwnershipObservation>.Failure("Simulated NFT protocol identity is unavailable."));
        // An unknown or burned asset (including any minted before a restart) proves no ownership.
        return Task.FromResult(AZOAResult<NftOwnershipObservation>.Success(
            _nftHoldings.TryGetValue(assetId, out var holding) && !holding.Burned
                ? new NftOwnershipObservation(true, holding.OwnerAddress, false)
                : new NftOwnershipObservation(false, null, false)));
    }

    /// <summary>Applies one stage's effect to the ownership ledger; caller holds <see cref="_nftGate"/>.</summary>
    private SimNftOutcome ApplySimNft(NftTransactionIntent intent)
    {
        if (intent.Kind == NftTransactionKinds.Create)
        {
            var minted = SimNftAssetId(intent.Authorization!.LifecycleId);
            if (!_nftHoldings.TryAdd(minted, new SimNftHolding(intent.RecipientAddress, false))) return new(false, minted, null);
            return new(true, minted, intent.RecipientAddress);
        }
        var assetId = intent.AssetId!;
        if (!_nftHoldings.TryGetValue(assetId, out var holding) || holding.Burned || holding.OwnerAddress != intent.SignerAddress)
            return new(false, assetId, null);
        if (intent.Kind == NftTransactionKinds.Burn)
        {
            _nftHoldings[assetId] = holding with { Burned = true };
            return new(true, assetId, null);
        }
        _nftHoldings[assetId] = holding with { OwnerAddress = intent.RecipientAddress };
        return new(true, assetId, intent.RecipientAddress);
    }

    private PreparedNftTransaction ExpectedSimNft(NftTransactionIntent intent)
    {
        var transactionId = SimTxPrefix + Base32(Sha256(Canonical("nft", intent.PreparationKey, "|", CanonicalJson.Hash(intent))));
        var wire = Convert.ToBase64String(Encoding.UTF8.GetBytes(Canonical("sim-signed:", transactionId)));
        return new PreparedNftTransaction(transactionId, wire, intent, SimProtocolIdentity, SimNftContract, TokenStandard: SimNftStandard);
    }

    // Field-wise: the persisted candidate round-trips through JSON, so re-derive every identity from its intent.
    private bool IsExpectedSimNft(PreparedNftTransaction? prepared)
    {
        if (prepared?.Intent is null || !ValidSimNftIntent(prepared.Intent)) return false;
        var expected = ExpectedSimNft(prepared.Intent);
        return expected.TransactionId == prepared.TransactionId && expected.SignedTransactionBase64 == prepared.SignedTransactionBase64
            && expected.ProtocolIdentity == prepared.ProtocolIdentity && expected.ContractAddress == prepared.ContractAddress
            && prepared.AdditionalSignedTransactionsBase64 is null or { Count: 0 } && prepared.AtomicGroupIdentity is null;
    }

    /// <summary>The shared authorization contract over sim addresses: transferable, zero-fee, user-signed, single-transaction stages only.</summary>
    private bool ValidSimNftIntent(NftTransactionIntent intent)
        => NftTransactionAuthorization.IsValid(intent) && intent.ChainType == ChainType && intent.Network == ActiveNetwork
            && !intent.Soulbound && !intent.SigningContext.IsPlatform && intent.ProtocolCostQuote is null
            && intent.Kind is NftTransactionKinds.Create or NftTransactionKinds.Transfer or NftTransactionKinds.ReturnForBurn or NftTransactionKinds.Burn
            && IsSimulated(intent.SignerAddress) && IsSimulated(intent.RecipientAddress) && IsSimulated(intent.Authorization!.ActorAddress)
            && intent.Fee is null && intent.Authorization.FeeQuote is { NativeBaseUnits: "0", Decimals: 0 } quote && quote.Currency == SimCurrency
            && (intent.Kind == NftTransactionKinds.Create
                ? intent.AssetId is null && intent.ContractAddress.Length == 0
                : UInt64Text.TryParse(intent.AssetId, out var asset) && asset > 0 && intent.ContractAddress == SimNftContract);
}
