using System.Text;
using System.Text.Json;
using Algorand;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AlgorandEncoder = Algorand.Utils.Encoder;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Reserve;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    private sealed record ExchangeUnsignedPlan(IReadOnlyList<string> TransactionsBase64);

    /// <inheritdoc cref="IExchangeTransactionModule.PrepareExchangeAsync"/>
    public async Task<AZOAResult<PreparedExchangeWire>> PrepareExchangeAsync(ExchangeWireIntent intent, CancellationToken ct = default)
    {
        if (!ValidExchangeIntent(intent) || IndexerHttpClient is null || _custodyScopeFactory is null)
            return AZOAResult<PreparedExchangeWire>.Failure("Exact exchange intent, authoritative indexer and durable custody journal are required.");
        var preparationId = ExchangePreparationId(intent);
        var hash = CanonicalJson.Hash(intent);
        return await NativePreparationJournal.UseAsync(_custodyScopeFactory, async store =>
        {
            var saved = await store.GetAsync(preparationId, ct);
            if (saved is null)
            {
                var parameters = await ValidatedTransactionParametersAsync(ct);
                if (intent.ProtocolIdentity != "algorand:" + parameters.GenesisHashB64)
                    return AZOAResult<PreparedExchangeWire>.Failure("Exchange belongs to another physical network.");
                var transactions = intent.Legs.Select(leg => (Transaction)new AssetTransferTransaction
                {
                    Sender = new Address(leg.Sender), AssetReceiver = new Address(leg.Recipient),
                    XferAsset = UInt64Text.Parse(leg.AssetId), AssetAmount = UInt64Text.Parse(leg.QuantityBaseUnits),
                }).Append(new PaymentTransaction { Sender = new Address(intent.FeePayerAddress),
                    Receiver = new Address(intent.FeePayerAddress), Amount = 0 }).ToArray();
                foreach (var transaction in transactions)
                {
                    ApplySuggestedParams(transaction, parameters);
                    transaction.Fee = 0;
                    transaction.Note = Encoding.UTF8.GetBytes(hash);
                }
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    foreach (var transaction in transactions) transaction.Group = null;
                    TxGroup.AssignGroupID(transactions);
                    var total = transactions.Aggregate(0UL, (fee, transaction) => checked(fee + AvmFee(transaction, parameters)));
                    if (total > UInt64Text.Parse(intent.MaximumNetworkFeeBaseUnits))
                        return AZOAResult<PreparedExchangeWire>.Failure("The exchange group exceeds its accepted network cost cap.");
                    if (transactions[^1].Fee == total) break;
                    transactions[^1].Fee = total;
                    if (attempt == 7) throw new InvalidOperationException("Exchange pooled fee did not converge.");
                }
                saved = await store.ClaimAsync(new NativePreparation { Id = preparationId, IntentHash = hash,
                    Stage = "Exchange", Generation = checked((int)intent.Generation), CreatedAt = DateTimeOffset.UtcNow,
                    PlanJson = JsonSerializer.Serialize(new ExchangeUnsignedPlan(transactions.Select(transaction =>
                        Convert.ToBase64String(AlgorandEncoder.EncodeToMsgPackOrdered(transaction))).ToArray())) }, ct);
            }
            if (saved.IntentHash != hash || saved.Stage != "Exchange" || saved.Generation != intent.Generation)
                return AZOAResult<PreparedExchangeWire>.Failure("This exchange generation already belongs to another intent.");
            var plan = JsonSerializer.Deserialize<ExchangeUnsignedPlan>(saved.PlanJson)
                ?? throw new InvalidOperationException("Exchange unsigned plan is absent.");
            if (saved.PreparedJson is not null)
            {
                var recovered = JsonSerializer.Deserialize<PreparedExchangeWire>(saved.PreparedJson)!;
                ValidateExchangePrepared(recovered, plan);
                return AZOAResult<PreparedExchangeWire>.Success(recovered);
            }
            var unsigned = plan.TransactionsBase64.Select(value => AlgorandEncoder.DecodeFromMsgPack<Transaction>(Convert.FromBase64String(value))).ToArray();
            ValidateExchangeUnsigned(intent, unsigned, plan);
            var signed = new List<string>();
            for (var index = 0; index < unsigned.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var bytes = Convert.FromBase64String(plan.TransactionsBase64[index]);
                var result = index < intent.Legs.Count && intent.Legs[index].SignerRole == "Treasury"
                    ? await SignExchangeTreasuryAsync(bytes, intent, intent.Legs[index].SigningContext)
                    : await SignViaCustodyAsync(bytes, "prepare treasury exchange", index == intent.Legs.Count
                        ? intent.FeePayerContext : intent.Legs[index].SigningContext);
                if (result.IsError || result.Result is null) return AZOAResult<PreparedExchangeWire>.Failure(result.Message);
                signed.Add(Convert.ToBase64String(result.Result));
            }
            var prepared = new PreparedExchangeWire(intent, preparationId, Convert.ToBase64String(unsigned[0].Group!.Bytes),
                unsigned.Select(transaction => transaction.TxID()).ToArray(), signed);
            ValidateExchangePrepared(prepared, plan);
            var winner = await store.SavePreparedAsync(preparationId, hash, JsonSerializer.Serialize(prepared), ct);
            var resultWinner = JsonSerializer.Deserialize<PreparedExchangeWire>(winner.PreparedJson!)!;
            ValidateExchangePrepared(resultWinner, plan);
            return AZOAResult<PreparedExchangeWire>.Success(resultWinner);
        });
    }

    private async Task<AZOAResult<byte[]>> SignExchangeTreasuryAsync(byte[] unsigned, ExchangeWireIntent intent, SigningContext context)
    {
        using var scope = _custodyScopeFactory!.CreateScope();
        var treasury = scope.ServiceProvider.GetRequiredService<IProjectTreasury>();
        var reference = new ProjectTreasuryReference(ChainType, intent.Treasury!.TenantUserId, intent.Treasury.ProjectReference);
        var result = await treasury.SignAsTreasuryAsync(reference, context, bytes =>
        {
            using var material = new SigningKeyMaterial(bytes);
            return Task.FromResult(_signerFactory!.GetSigner(ChainType).Sign(unsigned, material));
        });
        return result.IsError || result.Result is null ? AZOAResult<byte[]>.Failure(result.Message) : result.Result;
    }

    private bool ValidExchangeIntent(ExchangeWireIntent intent)
        => intent is { Version: 1, Generation: >= 0 and <= int.MaxValue, Legs.Count: >= 1 and <= 15 }
            && intent.ChainType == ChainType && intent.Network == ActiveNetwork
            && !string.IsNullOrWhiteSpace(intent.SourceId) && intent.SourceId.Length <= 200
            && ValidateAddressFormat(intent.FeePayerAddress) && intent.FeePayerContext.IsPlatform
            && intent.FeePayerContext.ChainType == ChainType
            && UInt64Text.TryParse(intent.MaximumNetworkFeeBaseUnits, out var fee) && fee > 0
            && intent.Legs.All(leg => leg is not null && leg.SigningContext.ChainType == ChainType
                && ValidateAddressFormat(leg.Sender) && ValidateAddressFormat(leg.Recipient)
                && leg.Sender != leg.Recipient && UInt64Text.TryParse(leg.AssetId, out var asset) && asset > 0
                && UInt64Text.TryParse(leg.QuantityBaseUnits, out var amount) && amount > 0
                && (leg.SignerRole == "Participant" && leg.SigningContext.IsResolvableUserContext && !leg.SigningContext.IsPlatform
                    && leg.SigningContext.Scope == AzoaScopes.SwapSign
                    || leg.SignerRole == "Reserve" && leg.SigningContext.IsPlatform && leg.Sender == intent.FeePayerAddress
                    || leg.SignerRole == "Treasury" && leg.SigningContext.IsPlatform && intent.Treasury is { TenantUserId: var tenant }
                        && tenant != Guid.Empty && !string.IsNullOrWhiteSpace(intent.Treasury.ProjectReference)));

    private static string ExchangePreparationId(ExchangeWireIntent intent)
        => CanonicalJson.Hash(new { Domain = "algorand-exchange-wire-v1", intent.ChainType, intent.Network, intent.SourceId, intent.Generation });
}
