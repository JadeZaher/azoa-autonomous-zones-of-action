using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using Solnet.Programs;
using Solnet.Rpc.Models;
using Solnet.Wallet;
using Solnet.Wallet.Utilities;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    /// <inheritdoc/>
    public async Task<AZOAResult<PreparedValueTransfer>> PrepareValueTransferAsync(ValueTransferIntent intent, CancellationToken ct)
    {
        _ = RpcHttpClient;
        if (!ValidValueIntent(intent)) return AZOAResult<PreparedValueTransfer>.Failure("Invalid Solana value-transfer intent.");
        var mint = intent.AssetId == "SOL" ? null : new PublicKey(intent.AssetId);
        byte decimals = 9;
        var createDestination = false;
        if (mint is not null)
        {
            var mintAccount = await ReadParsedAccountAsync(mint.Key, ct);
            if (mintAccount is null) return AZOAResult<PreparedValueTransfer>.Failure("Solana token mint does not exist.");
            var info = TokenInfo(mintAccount.Value, "mint");
            decimals = info.GetProperty("decimals").GetByte();
            if (!info.GetProperty("isInitialized").GetBoolean()) return AZOAResult<PreparedValueTransfer>.Failure("Solana mint is uninitialized.");
            var source = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(new PublicKey(intent.Sender), mint);
            var sourceAccount = await ReadParsedAccountAsync(source.Key, ct);
            if (sourceAccount is null || !ValidHolding(sourceAccount.Value, intent.Sender, mint.Key, false, out var amount)
                || amount < ulong.Parse(intent.QuantityBaseUnits, System.Globalization.CultureInfo.InvariantCulture))
                return AZOAResult<PreparedValueTransfer>.Failure("Solana source token account is unavailable or insufficient.");
            var destination = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(new PublicKey(intent.Recipient), mint);
            var destinationAccount = await ReadParsedAccountAsync(destination.Key, ct);
            createDestination = destinationAccount is null;
            if (destinationAccount is not null && !ValidHolding(destinationAccount.Value, intent.Recipient, mint.Key, false, out _))
                return AZOAResult<PreparedValueTransfer>.Failure("Solana destination token account is incompatible.");
        }
        var plan = await NewPlanAsync(mint?.Key ?? "SOL", 0, decimals, createDestination, IntentDigest(intent), ct);
        var message = CompileDurable(plan, new PublicKey(intent.Sender), ValueInstructions(intent, plan));
        var signed = await SignDurableAsync(message, new Dictionary<string, SigningContext> { [intent.Sender] = intent.SigningContext }, ct);
        if (signed.IsError || signed.Result is null) return AZOAResult<PreparedValueTransfer>.Failure(signed.Message);
        var txid = Encoders.Base58.EncodeData(Transaction.Deserialize(signed.Result).Signatures[0].Signature);
        return AZOAResult<PreparedValueTransfer>.Success(new(txid, Convert.ToBase64String(signed.Result), "solana:" + plan.Genesis, intent, JsonSerializer.Serialize(plan)));
    }

    /// <inheritdoc/>
    public async Task SubmitValueTransferAsync(PreparedValueTransfer prepared, CancellationToken ct)
    {
        var plan = ValidatePreparedValue(prepared);
        await SubmitDurableAsync(prepared.SignedTransactionBase64, prepared.TransactionId, plan, ct);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<ChainConfirmation>> ObserveValueTransferAsync(PreparedValueTransfer prepared, CancellationToken ct)
    {
        var plan = ValidatePreparedValue(prepared);
        var status = await ObserveDurableAsync(prepared.TransactionId, prepared.SignedTransactionBase64, plan, ct);
        return AZOAResult<ChainConfirmation>.Success(status switch
        {
            "Confirmed" => ChainConfirmation.Confirmed,
            "FailedOnChain" => ChainConfirmation.FailedOnChain,
            _ => ChainConfirmation.Unknown,
        });
    }

    private bool ValidValueIntent(ValueTransferIntent intent) => intent.ChainType == ChainType && intent.Network == ActiveNetwork
        && TryParsePublicKey(intent.Sender, out _) && TryParsePublicKey(intent.Recipient, out _)
        && (intent.AssetId == "SOL" || TryParsePublicKey(intent.AssetId, out _))
        && intent.Sender != intent.Recipient && CanonicalAmount(intent.QuantityBaseUnits, out _)
        && !string.IsNullOrWhiteSpace(intent.PreparationKey);

    private DurableSolanaPlan ValidatePreparedValue(PreparedValueTransfer prepared)
    {
        _ = RpcHttpClient;
        var plan = ReadPlan(prepared.RecoveryMetadata);
        if (!ValidValueIntent(prepared.Intent) || plan is null || plan.IntentHash != IntentDigest(prepared.Intent)
            || plan.Mint != prepared.Intent.AssetId || plan.Genesis != ExpectedGenesis || prepared.ProtocolIdentity != "solana:" + plan.Genesis
            || plan.LastValidBlockHeight <= 0 || plan.ContextSlot < 0 || plan.MintRent != 0
            || !MatchesDurableWire(prepared.SignedTransactionBase64, prepared.TransactionId,
                CompileDurable(plan, new PublicKey(prepared.Intent.Sender), ValueInstructions(prepared.Intent, plan)), out _))
            throw new InvalidOperationException("Persisted Solana value transaction differs from its authorized intent.");
        return plan;
    }

    private static List<TransactionInstruction> ValueInstructions(ValueTransferIntent intent, DurableSolanaPlan plan)
    {
        var sender = new PublicKey(intent.Sender); var recipient = new PublicKey(intent.Recipient);
        var amount = ulong.Parse(intent.QuantityBaseUnits, System.Globalization.CultureInfo.InvariantCulture);
        var instructions = new List<TransactionInstruction>();
        if (intent.AssetId == "SOL") instructions.Add(SystemProgram.Transfer(sender, recipient, amount));
        else
        {
            var mint = new PublicKey(intent.AssetId);
            if (plan.CreateDestination) instructions.Add(AssociatedTokenAccountProgram.CreateAssociatedTokenAccount(sender, recipient, mint));
            instructions.Add(TokenProgram.TransferChecked(AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(sender, mint),
                AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(recipient, mint), amount, plan.Decimals, sender, mint));
        }
        instructions.Add(MemoProgram.NewMemoV2("azoa:value:" + plan.IntentHash, sender));
        return instructions;
    }

    private static bool ValidHolding(JsonElement account, string owner, string mint, bool frozen, out ulong amount)
    {
        var info = TokenInfo(account, "account");
        amount = 0;
        return info.GetProperty("owner").GetString() == owner && info.GetProperty("mint").GetString() == mint
            && info.GetProperty("state").GetString() == (frozen ? "frozen" : "initialized")
            && ulong.TryParse(info.GetProperty("tokenAmount").GetProperty("amount").GetString(), out amount);
    }
}
