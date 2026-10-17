using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;
using Solnet.Programs;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider : IWttNativeOutputModule
{
    private sealed record WttSolanaNativeOutputPlan(int Version, WttSolanaGuard Guard, uint ComputeUnits, ulong ComputePrice,
        ulong RedemptionSlot, string RedemptionBlockIdentity, WttTargetProofs Proofs, WttStageQuote TargetQuote,
        WttStagePreparation Redemption);

    /// <inheritdoc cref="IWttNativeOutputModule.QuoteWttNativeOutputAsync"/>
    public async Task<AZOAResult<WttNativeOutputQuote>> QuoteWttNativeOutputAsync(WttOperationIntent intent,
        WttTargetProofs proofs, WttStageQuote targetQuote, WttStagePreparation redemption, CancellationToken ct = default)
    {
        try
        {
            if (!intent.UnwrapNative || redemption.Stage != "Redeem" || intent.Target.ChainType != ChainType
                || intent.Target.Network != ActiveNetwork || intent.Target.ProtocolChain != 1 || intent.TargetDecimals != 9)
                throw new InvalidDataException("Native output must be part of the original accepted Solana redemption intent.");
            var (_, target, wire) = await ReadWttTargetPreparationAsync(intent, proofs, targetQuote, redemption, ct);
            var observed = await ObserveWttTargetAsync(intent, proofs, targetQuote, redemption, ct);
            if (observed.IsError || observed.Result?.Verdict != "Confirmed")
                throw new InvalidDataException("Exact original token redemption must be finalized before native output.");
            var transaction = await ReadWttFinalizedTransactionAsync(redemption.TransactionId, ct);
            var finalized = await RequireWttFinalityAsync(transaction, redemption, wire, target.LookupTables, ct);
            if (finalized.Failed) throw new InvalidDataException("Failed redemption cannot create native-output authority.");
            var guard = await ReadWttSolanaGuardAsync(ct);
            var rent = Convert.FromBase64String(guard.RentBase64);
            var mint = SolanaSeededNativeAccount.Mint; var recipient = new PublicKey(intent.RecipientAddress);
            var source = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(recipient, mint);
            var amount = WttPayloadCodec.Denormalize((WttPayloadCodec.ParseAmount(intent.Amount.Amount)
                - WttPayloadCodec.ParseAmount(intent.Amount.Fee)).ToString(CultureInfo.InvariantCulture), 9);
            var prefix = $"AZOA:Solana:Wtt:Networks:{ActiveNetwork}:";
            if (!uint.TryParse(_config[prefix + "MaximumComputeUnits"], NumberStyles.None, CultureInfo.InvariantCulture, out var ceiling)
                || ceiling is 0 or > 1_400_000 || !ulong.TryParse(_config[prefix + "MicroLamportsPerComputeUnit"], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var price))
                throw new InvalidDataException("Native output requires explicit reviewed compute limits and unit price.");
            var plan = new WttSolanaNativeOutputPlan(1, guard, ceiling, price, finalized.Slot, finalized.BlockIdentity,
                proofs, targetQuote, redemption);
            var quote = new WttNativeOutputQuote(1, "", CanonicalJson.Hash(intent), CanonicalJson.Hash(redemption),
                CanonicalJson.Hash(new { Observation = observed.Result, finalized.BlockIdentity, finalized.Slot }), intent.Target.ProtocolIdentity,
                recipient.Key, mint.Key, source.Key, amount, "0", WttSolanaCostGuard.MinimumBalance(rent, 165).ToString(CultureInfo.InvariantCulture),
                "", DateTimeOffset.UtcNow.AddSeconds(30));
            var estimateKey = CanonicalJson.Hash(new { Domain = WttSolanaNativeOutputWire.Domain, intent.OperationId, redemption.TransactionId, quote.ExpiresAt });
            var recovery = await NewPlanAsync("", 0, 0, false, quote.IntentHash, ct);
            if ("solana:" + recovery.Genesis != quote.ProtocolIdentity) throw new InvalidDataException("Native output belongs to another genesis.");
            var compiled = CompileWttNativeOutput(intent, quote, plan, recovery, estimateKey);
            var account = WttSolanaNativeOutputWire.Build(intent, quote, estimateKey, new(guard.Program), rent, ceiling, price).Account;
            await RequireWttNativeOutputAccountsAsync(quote, account.Address, finalized.Slot, ct);
            var measured = await MeasureSolanaComputeAsync(compiled, checked((long)finalized.Slot), ct);
            var units = checked(measured + Math.Max(1000UL, measured / 10));
            if (units > ceiling) throw new InvalidDataException("Actual native-output compute exceeds the configured ceiling.");
            plan = plan with { ComputeUnits = checked((uint)units) };
            compiled = CompileWttNativeOutput(intent, quote, plan, recovery, estimateKey);
            _ = await MeasureSolanaComputeAsync(compiled, checked((long)finalized.Slot), ct);
            quote = quote with { NetworkFee = (await NativeMessageFeeAsync(compiled, ct)).ToString(CultureInfo.InvariantCulture),
                PlanJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(plan)) };
            await RequireWttNativeOutputAccountsAsync(quote, account.Address, finalized.Slot, ct);
            return Ok(quote with { QuoteId = CanonicalJson.Hash(quote) });
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException
            or FormatException or KeyNotFoundException or InvalidOperationException or OverflowException)
        { return Error<WttNativeOutputQuote>(exception.Message); }
    }

    private async Task RequireWttNativeOutputAccountsAsync(WttNativeOutputQuote quote, string temporary, ulong minimumSlot, CancellationToken ct)
    {
        var response = await DurableRpcAsync("getMultipleAccounts", [new[] { quote.MintAddress, quote.SourceTokenAccount, quote.RecipientAddress, temporary },
            new { encoding = "base64", commitment = "finalized", minContextSlot = minimumSlot }], ct);
        var accounts = response.GetProperty("value");
        if (response.GetProperty("context").GetProperty("slot").GetUInt64() < minimumSlot
            || accounts.ValueKind != JsonValueKind.Array || accounts.GetArrayLength() != 4 || accounts[3].ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("Native-output account snapshot is stale or its distinct transient address is already occupied.");
        var mint = SolanaSplAssetCodec.ReadMint(SolanaAccountData.ReadOwned(accounts[0], TokenProgram.ProgramIdKey.Key, false));
        var holding = SolanaSplAssetCodec.ReadHolding(SolanaAccountData.ReadOwned(accounts[1], TokenProgram.ProgramIdKey.Key, false));
        if (mint.Decimals != 9 || mint.MintAuthority is not null || mint.FreezeAuthority is not null
            || holding.Mint != quote.MintAddress || holding.Owner != quote.RecipientAddress || !holding.Native || holding.Frozen
            || holding.Delegate is not null || holding.CloseAuthority is not null && holding.CloseAuthority != quote.RecipientAddress
            || holding.Amount < UInt64Text.Parse(quote.NativeAmount)
            || SolanaAccountData.ReadOwned(accounts[2], SystemProgram.ProgramIdKey.Key, false).Length != 0
            || (System.Numerics.BigInteger)accounts[2].GetProperty("lamports").GetUInt64()
                < (System.Numerics.BigInteger)UInt64Text.Parse(quote.TemporaryRent) + UInt64Text.Parse(quote.NetworkFee))
            throw new InvalidDataException("Recipient native-token authority, net balance or temporary funding is unavailable.");
    }

    private static byte[] CompileWttNativeOutput(WttOperationIntent intent, WttNativeOutputQuote quote,
        WttSolanaNativeOutputPlan plan, DurableSolanaPlan recovery, string preparationKey)
    {
        var built = WttSolanaNativeOutputWire.Build(intent, quote, preparationKey, new(plan.Guard.Program),
            Convert.FromBase64String(plan.Guard.RentBase64), plan.ComputeUnits, plan.ComputePrice);
        return CompileDurable(recovery, new(quote.RecipientAddress), built.Instructions);
    }
}
