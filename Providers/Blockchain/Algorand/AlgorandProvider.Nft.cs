using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Algorand;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    /// <inheritdoc/>
    public string? CredentialAuthorityAddress => ResolvePlatformAddress();

    /// <inheritdoc/>
    public string NftTokenStandard => "ASA";
    /// <inheritdoc/>
    public string NativeCurrencyCode => "ALGO";
    /// <inheritdoc/>
    public int NativeCurrencyDecimals => 6;

    /// <inheritdoc/>
    public async Task<AZOAResult<PreparedNftTransaction>> PrepareNftTransactionAsync(NftTransactionIntent intent, CancellationToken ct = default)
    {
        if (intent.ChainType != ChainType || intent.Network != ActiveNetwork || !ValidateAddressFormat(intent.SignerAddress)
            || !ValidateAddressFormat(intent.RecipientAddress) || string.IsNullOrWhiteSpace(intent.MetadataUri)
            || !NftTransactionAuthorization.IsValid(intent) || !ValidateAddressFormat(intent.Authorization!.ActorAddress)
            || intent.Authorization.FeeQuote.Currency != NativeCurrencyCode || intent.Authorization.FeeQuote.Decimals != NativeCurrencyDecimals
            || (intent.SigningContext.IsPlatform && (!intent.Soulbound || intent.Kind is not (NftTransactionKinds.Create or NftTransactionKinds.Deliver or NftTransactionKinds.Revoke)))
            || (intent.AssetId is not null && intent.ContractAddress != "ASA"))
            return AZOAResult<PreparedNftTransaction>.Failure("Invalid NFT transaction identity.");
        var signer = new Address(intent.SignerAddress);
        var recipient = new Address(intent.RecipientAddress);
        Transaction transaction;
        if (intent.Kind == NftTransactionKinds.Create)
        {
            if (intent.AssetId is not null || (intent.Soulbound && (!intent.SigningContext.IsPlatform
                || intent.SignerAddress != CredentialAuthorityAddress)))
                return AZOAResult<PreparedNftTransaction>.Failure("NFT creation cannot import an existing asset or unproven authority.");
            transaction = new AssetCreateTransaction
            {
                Sender = signer,
                AssetParams = BuildAssetParams(intent.Name, "NFT", 1, 0, intent.Soulbound,
                    intent.SignerAddress, intent.SignerAddress,
                    intent.Soulbound ? intent.SignerAddress : string.Empty,
                    intent.Soulbound ? intent.SignerAddress : string.Empty,
                    intent.MetadataUri, null),
            };
        }
        else
        {
            if (!ulong.TryParse(intent.AssetId, NumberStyles.None, CultureInfo.InvariantCulture, out var assetId) || assetId == 0)
                return AZOAResult<PreparedNftTransaction>.Failure("An authoritative NFT asset identifier is required.");
            var asset = await AlgodHttpClient.GetFromJsonAsync<AlgodAssetResponse>($"/v2/assets/{assetId}", ct);
            if (asset?.Params is not { Total: 1, Decimals: 0 } parameters || asset.Index != assetId)
                return AZOAResult<PreparedNftTransaction>.Failure("The asset is not an indivisible single-supply NFT.");
            switch (intent.Kind)
            {
                case NftTransactionKinds.Accept:
                    if (intent.SignerAddress != intent.RecipientAddress || intent.SigningContext.IsPlatform)
                        return AZOAResult<PreparedNftTransaction>.Failure("NFT acceptance requires the recipient's signing authority.");
                    transaction = new AssetAcceptTransaction { Sender = signer, XferAsset = assetId, AssetReceiver = signer };
                    break;
                case NftTransactionKinds.Deliver:
                case NftTransactionKinds.Revoke:
                    if (!intent.Soulbound || parameters.Creator != intent.SignerAddress || parameters.Clawback != intent.SignerAddress
                        || intent.SignerAddress != CredentialAuthorityAddress || !intent.SigningContext.IsPlatform)
                        return AZOAResult<PreparedNftTransaction>.Failure("Credential delivery authority is not proven.");
                    var sourceAddress = intent.Kind == NftTransactionKinds.Revoke ? intent.SourceAddress : intent.SignerAddress;
                    if (sourceAddress is null || !ValidateAddressFormat(sourceAddress))
                        return AZOAResult<PreparedNftTransaction>.Failure("Credential source holding is not identified.");
                    if (await RequireClawbackableHoldingAsync(sourceAddress, assetId, 1, ct) is { } noHolding)
                        return AZOAResult<PreparedNftTransaction>.Failure(noHolding.Message);
                    if (await RequireClawbackDestinationAsync(intent.RecipientAddress, assetId, ct) is { } noDestination)
                        return AZOAResult<PreparedNftTransaction>.FailureWithCode(noDestination.Message, "NFT_RECIPIENT_ACCEPTANCE_REQUIRED");
                    transaction = new AssetClawbackTransaction
                    {
                        Sender = signer, XferAsset = assetId, AssetSender = new Address(sourceAddress), AssetReceiver = recipient, AssetAmount = 1,
                    };
                    break;
                case NftTransactionKinds.Transfer:
                case NftTransactionKinds.ReturnForBurn:
                    if (intent.Soulbound || parameters.DefaultFrozen || intent.SigningContext.IsPlatform)
                        return AZOAResult<PreparedNftTransaction>.Failure("A credential cannot use a transferable NFT operation.");
                    var holding = await ReadAssetHoldingAsync(intent.SignerAddress, assetId, ct);
                    var target = await ReadAssetHoldingAsync(intent.RecipientAddress, assetId, ct);
                    if (holding.IsError || holding.Result is not { Amount: 1, IsFrozen: false })
                        return AZOAResult<PreparedNftTransaction>.Failure("Current NFT ownership is required.");
                    if (target.IsError || target.Result is not { IsFrozen: false })
                        return AZOAResult<PreparedNftTransaction>.FailureWithCode("The recipient must accept this NFT before transfer.", "NFT_RECIPIENT_ACCEPTANCE_REQUIRED");
                    transaction = new AssetTransferTransaction { Sender = signer, XferAsset = assetId, AssetReceiver = recipient, AssetAmount = 1 };
                    break;
                case NftTransactionKinds.Burn:
                    if (parameters.Creator != intent.SignerAddress || parameters.Manager != intent.SignerAddress)
                        return AZOAResult<PreparedNftTransaction>.Failure("NFT destruction requires its creator and manager.");
                    var owner = await ReadAssetHoldingAsync(intent.SignerAddress, assetId, ct);
                    if (owner.IsError || owner.Result is not { Amount: 1 })
                        return AZOAResult<PreparedNftTransaction>.Failure("The NFT creator must hold the complete supply before destruction.");
                    transaction = new AssetDestroyTransaction { Sender = signer, AssetIndex = assetId };
                    break;
                default:
                    return AZOAResult<PreparedNftTransaction>.Failure("Unknown NFT transaction operation.");
            }
        }
        var suggested = await AlgodHttpClient.GetFromJsonAsync<AlgodSuggestedParams>("/v2/transactions/params", ct)
            ?? throw new InvalidOperationException("Algod returned no suggested parameters.");
        if (string.IsNullOrWhiteSpace(suggested.GenesisHashB64))
            return AZOAResult<PreparedNftTransaction>.Failure("NFT network identity is unavailable.");
        ApplySuggestedParams(transaction, suggested);
        transaction.Note = System.Text.Encoding.UTF8.GetBytes(NftLifecycleIdentity.Fingerprint(JsonSerializer.Serialize(intent)));
        PaymentTransaction? platformFee = null;
        if (intent.Fee is { } fee)
        {
            if (fee.Quote.ChainType != ChainType || fee.Quote.Network != ActiveNetwork || fee.Quote.Currency != NativeCurrencyCode
                || fee.Quote.Decimals != NativeCurrencyDecimals || !ulong.TryParse(fee.Quote.NativeBaseUnits, NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
                || amount == 0 || !ValidateAddressFormat(fee.PayerAddress) || !ValidateAddressFormat(fee.Quote.TreasuryAddress)
                || fee.SigningContext.IsPlatform || fee.SigningContext.UserId != fee.Quote.PayerUserId)
                return AZOAResult<PreparedNftTransaction>.Failure("NFT native fee identity is invalid.");
            platformFee = new PaymentTransaction
            {
                Sender = new Address(fee.PayerAddress), Receiver = new Address(fee.Quote.TreasuryAddress), Amount = amount,
            };
        }
        else if (intent.SigningContext.IsPlatform)
            platformFee = new PaymentTransaction
            {
                Sender = new Address(intent.Authorization!.ActorAddress), Receiver = new Address(intent.Authorization.ActorAddress), Amount = 0,
            };
        if (platformFee is not null)
        {
            ApplySuggestedParams(platformFee, suggested);
            platformFee.Note = transaction.Note;
            TxGroup.AssignGroupID(new Transaction[] { transaction, platformFee });
        }
        var signed = await SignViaCustodyAsync(Encoder.EncodeToMsgPackOrdered(transaction), "prepare NFT transaction", intent.SigningContext);
        if (signed.IsError || signed.Result is null)
            return AZOAResult<PreparedNftTransaction>.Failure(signed.Message);
        string[]? additional = null;
        if (platformFee is not null)
        {
            var feeSigned = await SignViaCustodyAsync(Encoder.EncodeToMsgPackOrdered(platformFee), "prepare NFT issuer authorization and fee", intent.Fee?.SigningContext ?? intent.Authorization!.ActorSigningContext);
            if (feeSigned.IsError || feeSigned.Result is null) return AZOAResult<PreparedNftTransaction>.Failure(feeSigned.Message);
            additional = [Convert.ToBase64String(feeSigned.Result)];
        }
        return AZOAResult<PreparedNftTransaction>.Success(new(transaction.TxID(), Convert.ToBase64String(signed.Result),
            intent, "algorand:" + suggested.GenesisHashB64, "ASA", additional,
            transaction.Group is null ? null : Convert.ToBase64String(transaction.Group.Bytes)));
    }

    /// <inheritdoc/>
    public async Task SubmitNftTransactionAsync(PreparedNftTransaction prepared, CancellationToken ct = default)
    {
        var transaction = ValidatePreparedNft(prepared);
        await RequireNftNetworkAsync(prepared, ct);
        var status = await AlgodHttpClient.GetFromJsonAsync<AlgodStatus>("/v2/status", ct)
            ?? throw new InvalidOperationException("Algod status unavailable.");
        if ((ulong)status.LastRound > transaction.LastValid)
            return;
        var payloads = new List<byte[]> { Convert.FromBase64String(prepared.SignedTransactionBase64) };
        payloads.AddRange((prepared.AdditionalSignedTransactionsBase64 ?? []).Select(Convert.FromBase64String));
        using var content = new ByteArrayContent(JoinSignedTransactions(payloads));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-binary");
        using var response = await AlgodHttpClient.PostAsync("/v2/transactions", content, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NftTransactionObservation>> ObserveNftTransactionAsync(PreparedNftTransaction prepared, CancellationToken ct = default)
    {
        var transaction = ValidatePreparedNft(prepared);
        await RequireNftNetworkAsync(prepared, ct);
        using var response = await AlgodHttpClient.GetAsync($"/v2/transactions/pending/{prepared.TransactionId}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return await ObserveIndexedNftAsync(prepared, transaction, ct);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = document.RootElement;
        var confirmed = root.TryGetProperty("confirmed-round", out var round) && round.TryGetInt64(out var value) && value > 0;
        if (!confirmed)
        {
            var status = await AlgodHttpClient.GetFromJsonAsync<AlgodStatus>("/v2/status", ct);
            if (status is not null && (ulong)status.LastRound > transaction.LastValid)
                return await ObserveIndexedNftAsync(prepared, transaction, ct);
            var rejected = root.TryGetProperty("pool-error", out var error) && !string.IsNullOrWhiteSpace(error.GetString());
            return AZOAResult<NftTransactionObservation>.Success(new(rejected ? "Unseen" : "Pending", prepared.TransactionId,
                prepared.Intent.AssetId, null, prepared.ProtocolIdentity, prepared.ContractAddress));
        }
        var assetId = prepared.Intent.AssetId;
        if (prepared.Intent.Kind == NftTransactionKinds.Create)
        {
            if (!root.TryGetProperty("asset-index", out var asset) || !asset.TryGetUInt64(out var id) || id == 0)
                return AZOAResult<NftTransactionObservation>.Failure("Confirmed NFT creation has no asset identifier.");
            assetId = id.ToString(CultureInfo.InvariantCulture);
            var created = await AlgodHttpClient.GetFromJsonAsync<AlgodAssetResponse>($"/v2/assets/{assetId}", ct);
            if (created?.Params is not { Total: 1, Decimals: 0 } parameters
                || parameters.Creator != prepared.Intent.SignerAddress || parameters.DefaultFrozen != prepared.Intent.Soulbound
                || (prepared.Intent.Soulbound && parameters.Clawback != prepared.Intent.SignerAddress))
                return AZOAResult<NftTransactionObservation>.Failure("NFT creation evidence does not match its signed intent.");
        }
        return AZOAResult<NftTransactionObservation>.Success(new("Confirmed", prepared.TransactionId, assetId,
            prepared.Intent.Kind == NftTransactionKinds.Create ? prepared.Intent.SignerAddress : prepared.Intent.RecipientAddress,
            prepared.ProtocolIdentity, prepared.ContractAddress));
    }

    private async Task RequireNftNetworkAsync(PreparedNftTransaction prepared, CancellationToken ct)
    {
        var network = await AlgodHttpClient.GetFromJsonAsync<AlgodSuggestedParams>("/v2/transactions/params", ct);
        if (network is null || prepared.ProtocolIdentity != "algorand:" + network.GenesisHashB64)
            throw new InvalidOperationException("The NFT transaction belongs to a different chain genesis.");
    }

    private async Task<AZOAResult<NftTransactionObservation>> ObserveIndexedNftAsync(
        PreparedNftTransaction prepared, Transaction transaction, CancellationToken ct)
    {
        var status = await AlgodHttpClient.GetFromJsonAsync<AlgodStatus>("/v2/status", ct)
            ?? throw new InvalidOperationException("Algod status unavailable.");
        var expired = (ulong)status.LastRound > transaction.LastValid;
        var indexer = IndexerHttpClient;
        if (indexer is null) return NftObservation(prepared, expired ? "NeedsOperator" : "Unseen");
        using var response = await indexer.GetAsync($"/v2/transactions?txid={Uri.EscapeDataString(prepared.TransactionId)}", ct);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = document.RootElement;
        if (!root.TryGetProperty("current-round", out var current) || !current.TryGetUInt64(out var indexedRound)
            || !root.TryGetProperty("transactions", out var transactions) || transactions.ValueKind != JsonValueKind.Array)
            return AZOAResult<NftTransactionObservation>.Failure("Indexer transaction evidence is incomplete.");
        var found = transactions.EnumerateArray().ToArray();
        if (found.Length > 1) return AZOAResult<NftTransactionObservation>.Failure("Indexer returned non-unique transaction evidence.");
        var evidenceRound = found.Length == 1 && found[0].TryGetProperty("confirmed-round", out var confirmed)
            && confirmed.TryGetUInt64(out var confirmedRound) ? confirmedRound : transaction.LastValid!.Value;
        if (evidenceRound == 0 || indexedRound < evidenceRound)
            return NftObservation(prepared, expired ? "NeedsOperator" : "Unseen");
        using var blockResponse = await indexer.GetAsync($"/v2/blocks/{evidenceRound}", ct);
        blockResponse.EnsureSuccessStatusCode();
        using var block = await JsonDocument.ParseAsync(await blockResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!block.RootElement.TryGetProperty("genesis-hash", out var genesis)
            || prepared.ProtocolIdentity != "algorand:" + genesis.GetString())
            return AZOAResult<NftTransactionObservation>.Failure("Indexer genesis identity does not match the persisted transaction.");
        if (found.Length == 0)
            return NftObservation(prepared, expired && indexedRound >= transaction.LastValid ? "ExpiredAbsent" : "Unseen");
        var receipt = found[0];
        if (!receipt.TryGetProperty("id", out var id) || id.GetString() != prepared.TransactionId
            || !receipt.TryGetProperty("sender", out var sender) || sender.GetString() != prepared.Intent.SignerAddress
            || evidenceRound < transaction.FirstValid || evidenceRound > transaction.LastValid)
            return AZOAResult<NftTransactionObservation>.Failure("Indexed NFT receipt does not match the persisted transaction.");
        var assetId = prepared.Intent.AssetId;
        if (prepared.Intent.Kind == NftTransactionKinds.Create)
        {
            if (!receipt.TryGetProperty("created-asset-index", out var asset) || !asset.TryGetUInt64(out var created) || created == 0)
                return AZOAResult<NftTransactionObservation>.Failure("NFT creation receipt has no asset identifier.");
            assetId = created.ToString(CultureInfo.InvariantCulture);
        }
        return AZOAResult<NftTransactionObservation>.Success(new("Confirmed", prepared.TransactionId, assetId,
            prepared.Intent.Kind == NftTransactionKinds.Create ? prepared.Intent.SignerAddress : prepared.Intent.RecipientAddress,
            prepared.ProtocolIdentity, prepared.ContractAddress));
    }

    private static AZOAResult<NftTransactionObservation> NftObservation(PreparedNftTransaction prepared, string status)
        => AZOAResult<NftTransactionObservation>.Success(new(status, prepared.TransactionId,
            prepared.Intent.AssetId, null, prepared.ProtocolIdentity, prepared.ContractAddress));

    /// <inheritdoc/>
    public async Task<AZOAResult<NftOwnershipObservation>> ReadNftOwnershipAsync(string assetId, string ownerAddress, string protocolIdentity, CancellationToken ct = default, string contractAddress = "")
    {
        if (contractAddress != "ASA" || !ValidateAddressFormat(ownerAddress)
            || !ulong.TryParse(assetId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
            return AZOAResult<NftOwnershipObservation>.Failure("Invalid NFT identifier.");
        var network = await AlgodHttpClient.GetFromJsonAsync<AlgodSuggestedParams>("/v2/transactions/params", ct);
        if (network is null || protocolIdentity != "algorand:" + network.GenesisHashB64)
            return AZOAResult<NftOwnershipObservation>.Failure("NFT network identity does not match the confirmed asset.");
        using var response = await AlgodHttpClient.GetAsync($"/v2/assets/{id}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return AZOAResult<NftOwnershipObservation>.Success(new(false, null, false));
        response.EnsureSuccessStatusCode();
        var asset = await response.Content.ReadFromJsonAsync<AlgodAssetResponse>(ct);
        if (asset?.Params is not { Total: 1, Decimals: 0 } parameters)
            return AZOAResult<NftOwnershipObservation>.Failure("NFT identity is not proven.");
        var holding = await ReadAssetHoldingAsync(ownerAddress, id, ct);
        return holding.IsError ? AZOAResult<NftOwnershipObservation>.Failure(holding.Message)
            : AZOAResult<NftOwnershipObservation>.Success(new(holding.Result?.Amount == 1,
                holding.Result?.Amount == 1 ? ownerAddress : null, parameters.DefaultFrozen));
    }

    private Transaction ValidatePreparedNft(PreparedNftTransaction prepared)
    {
        var signed = AlgorandSignedEnvelope.DecodeCanonical(prepared.SignedTransactionBase64);
        var transaction = signed.Tx;
        if (prepared.Intent.ChainType != ChainType || prepared.Intent.Network != ActiveNetwork
            || !NftTransactionAuthorization.IsValid(prepared.Intent) || prepared.ContractAddress != "ASA"
            || transaction.TxID() != prepared.TransactionId || transaction.Sender.EncodeAsString() != prepared.Intent.SignerAddress
            || transaction.GenesisHash is null || transaction.LastValid is null
            || prepared.ProtocolIdentity != "algorand:" + Convert.ToBase64String(transaction.GenesisHash.Bytes)
            || transaction.Note is null || !transaction.Note.AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(NftLifecycleIdentity.Fingerprint(JsonSerializer.Serialize(prepared.Intent)))))
            throw new InvalidOperationException("Prepared NFT transaction identity does not match its durable intent.");
        var intent = prepared.Intent;
        var assetId = ulong.TryParse(intent.AssetId, out var parsed) ? parsed : 0;
        var matches = intent.Kind switch
        {
            NftTransactionKinds.Create => transaction is AssetCreateTransaction create
                && create.AssetIndex.GetValueOrDefault() == 0 && create.AssetParams.Total == 1
                && create.AssetParams.Decimals == 0 && create.AssetParams.DefaultFrozen.GetValueOrDefault() == intent.Soulbound
                && create.AssetParams.Name == intent.Name && create.AssetParams.Url == intent.MetadataUri
                && create.AssetParams.Manager?.EncodeAsString() == intent.SignerAddress
                && (!intent.Soulbound || (create.AssetParams.Clawback?.EncodeAsString() == intent.SignerAddress
                    && create.AssetParams.Freeze?.EncodeAsString() == intent.SignerAddress)),
            NftTransactionKinds.Accept => transaction is AssetAcceptTransaction accept
                && accept.XferAsset == assetId && accept.AssetReceiver?.EncodeAsString() == intent.RecipientAddress
                && intent.SignerAddress == intent.RecipientAddress,
            NftTransactionKinds.Deliver or NftTransactionKinds.Revoke => transaction is AssetClawbackTransaction deliver
                && deliver.XferAsset == assetId && deliver.AssetAmount == 1
                && deliver.AssetSender?.EncodeAsString() == (intent.Kind == NftTransactionKinds.Revoke ? intent.SourceAddress : intent.SignerAddress)
                && deliver.AssetReceiver?.EncodeAsString() == intent.RecipientAddress,
            NftTransactionKinds.Transfer or NftTransactionKinds.ReturnForBurn => transaction is AssetTransferTransaction transfer
                && transaction is not AssetClawbackTransaction && transfer.XferAsset == assetId && transfer.AssetAmount == 1
                && transfer.AssetReceiver?.EncodeAsString() == intent.RecipientAddress && transfer.AssetCloseTo is null,
            NftTransactionKinds.Burn => transaction is AssetDestroyTransaction burn && burn.AssetIndex == assetId,
            _ => false,
        };
        if (!matches || transaction.RekeyTo is not null)
            throw new InvalidOperationException("Prepared NFT transaction semantics do not match the authorized intent.");
        ValidateNftFeeGroup(prepared, transaction);
        return transaction;
    }
    private void ValidateNftFeeGroup(PreparedNftTransaction prepared, Transaction transaction)
    {
        var fee = prepared.Intent.Fee;
        var authorization = prepared.Intent.Authorization!;
        if (fee is null && !prepared.Intent.SigningContext.IsPlatform)
        {
            if (transaction.Group is not null || prepared.AtomicGroupIdentity is not null
                || prepared.AdditionalSignedTransactionsBase64 is { Count: > 0 })
                throw new InvalidOperationException("An unquoted NFT fee group is not authorized.");
            return;
        }
        if (prepared.AdditionalSignedTransactionsBase64 is not { Count: 1 } additional
            || transaction.Group is null || prepared.AtomicGroupIdentity != Convert.ToBase64String(transaction.Group.Bytes))
            throw new InvalidOperationException("NFT platform fee group is incomplete.");
        var feeSigned = AlgorandSignedEnvelope.DecodeCanonical(additional[0]);
        var expectedAmount = fee?.Quote.NativeBaseUnits ?? "0";
        if (feeSigned.Tx is not PaymentTransaction payment || payment.Sender?.EncodeAsString() != authorization.ActorAddress
            || payment.Receiver?.EncodeAsString() != (fee?.Quote.TreasuryAddress ?? authorization.ActorAddress)
            || !ulong.TryParse(expectedAmount, NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
            || payment.Amount != amount || payment.CloseRemainderTo is not null || payment.RekeyTo is not null
            || payment.Group is null || !payment.Group.Equals(transaction.Group) || payment.GenesisHash is null
            || !payment.GenesisHash.Equals(transaction.GenesisHash) || payment.FirstValid != transaction.FirstValid
            || payment.LastValid != transaction.LastValid || payment.Note is null || !payment.Note.AsSpan().SequenceEqual(transaction.Note)
            || authorization.FeeQuote.ChainType != ChainType || authorization.FeeQuote.Network != ActiveNetwork
            || authorization.FeeQuote.Currency != NativeCurrencyCode || authorization.FeeQuote.Decimals != NativeCurrencyDecimals)
            throw new InvalidOperationException("NFT platform fee does not match the authorized quote.");
        var expected = prepared.AtomicGroupIdentity;
        transaction.Group = null;
        payment.Group = null;
        TxGroup.AssignGroupID(new Transaction[] { transaction, payment });
        if (transaction.Group is null || expected != Convert.ToBase64String(transaction.Group.Bytes)
            || transaction.TxID() != prepared.TransactionId)
            throw new InvalidOperationException("NFT effect and platform fee are not the complete atomic group.");
    }

}
