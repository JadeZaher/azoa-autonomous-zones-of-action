using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Algorand;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AlgorandEncoder = Algorand.Utils.Encoder;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider : IValueCustodyModule
{
    /// <inheritdoc/>
    public async Task<AZOAResult<PreparedValueAcceptance>> PrepareValueAcceptanceAsync(ValueAcceptanceIntent intent, CancellationToken ct)
    {
        if (intent.ChainType != ChainType || intent.Network != ActiveNetwork || IndexerHttpClient is null
            || string.IsNullOrWhiteSpace(intent.PreparationKey) || !ValidateAddressFormat(intent.Recipient)
            || intent.SigningContext.IsPlatform || !intent.SigningContext.IsResolvableUserContext
            || intent.SigningContext.Scope != AzoaScopes.TransferSign || !ValueCustodyIdentity.MatchesAcceptance(intent)
            || !UInt64Text.TryParse(intent.AssetId, out var asset) || asset == 0
            || !UInt64Text.TryParse(intent.Cost.MaximumNetworkFeeBaseUnits, out var maximumFee)
            || intent.Cost.Quote.AcceptanceMinimumBalanceBaseUnits != "100000")
            return AZOAResult<PreparedValueAcceptance>.Failure("Exact recipient authority and accepted account costs are required.");
        var id = ValueAcceptancePreparationId(intent);
        var hash = CanonicalJson.Hash(intent);
        return await NativePreparationJournal.UseAsync(_custodyScopeFactory, async store =>
        {
            var row = await store.GetAsync(id, ct);
            if (row is null)
            {
                var current = await QuoteValueCustodyAsync(intent.Cost.Quote.Request, ct);
                if (current.IsError || current.Result is not { } quote || quote.RecipientReady
                    || quote.ProtocolIdentity != intent.Cost.Quote.ProtocolIdentity
                    || quote.AssetAuthorityHash != intent.Cost.Quote.AssetAuthorityHash)
                    return AZOAResult<PreparedValueAcceptance>.Failure("Current account readiness or asset mechanics changed; observe before preparing acceptance.");
                var parameters = await ValidatedTransactionParametersAsync(ct);
                var transaction = new AssetAcceptTransaction { Sender = new Address(intent.Recipient),
                    AssetReceiver = new Address(intent.Recipient), XferAsset = asset, Note = Encoding.UTF8.GetBytes(hash) };
                ApplySuggestedParams(transaction, parameters);
                transaction.Fee = AvmFee(transaction, parameters);
                transaction.Fee = AvmFee(transaction, parameters);
                var account = await ReadValueAccountAsync(intent.Recipient, asset, ct);
                if (transaction.Fee!.Value > maximumFee || account is null || account.OptedIn
                    || account.NativeAmount < account.MinimumBalance
                    || account.NativeAmount - account.MinimumBalance < checked(100000UL + transaction.Fee.Value))
                    return AZOAResult<PreparedValueAcceptance>.Failure("The recipient needs the accepted native fee and additional account minimum before acceptance.");
                row = await store.ClaimAsync(new NativePreparation { Id = id, IntentHash = hash,
                    Stage = "ValueAcceptance", Generation = intent.Cost.Generation,
                    PlanJson = JsonSerializer.Serialize(new ValueTransferPlan(Convert.ToBase64String(AlgorandEncoder.EncodeToMsgPackOrdered(transaction)),
                        "algorand:" + parameters.GenesisHashB64)), CreatedAt = DateTimeOffset.UtcNow }, ct);
            }
            if (row.IntentHash != hash || row.Stage != "ValueAcceptance" || row.Generation != intent.Cost.Generation)
                return AZOAResult<PreparedValueAcceptance>.Failure("Preparation key belongs to a different account acceptance.");
            var plan = JsonSerializer.Deserialize<ValueTransferPlan>(row.PlanJson)
                ?? throw new InvalidDataException("Saved account acceptance plan is absent.");
            if (row.PreparedJson is not null)
            {
                var recovered = JsonSerializer.Deserialize<PreparedValueAcceptance>(row.PreparedJson)!;
                var transaction = ValidatePreparedValueAcceptance(recovered);
                if (CanonicalJson.Hash(recovered.Intent) != hash || recovered.ProtocolIdentity != plan.ProtocolIdentity
                    || !AlgorandEncoder.EncodeToMsgPackOrdered(transaction).AsSpan().SequenceEqual(Convert.FromBase64String(plan.UnsignedBase64)))
                    throw new InvalidDataException("Recovered acceptance conflicts with its original preparation.");
                return AZOAResult<PreparedValueAcceptance>.Success(recovered);
            }
            var unsigned = Convert.FromBase64String(plan.UnsignedBase64);
            var signed = await SignViaCustodyAsync(unsigned, "prepare recipient acceptance", intent.SigningContext);
            if (signed.IsError || signed.Result is null) return AZOAResult<PreparedValueAcceptance>.Failure(signed.Message);
            var envelope = AlgorandSignedEnvelope.DecodeCanonical(Convert.ToBase64String(signed.Result));
            if (!AlgorandEncoder.EncodeToMsgPackOrdered(envelope.Tx).AsSpan().SequenceEqual(unsigned))
                throw new InvalidDataException("Custody changed the saved acceptance plan.");
            var prepared = new PreparedValueAcceptance(envelope.Tx.TxID(), Convert.ToBase64String(signed.Result), plan.ProtocolIdentity, intent);
            ValidatePreparedValueAcceptance(prepared);
            var saved = await store.SavePreparedAsync(id, hash, JsonSerializer.Serialize(prepared), ct);
            return AZOAResult<PreparedValueAcceptance>.Success(JsonSerializer.Deserialize<PreparedValueAcceptance>(saved.PreparedJson!)!);
        });
    }

    /// <inheritdoc/>
    public async Task SubmitValueAcceptanceAsync(PreparedValueAcceptance prepared, CancellationToken ct)
    {
        var transaction = ValidatePreparedValueAcceptance(prepared);
        var row = await NativePreparationJournal.UseAsync(_custodyScopeFactory,
            store => store.GetAsync(ValueAcceptancePreparationId(prepared.Intent), ct));
        if (row is null || row.IntentHash != CanonicalJson.Hash(prepared.Intent) || row.Stage != "ValueAcceptance"
            || row.Generation != prepared.Intent.Cost.Generation || row.PreparedJson != JsonSerializer.Serialize(prepared))
            throw new InvalidDataException("Acceptance is not the journaled signed candidate.");
        var plan = JsonSerializer.Deserialize<ValueTransferPlan>(row.PlanJson)!;
        if (plan.ProtocolIdentity != prepared.ProtocolIdentity
            || !AlgorandEncoder.EncodeToMsgPackOrdered(transaction).AsSpan().SequenceEqual(Convert.FromBase64String(plan.UnsignedBase64)))
            throw new InvalidDataException("Acceptance bytes conflict with the original plan.");
        var parameters = await ValidatedTransactionParametersAsync(ct);
        if (prepared.ProtocolIdentity != "algorand:" + parameters.GenesisHashB64)
            throw new InvalidDataException("Acceptance belongs to a different physical network.");
        var status = await AlgodHttpClient.GetFromJsonAsync<AlgodStatus>("/v2/status", ct)
            ?? throw new InvalidDataException("Current chain round is unavailable.");
        if (status.LastRound < 0) throw new InvalidDataException("Current chain round is invalid.");
        if ((ulong)status.LastRound > transaction.LastValid) return;
        using var content = new ByteArrayContent(Convert.FromBase64String(prepared.SignedTransactionBase64));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-binary");
        using var response = await AlgodHttpClient.PostAsync("/v2/transactions", content, ct);
        response.EnsureSuccessStatusCode();
        using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!AlgorandReceiptFields.String(result.RootElement, "txId", prepared.TransactionId))
            throw new HttpRequestException("Algod returned a different acceptance transaction identity.");
    }

    private AssetAcceptTransaction ValidatePreparedValueAcceptance(PreparedValueAcceptance prepared)
    {
        var intent = prepared.Intent;
        var signed = AlgorandSignedEnvelope.DecodeCanonical(prepared.SignedTransactionBase64);
        if (intent.ChainType != ChainType || intent.Network != ActiveNetwork || !ValueCustodyIdentity.MatchesAcceptance(intent)
            || string.IsNullOrWhiteSpace(intent.PreparationKey) || intent.SigningContext.IsPlatform
            || !intent.SigningContext.IsResolvableUserContext || intent.SigningContext.Scope != AzoaScopes.TransferSign
            || signed.Tx is not AssetAcceptTransaction transaction || transaction.TxID() != prepared.TransactionId
            || transaction.Sender.EncodeAsString() != intent.Recipient || transaction.AssetReceiver?.EncodeAsString() != intent.Recipient
            || transaction.XferAsset.ToString() != intent.AssetId || transaction.AssetCloseTo is not null
            || transaction.RekeyTo is not null || transaction.Group is not null || transaction.GenesisHash?.Bytes.Length != 32
            || transaction.FirstValid is null || transaction.LastValid is null || transaction.FirstValid > transaction.LastValid
            || prepared.ProtocolIdentity != "algorand:" + Convert.ToBase64String(transaction.GenesisHash.Bytes)
            || prepared.ProtocolIdentity != intent.Cost.Quote.ProtocolIdentity
            || transaction.Fee is null || !UInt64Text.TryParse(intent.Cost.MaximumNetworkFeeBaseUnits, out var maximum)
            || transaction.Fee > maximum || transaction.Note is null
            || !transaction.Note.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(CanonicalJson.Hash(intent))))
            throw new InvalidDataException("Acceptance wire differs from the recipient's saved intent and costs.");
        AlgorandSignedEnvelope.RequireSingleSender(signed, intent.Recipient);
        return transaction;
    }

    private static string ValueAcceptancePreparationId(ValueAcceptanceIntent intent)
        => CanonicalJson.Hash(new { Domain = "algorand-value-acceptance-v1", intent.ChainType, intent.Network, intent.PreparationKey });
}
