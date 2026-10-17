using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using Nethereum.ABI;
using Nethereum.Signer;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider : INftTransactionModule
{
    /// <inheritdoc/>
    public string? CredentialAuthorityAddress => _config[$"AZOA:Ethereum:Nft:Networks:{ActiveNetwork}:CredentialAuthorityAddress"];
    /// <inheritdoc/>
    public string NftTokenStandard => "ERC721";
    /// <inheritdoc/>
    public string NativeCurrencyCode => "ETH";
    /// <inheritdoc/>
    public int NativeCurrencyDecimals => 18;

    private string NftContract => _config[$"AZOA:Ethereum:Nft:Networks:{ActiveNetwork}:ContractAddress"] ?? string.Empty;

    /// <inheritdoc/>
    public async Task<AZOAResult<PreparedNftTransaction>> PrepareNftTransactionAsync(NftTransactionIntent intent, CancellationToken ct = default)
    {
        if (!ValidEthereumNftIntent(intent)) return Error<PreparedNftTransaction>("Ethereum NFT authorization is invalid.");
        var readiness = await RequireNftContractAsync(intent.ContractAddress, ct);
        if (readiness.IsError) return Error<PreparedNftTransaction>(readiness.Message);
        var chain = await GetChainIdAsync(ct);
        if (chain.IsError) return Error<PreparedNftTransaction>(chain.Message);
        var protocol = "eip155:" + chain.Result.ToString(CultureInfo.InvariantCulture);
        var payer = intent.Fee?.PayerAddress ?? intent.SignerAddress;
        var payerContext = intent.Fee?.SigningContext ?? intent.SigningContext;
        var ownerJson = JsonSerializer.Serialize(intent);
        var prior = await WithPreparationStoreAsync(store => store.GetAsync(EthereumIdentity(protocol, payer.ToLowerInvariant(), intent.PreparationKey), ct));
        if (prior is not null)
        {
            var call = JsonSerializer.Deserialize<EthereumCall>(prior.IntentJson) ?? throw new InvalidOperationException("NFT admitted call missing.");
            if (call.OwnerIntentJson != ownerJson || call.Context is not { } priorContext || call.Owner is not null)
                return Error<PreparedNftTransaction>("NFT preparation key belongs to another intent.");
            var resumed = await PrepareCallAsync(intent.PreparationKey, payer, call.Target, BigInteger.Parse(call.Value, CultureInfo.InvariantCulture),
                Convert.FromHexString(call.Data[2..]), call.Label, priorContext, ct, ownerJson);
            return resumed.IsError || resumed.Result is null ? Error<PreparedNftTransaction>(resumed.Message)
                : Ok(new PreparedNftTransaction(resumed.Result.TransactionId!, resumed.Result.SignedTransactionBase64!, intent, protocol, NftContract));
        }
        var payload = EncodeNftExecution(intent, DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds());
        var digest = NftPermitDigest(chain.Result, NftContract, payload);
        var grant = intent.Authorization!;
        var effectContext = intent.SigningContext.IsPlatform ? grant.IssuerSigningContext ?? grant.ActorSigningContext : intent.SigningContext;
        var effectAddress = intent.SigningContext.IsPlatform ? grant.IssuerAddress ?? grant.ActorAddress : intent.SignerAddress;
        var effect = await SignNftPermitAsync(digest, effectAddress, effectContext);
        if (effect.IsError || effect.Result is null) return Error<PreparedNftTransaction>(effect.Message);
        var policyContext = SigningContext.PlatformFor(ChainType);
        if (grant.ActorSigningContext.ActingTenantId != Guid.Empty)
            policyContext = policyContext.ActingAs(grant.ActorSigningContext.ActingTenantId, grant.ActorSigningContext.Scope!, grant.ActorUserId);
        var policy = await SignNftPermitAsync(digest, CredentialAuthorityAddress!, policyContext);
        if (policy.IsError || policy.Result is null) return Error<PreparedNftTransaction>(policy.Message);
        var data = Convert.FromHexString(EthereumAbi.EncodeCall("execute(bytes,bytes,bytes)", payload, effect.Result, policy.Result)[2..]);
        var amount = intent.Fee is null ? BigInteger.Zero : BigInteger.Parse(grant.FeeQuote.NativeBaseUnits, CultureInfo.InvariantCulture);
        var prepared = await PrepareCallAsync(intent.PreparationKey, payer, NftContract, amount, data, "execute NFT lifecycle", payerContext, ct, ownerJson);
        return prepared.IsError || prepared.Result is null ? Error<PreparedNftTransaction>(prepared.Message)
            : Ok(new PreparedNftTransaction(prepared.Result.TransactionId!, prepared.Result.SignedTransactionBase64!, intent, protocol, NftContract));
    }

    /// <inheritdoc/>
    public async Task SubmitNftTransactionAsync(PreparedNftTransaction prepared, CancellationToken ct = default)
    {
        var readiness = await RequireNftContractAsync(prepared.ContractAddress, ct);
        if (readiness.IsError) throw new InvalidOperationException(readiness.Message);
        await SubmitPreparationAsync(await ReadNftPreparationAsync(prepared, ct), ct);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NftTransactionObservation>> ObserveNftTransactionAsync(PreparedNftTransaction prepared, CancellationToken ct = default)
    {
        var row = await ReadNftPreparationAsync(prepared, ct);
        var observed = await ObserveOwnedPreparationAsync(row, ct);
        if (observed.IsError) return Error<NftTransactionObservation>(observed.Message);
        var asset = NftTokenId(prepared.Intent).ToString(CultureInfo.InvariantCulture);
        if (observed.Result != ChainConfirmation.Confirmed)
            return Ok(new NftTransactionObservation(observed.Result == ChainConfirmation.FailedOnChain ? "FailedOnChain"
                : observed.Result == ChainConfirmation.Pending ? "Pending" : "Unseen",
                prepared.TransactionId, asset, null, prepared.ProtocolIdentity, prepared.ContractAddress));
        var receipt = await RpcCallAsync("eth_getTransactionReceipt", new object?[] { prepared.TransactionId }, ct);
        if (receipt.IsError || receipt.IsNullResult || !receipt.Value.TryGetProperty("logs", out var logs) || logs.ValueKind != JsonValueKind.Array)
            return Error<NftTransactionObservation>("Final NFT effect evidence unavailable.");
        var signature = "0x" + Keccak.CalculateHash("EffectExecuted(bytes32,uint8,uint256,address,uint256)");
        var lifecycle = EthereumHex.Data(Keccak.CalculateHash(Encoding.UTF8.GetBytes(prepared.Intent.Authorization!.LifecycleId)));
        var stage = "0x" + NftStage(prepared.Intent.Kind).ToString("x64", CultureInfo.InvariantCulture);
        var token = "0x" + NftTokenId(prepared.Intent).ToString("x", CultureInfo.InvariantCulture).TrimStart('0').PadLeft(64, '0');
        var matching = logs.EnumerateArray().Where(log => TryReadStringProperty(log, "address", out var address)
            && EthereumAddress.AreSame(address, prepared.ContractAddress) && log.TryGetProperty("topics", out var topics)
            && topics.ValueKind == JsonValueKind.Array && topics.GetArrayLength() == 4
            && string.Equals(topics[0].GetString(), signature, StringComparison.OrdinalIgnoreCase)
            && string.Equals(topics[1].GetString(), lifecycle, StringComparison.OrdinalIgnoreCase)
            && string.Equals(topics[2].GetString(), stage, StringComparison.OrdinalIgnoreCase)
            && string.Equals(topics[3].GetString(), token, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matching.Length != 1 || !TryReadStringProperty(matching[0], "data", out var eventData)
            || !EthereumHex.TryParseData(eventData, out var eventBytes) || eventBytes.Length != 64)
            return Error<NftTransactionObservation>("Finalized receipt lacks the exact NFT lifecycle effect.");
        var owner = EthereumHex.Data(eventBytes.AsSpan(12, 20));
        var expectedOwner = NftEffectOwner(prepared.Intent);
        var paid = new BigInteger(eventBytes.AsSpan(32, 32), isUnsigned: true, isBigEndian: true);
        var expectedFee = prepared.Intent.Fee is null ? BigInteger.Zero : BigInteger.Parse(prepared.Intent.Fee.Quote.NativeBaseUnits, CultureInfo.InvariantCulture);
        if (!EthereumAddress.AreSame(owner, expectedOwner) || paid != expectedFee)
            return Error<NftTransactionObservation>("NFT effect owner or native fee differs from the authorized intent.");
        return Ok(new NftTransactionObservation("Confirmed", prepared.TransactionId, asset, owner, prepared.ProtocolIdentity, prepared.ContractAddress));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NftOwnershipObservation>> ReadNftOwnershipAsync(string assetId, string ownerAddress, string protocolIdentity,
        CancellationToken ct = default, string contractAddress = "")
    {
        var ready = await RequireNftContractAsync(contractAddress, ct);
        var chain = await GetChainIdAsync(ct);
        if (ready.IsError || chain.IsError || protocolIdentity != "eip155:" + chain.Result.ToString(CultureInfo.InvariantCulture)
            || !BigInteger.TryParse(assetId, NumberStyles.None, CultureInfo.InvariantCulture, out var token) || token <= 0)
            return Error<NftOwnershipObservation>("NFT contract or protocol identity is unavailable.");
        var owner = await CallReadAsync(NftContract, EthereumAbi.EncodeCall("ownerOf(uint256)", token), null, ct);
        var bound = await CallReadAsync(NftContract, EthereumAbi.EncodeCall("soulbound(uint256)", token), null, ct);
        if (owner.IsError || bound.IsError || !EthereumAbi.TryDecodeAddress(owner.Result, out var address)
            || !EthereumAbi.TryDecodeUInt256(bound.Result, out var soulbound))
            return Error<NftOwnershipObservation>("Authoritative NFT ownership is unavailable.");
        return Ok(new NftOwnershipObservation(true, address, soulbound == 1));
    }

    private async Task<AZOAResult<bool>> RequireNftContractAsync(string requested, CancellationToken ct)
    {
        var expected = _config[$"AZOA:Ethereum:Nft:Networks:{ActiveNetwork}:RuntimeCodeHash"];
        if (!EthereumAddress.TryParse(NftContract, out _) || !EthereumAddress.TryParse(CredentialAuthorityAddress, out _)
            || string.IsNullOrWhiteSpace(expected) || (!string.IsNullOrEmpty(requested) && !EthereumAddress.AreSame(requested, NftContract)))
            return Error<bool>("Verified Ethereum NFT deployment is unavailable.");
        var code = await RpcCallAsync("eth_getCode", new object?[] { NftContract, "latest" }, ct);
        if (!code.TryGetString(out var encoded) || !EthereumHex.TryParseData(encoded, out var bytes) || bytes.Length == 0
            || !string.Equals(EthereumHex.Data(Keccak.CalculateHash(bytes)), expected, StringComparison.OrdinalIgnoreCase))
            return Error<bool>("Ethereum NFT deployed code differs from the verified artifact.");
        var authority = await CallReadAsync(NftContract, EthereumAbi.EncodeCall("credentialAuthority()"), null, ct);
        return !authority.IsError && EthereumAbi.TryDecodeAddress(authority.Result, out var address) && EthereumAddress.AreSame(address, CredentialAuthorityAddress)
            ? Ok(true) : Error<bool>("Ethereum NFT credential authority differs from configuration.");
    }

    private bool ValidEthereumNftIntent(NftTransactionIntent intent)
        => NftTransactionAuthorization.IsValid(intent) && intent.ChainType == ChainType && intent.Network == ActiveNetwork
            && intent.Authorization!.FeeQuote.Currency == NativeCurrencyCode && intent.Authorization.FeeQuote.Decimals == NativeCurrencyDecimals
            && BigInteger.TryParse(intent.Authorization.FeeQuote.NativeBaseUnits, NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
            && amount >= 0 && amount <= EthereumHex.MaxUInt256 && intent.Authorization.FeeQuote.Revision > 0
            && EthereumAddress.TryParse(intent.SignerAddress, out _) && EthereumAddress.TryParse(intent.RecipientAddress, out _)
            && EthereumAddress.TryParse(intent.Authorization.ActorAddress, out _)
            && (intent.SourceAddress is null || EthereumAddress.TryParse(intent.SourceAddress, out _))
            && (intent.Authorization.IssuerAddress is null || EthereumAddress.TryParse(intent.Authorization.IssuerAddress, out _))
            && (intent.Kind == NftTransactionKinds.Create ? intent.AssetId is null
                : BigInteger.TryParse(intent.AssetId, NumberStyles.None, CultureInfo.InvariantCulture, out var token)
                    && token > 0 && token <= EthereumHex.MaxUInt256)
            && (amount == 0 || EthereumAddress.TryParse(intent.Authorization.FeeQuote.TreasuryAddress, out _));

    private async Task<EthereumPreparation> ReadNftPreparationAsync(PreparedNftTransaction prepared, CancellationToken ct)
    {
        if (!ValidEthereumNftIntent(prepared.Intent) || !EthereumAddress.AreSame(prepared.ContractAddress, NftContract))
            throw new InvalidOperationException("Prepared NFT authorization differs from this provider.");
        var payer = prepared.Intent.Fee?.PayerAddress ?? prepared.Intent.SignerAddress;
        var row = await WithPreparationStoreAsync(store => store.GetAsync(EthereumIdentity(prepared.ProtocolIdentity,
            payer.ToLowerInvariant(), prepared.Intent.PreparationKey), ct));
        var call = row is null ? null : JsonSerializer.Deserialize<EthereumCall>(row.IntentJson);
        if (row is null || row.TransactionId != prepared.TransactionId || row.SignedTransactionBase64 != prepared.SignedTransactionBase64
            || call?.OwnerIntentJson != JsonSerializer.Serialize(prepared.Intent))
            throw new InvalidOperationException("Prepared NFT wire differs from its durable owner authorization.");
        return row;
    }

    private static BigInteger NftTokenId(NftTransactionIntent intent) => intent.AssetId is null
        ? new BigInteger(Keccak.CalculateHash(Encoding.UTF8.GetBytes(intent.Authorization!.LifecycleId)), isUnsigned: true, isBigEndian: true)
        : BigInteger.Parse(intent.AssetId, CultureInfo.InvariantCulture);
    private static int NftStage(string stage) => stage switch { "Create" => 0, "Accept" => 1, "Deliver" => 2, "Transfer" => 3, "ReturnForBurn" => 4, "Burn" => 5, "Revoke" => 6, _ => throw new InvalidOperationException("Unknown NFT stage.") };
    private string NftEffectOwner(NftTransactionIntent intent) => intent.Kind switch
    {
        "Create" => intent.Soulbound ? CredentialAuthorityAddress! : intent.RecipientAddress,
        "Accept" => intent.SourceAddress ?? (intent.Soulbound ? CredentialAuthorityAddress! : intent.Authorization!.ActorAddress),
        "Burn" or "Revoke" => EthereumAddress.Zero,
        _ => intent.RecipientAddress,
    };

    private byte[] EncodeNftExecution(NftTransactionIntent intent, long deadline)
    {
        var grant = intent.Authorization!;
        var stage = NftStage(intent.Kind);
        var operation = grant.Operation switch { "Mint" => 0, "Transfer" => 1, "Burn" => 2, "Revoke" => 3, "Refund" => 4, _ => throw new InvalidOperationException() };
        var from = stage == 0 ? EthereumAddress.Zero : stage == 5 ? intent.SignerAddress
            : intent.SourceAddress ?? (intent.Soulbound ? CredentialAuthorityAddress! : intent.SignerAddress);
        var to = stage == 0 ? NftEffectOwner(intent) : stage is 5 or 6 ? EthereumAddress.Zero : intent.RecipientAddress;
        var amount = BigInteger.Parse(grant.FeeQuote.NativeBaseUnits, CultureInfo.InvariantCulture);
        var encoded = new ABIEncode().GetABIEncoded(
            new ABIValue("bytes32", Keccak.CalculateHash(Encoding.UTF8.GetBytes(grant.LifecycleId))), new ABIValue("uint8", operation),
            new ABIValue("uint8", stage), new ABIValue("uint32", grant.PreparationGeneration), new ABIValue("uint256", NftTokenId(intent)),
            new ABIValue("address", from), new ABIValue("address", to), new ABIValue("address", grant.ActorAddress),
            new ABIValue("address", grant.IssuerAddress ?? EthereumAddress.Zero), new ABIValue("address", intent.RecipientAddress),
            new ABIValue("bool", intent.Soulbound), new ABIValue("bool", grant.IsFinalEffect), new ABIValue("string", intent.MetadataUri),
            new ABIValue("uint256", grant.IsFinalEffect ? amount : BigInteger.Zero), new ABIValue("uint256", amount),
            new ABIValue("address", string.IsNullOrWhiteSpace(grant.FeeQuote.TreasuryAddress) ? EthereumAddress.Zero : grant.FeeQuote.TreasuryAddress),
            new ABIValue("uint256", grant.FeeQuote.Revision), new ABIValue("address", intent.Fee?.PayerAddress ?? intent.SignerAddress), new ABIValue("uint256", deadline));
        return [..new ABIEncode().GetABIEncoded(new ABIValue("uint256", 32)), ..encoded];
    }

    private static byte[] NftPermitDigest(BigInteger chain, string contract, byte[] payload)
        => EthereumPayloadPermit.Digest("AZOA NFT", "1", chain, contract, payload);

    private async Task<AZOAResult<byte[]>> SignNftPermitAsync(byte[] digest, string sender, SigningContext context)
    {
        Task<AZOAResult<byte[]>> Sign(byte[] secret)
        {
            if (!TryDeriveAddress(secret, out var address) || !EthereumAddress.AreSame(address, sender))
                return Task.FromResult(Error<byte[]>("NFT permit signer does not match the authorized actor."));
            return Task.FromResult(Ok(EthereumPayloadPermit.Sign(new EthECKey(secret, true), digest)));
        }
        if (_custodyService is not null)
            return await SignWithCustodyAsync(_custodyService, "authorize NFT effect", context, Sign);
        if (_custodyScopeFactory is null) return Error<byte[]>("NFT permit signing requires custody.");
        using var scope = _custodyScopeFactory.CreateScope();
        return await SignWithCustodyAsync(scope.ServiceProvider.GetRequiredService<IKeyCustodyService>(), "authorize NFT effect", context, Sign);
    }
}
