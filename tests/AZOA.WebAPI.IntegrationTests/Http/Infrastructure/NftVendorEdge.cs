using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using Microsoft.Extensions.DependencyInjection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Http.Infrastructure;

/// <summary>Stateful, network-free NFT module boundary with exact persisted-byte submission checks.</summary>
public sealed class NftVendorEdge(Func<IServiceScope> createScope) : INftTransactionModule, INftProtocolCostModule
{
    public const string Protocol = "fixture-nft-network";
    public const string Contract = "ASA";
    private readonly ConcurrentDictionary<string, PreparedNftTransaction> _prepared = new();
    private readonly ConcurrentDictionary<string, NftTransactionObservation> _submitted = new();
    private readonly ConcurrentDictionary<(string Protocol, string Contract, string Asset), NftOwnershipObservation> _owners = new();
    private readonly ConcurrentDictionary<(string Asset, string Owner), bool> _accepted = new();
    private readonly object _stateGate = new();
    /// <inheritdoc/>
    public string CapabilityName => "Fixture.Nft";
    /// <inheritdoc/>
    public string ChainType => "Algorand";
    /// <inheritdoc/>
    public string? CredentialAuthorityAddress => "fixtureauthority";
    /// <inheritdoc/>
    public string NftTokenStandard => "ASA";
    /// <inheritdoc/>
    public string NativeCurrencyCode => "ALGO";
    /// <inheritdoc/>
    public int NativeCurrencyDecimals => 6;
    public int SubmissionCount => _submitted.Count;

    public void SeedAsset(string asset, string owner, bool soulbound = false)
    {
        _owners[(Protocol, Contract, asset)] = new(true, owner, soulbound);
        _accepted[(asset, owner)] = true;
    }

    /// <inheritdoc/>
    public Task<AZOAResult<NftProtocolCostEstimate>> QuoteNftProtocolCostsAsync(NftTransactionIntent intent, CancellationToken ct = default)
        => Task.FromResult(AZOAResult<NftProtocolCostEstimate>.Success(new(Protocol, NftTokenStandard,
            """{"fixture":"nft-module"}""", new("1000", "100000", "0"))));

    /// <inheritdoc/>
    public Task<AZOAResult<PreparedNftTransaction>> PrepareNftTransactionAsync(NftTransactionIntent intent, CancellationToken ct = default)
    {
        if (intent.Kind is NftTransactionKinds.Transfer or NftTransactionKinds.Deliver or NftTransactionKinds.ReturnForBurn)
        {
            if (intent.AssetId is null || !_owners.TryGetValue((Protocol, Contract, intent.AssetId), out var current)
                || !current.Exists || current.OwnerAddress != intent.SignerAddress)
                return Task.FromResult(AZOAResult<PreparedNftTransaction>.Failure("The fixture asset is not owned by the signing address."));
            if (!_accepted.ContainsKey((intent.AssetId, intent.RecipientAddress)))
                return Task.FromResult(AZOAResult<PreparedNftTransaction>.FailureWithCode(
                    "The recipient must accept this fixture asset before transfer.", "NFT_RECIPIENT_ACCEPTANCE_REQUIRED"));
        }
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(intent));
        var id = NftLifecycleIdentity.Fingerprint(Convert.ToBase64String(bytes));
        var transaction = new PreparedNftTransaction(id, Convert.ToBase64String(bytes), intent, Protocol, Contract, TokenStandard: NftTokenStandard);
        _prepared.TryAdd(id, transaction);
        return Task.FromResult(AZOAResult<PreparedNftTransaction>.Success(transaction));
    }

    /// <inheritdoc/>
    public async Task SubmitNftTransactionAsync(PreparedNftTransaction prepared, CancellationToken ct = default)
    {
        var lifecycleId = prepared.Intent.Authorization?.LifecycleId
            ?? throw new InvalidOperationException("The submitted fixture transaction lacks lifecycle authority.");
        using var scope = createScope();
        var journal = await scope.ServiceProvider.GetRequiredService<ISurrealExecutor>()
            .QuerySingleAsync<NftLifecycle>(SurrealQuery<NftLifecycle>.Key(lifecycleId), ct);
        if (journal?.State != "Submitted" || journal.PreparedTransactionJson != JsonSerializer.Serialize(prepared)
            || !_prepared.TryGetValue(prepared.TransactionId, out var original)
            || JsonSerializer.Serialize(original) != JsonSerializer.Serialize(prepared))
            throw new InvalidOperationException("NFT submission did not use the exact prepared transaction durably authorized as Submitted.");
        var asset = prepared.Intent.AssetId ?? prepared.TransactionId[..16];
        lock (_stateGate)
        {
            if (_submitted.ContainsKey(prepared.TransactionId)) return;
            if (prepared.Intent.Kind == NftTransactionKinds.Create)
                SeedAsset(asset, prepared.Intent.SignerAddress, prepared.Intent.Soulbound);
            else if (prepared.Intent.Kind == NftTransactionKinds.Accept)
                _accepted[(asset, prepared.Intent.SignerAddress)] = true;
            else if (prepared.Intent.Kind is NftTransactionKinds.Deliver or NftTransactionKinds.Transfer or NftTransactionKinds.ReturnForBurn)
                _owners[(Protocol, Contract, asset)] = new(true, prepared.Intent.RecipientAddress, prepared.Intent.Soulbound);
            else if (prepared.Intent.Kind is NftTransactionKinds.Burn or NftTransactionKinds.Revoke)
                _owners[(Protocol, Contract, asset)] = new(false, null, prepared.Intent.Soulbound);
            _submitted.TryAdd(prepared.TransactionId, new("Confirmed", prepared.TransactionId, asset,
                _owners.TryGetValue((Protocol, Contract, asset), out var owner) ? owner.OwnerAddress : null, Protocol, Contract));
        }
    }

    /// <inheritdoc/>
    public Task<AZOAResult<NftTransactionObservation>> ObserveNftTransactionAsync(PreparedNftTransaction prepared, CancellationToken ct = default)
        => Task.FromResult(AZOAResult<NftTransactionObservation>.Success(_submitted.TryGetValue(prepared.TransactionId, out var observed)
            ? observed : new("Unseen", prepared.TransactionId, prepared.Intent.AssetId, null, Protocol, Contract)));

    /// <inheritdoc/>
    public Task<AZOAResult<NftOwnershipObservation>> ReadNftOwnershipAsync(string assetId, string ownerAddress, string protocolIdentity,
        CancellationToken ct = default, string contractAddress = "")
        => Task.FromResult(AZOAResult<NftOwnershipObservation>.Success(_owners.TryGetValue((protocolIdentity, contractAddress, assetId), out var owner)
            ? owner : new(false, null, false)));
}
