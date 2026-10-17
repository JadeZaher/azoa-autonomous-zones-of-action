using System.Text;
using System.Text.Json;
using Algorand;
using Algorand.Algod.Model.Transactions;
using AlgorandEncoder = Algorand.Utils.Encoder;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Signing;
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
                var transactions = ExchangeMembers(intent).Select(member => member.Template).ToArray();
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
            var members = ExchangeMembers(intent);
            var signed = new List<string>();
            for (var index = 0; index < unsigned.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var bytes = Convert.FromBase64String(plan.TransactionsBase64[index]);
                var result = members[index].TreasurySigned
                    ? await SignExchangeTreasuryAsync(bytes, intent, members[index].Context)
                    : await SignViaCustodyAsync(bytes, "prepare treasury exchange", members[index].Context);
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

    /// <summary>One group member: its unsigned template (no network parameters), sender and custody signer.</summary>
    private sealed record ExchangeMember(Transaction Template, string Sender, SigningContext Context, bool TreasurySigned);

    /// <summary>The exact ordered members: readiness funding and acceptance, the legs, then the pooled fee payer.</summary>
    private static IReadOnlyList<ExchangeMember> ExchangeMembers(ExchangeWireIntent intent)
    {
        var members = new List<ExchangeMember>();
        if (intent.Readiness is { } readiness)
        {
            var funding = UInt64Text.Parse(readiness.FundingBaseUnits);
            if (funding > 0)
                members.Add(new(new PaymentTransaction { Sender = new Address(intent.FeePayerAddress),
                    Receiver = new Address(readiness.Recipient), Amount = funding, Fee = 0 },
                    intent.FeePayerAddress, intent.FeePayerContext, false));
            members.Add(new(new AssetAcceptTransaction { Sender = new Address(readiness.Recipient),
                AssetReceiver = new Address(readiness.Recipient), XferAsset = UInt64Text.Parse(readiness.AssetId), Fee = 0 },
                readiness.Recipient, readiness.RecipientContext, false));
        }
        members.AddRange(intent.Legs.Select(leg => new ExchangeMember(new AssetTransferTransaction
        {
            Sender = new Address(leg.Sender), AssetReceiver = new Address(leg.Recipient),
            XferAsset = UInt64Text.Parse(leg.AssetId), AssetAmount = UInt64Text.Parse(leg.QuantityBaseUnits), Fee = 0,
        }, leg.Sender, leg.SigningContext, leg.SignerRole == "Treasury")));
        members.Add(new(new PaymentTransaction { Sender = new Address(intent.FeePayerAddress),
            Receiver = new Address(intent.FeePayerAddress), Amount = 0 }, intent.FeePayerAddress, intent.FeePayerContext, false));
        return members;
    }

    private bool ValidExchangeIntent(ExchangeWireIntent intent)
        => intent is { Version: 1, Generation: >= 0 and <= int.MaxValue, Legs.Count: >= 1 and <= 15 }
            && intent.Legs.Count + 1 + (intent.Readiness is null ? 0 : 2) <= 16
            && (intent.Readiness is null || ValidExchangeReadiness(intent, intent.Readiness))
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

    /// <summary>Readiness only ever accepts for, and funds, the participant who signs one of the legs, within the funding ceiling.</summary>
    private bool ValidExchangeReadiness(ExchangeWireIntent intent, ExchangeWireReadiness readiness)
        => ValidateAddressFormat(readiness.Recipient) && readiness.Recipient != intent.FeePayerAddress
            && UInt64Text.TryParse(readiness.AssetId, out var asset) && asset > 0
            && UInt64Text.TryParse(readiness.FundingBaseUnits, out var funding) && funding <= ExchangeReadinessFundingCeiling
            && readiness.RecipientContext.ChainType == ChainType && readiness.RecipientContext.IsResolvableUserContext
            && !readiness.RecipientContext.IsPlatform && readiness.RecipientContext.Scope == AzoaScopes.SwapSign
            && intent.Legs.Any(leg => leg.SignerRole == "Participant" && leg.Sender == readiness.Recipient
                && leg.SigningContext == readiness.RecipientContext);

    /// <summary>Hard ceiling on reserve-paid readiness funding in microAlgos, whatever the exchange option says.</summary>
    private const ulong ExchangeReadinessFundingCeiling = 1_000_000;

    private static string ExchangePreparationId(ExchangeWireIntent intent)
        => CanonicalJson.Hash(new { Domain = "algorand-exchange-wire-v1", intent.ChainType, intent.Network, intent.SourceId, intent.Generation });
}
