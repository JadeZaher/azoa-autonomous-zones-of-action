using System.Text.Json;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain.ClientSigning;
using Solnet.Rpc.Models;
using Solnet.Wallet;
using Solnet.Wallet.Utilities;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider : IClientSignedTransferModule
{
    /// <summary>Signatures inspected when proving an unsubmitted plan never landed; more recent traffic leaves it pending.</summary>
    private const int ClientPlanSearchLimit = 100;

    private sealed record SolanaClientPlan(DurableSolanaPlan Plan, ValueTransferIntent Intent);

    /// <inheritdoc/>
    public async Task<AZOAResult<ClientSigningPlan>> PlanClientSignedTransferAsync(ValueTransferIntent intent, CancellationToken ct)
    {
        var planned = await PlanValueMessageAsync(intent, ct);
        if (planned.IsError) return AZOAResult<ClientSigningPlan>.Failure(planned.Message);
        var (plan, message) = planned.Result;
        return AZOAResult<ClientSigningPlan>.Success(new(1, ChainType, ActiveNetwork.ToString(), "solana:" + plan.Genesis,
            ClientSigningFormats.Solana, intent.Sender, Convert.ToBase64String(message), CanonicalJson.Hash(intent),
            ValidThroughHeight: plan.LastValidBlockHeight, RecoveryJson: JsonSerializer.Serialize(new SolanaClientPlan(plan, intent))));
    }

    /// <inheritdoc/>
    public AZOAResult<string> VerifyClientSignedTransfer(ClientSigningPlan plan, string signedTransactionBase64)
    {
        try
        {
            var message = ClientPlanMessage(plan, out _);
            var wire = Convert.FromBase64String(signedTransactionBase64);
            var transaction = Transaction.Deserialize(wire);
            if (transaction.Signatures.Count != 1) return AZOAResult<string>.Failure("A Solana transfer carries exactly the fee payer's signature.");
            var id = Encoders.Base58.EncodeData(transaction.Signatures[0].Signature);
            return MatchesDurableWire(signedTransactionBase64, id, message, out _) ? AZOAResult<string>.Success(id)
                : AZOAResult<string>.Failure("Signed bytes are not the saved Solana message signed by its fee payer.");
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or IndexOutOfRangeException
            or InvalidOperationException or JsonException)
        {
            return AZOAResult<string>.Failure("Signed bytes are not the saved Solana message signed by its fee payer.");
        }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<string>> SubmitClientSignedTransferAsync(ClientSigningPlan plan, string signedTransactionBase64, CancellationToken ct)
    {
        var verified = VerifyClientSignedTransfer(plan, signedTransactionBase64);
        if (verified.IsError) return verified;
        ClientPlanMessage(plan, out var durable);
        try
        {
            await SubmitDurableAsync(signedTransactionBase64, verified.Result!, durable, ct);
            return verified;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            return AZOAResult<string>.FailureWithCode("The cluster did not accept the broadcast; reconcile the saved transaction.",
                ClientSigningCodes.BroadcastUnconfirmed);
        }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<ClientSigningObservation>> ObserveClientSignedTransferAsync(ClientSigningPlan plan, string? transactionId,
        CancellationToken ct)
    {
        var message = ClientPlanMessage(plan, out var durable);
        if (await VerifyGenesisAsync(ct) != durable.Genesis)
            return AZOAResult<ClientSigningObservation>.Failure("The saved plan belongs to another Solana cluster.");
        var pending = AZOAResult<ClientSigningObservation>.Success(new(ClientSigningVerdicts.Pending, transactionId));
        var expired = (await DurableRpcAsync("getBlockHeight", [new { commitment = "finalized" }], ct)).GetInt64() > durable.LastValidBlockHeight;
        if (transactionId is not null)
        {
            var status = (await DurableRpcAsync("getSignatureStatuses", [new[] { transactionId }, new { searchTransactionHistory = true }], ct))
                .GetProperty("value")[0];
            if (status.ValueKind == JsonValueKind.Null)
                return expired ? AZOAResult<ClientSigningObservation>.Success(new(ClientSigningVerdicts.Expired, null,
                    "The blockhash expired and the cluster never saw the plan's signature.")) : pending;
            if (status.GetProperty("confirmationStatus").GetString() != "finalized") return pending;
            return await PlannedOutcomeAsync(transactionId, message, ct) ?? AZOAResult<ClientSigningObservation>.Failure(
                "The finalized transaction does not carry the saved message.");
        }
        if (!expired) return pending;
        var signatures = await DurableRpcAsync("getSignaturesForAddress",
            [plan.Signer, new { limit = ClientPlanSearchLimit, commitment = "finalized" }], ct);
        var candidates = signatures.EnumerateArray().ToArray();
        foreach (var candidate in candidates.Where(item => item.GetProperty("slot").GetInt64() >= durable.ContextSlot))
        {
            var outcome = await PlannedOutcomeAsync(candidate.GetProperty("signature").GetString()!, message, ct);
            if (outcome is not null) return outcome;
        }
        var searchedPastPlan = candidates.Length < ClientPlanSearchLimit
            || candidates.Any(item => item.GetProperty("slot").GetInt64() < durable.ContextSlot);
        return searchedPastPlan ? AZOAResult<ClientSigningObservation>.Success(new(ClientSigningVerdicts.Expired, null,
            "The blockhash expired and no finalized transaction from the signer since the plan carries its message.")) : pending;
    }

    /// <summary>The finalized outcome when <paramref name="signature"/> carries exactly the planned message, else null.</summary>
    private async Task<AZOAResult<ClientSigningObservation>?> PlannedOutcomeAsync(string signature, byte[] message, CancellationToken ct)
    {
        var transaction = await DurableRpcAsync("getTransaction",
            [signature, new { encoding = "base64", commitment = "finalized", maxSupportedTransactionVersion = 0 }], ct);
        if (transaction.ValueKind == JsonValueKind.Null) return null;
        var wire = Convert.FromBase64String(transaction.GetProperty("transaction")[0].GetString()!);
        if (!Transaction.Deserialize(wire).CompileMessage().AsSpan().SequenceEqual(message)) return null;
        var failed = transaction.GetProperty("meta").GetProperty("err").ValueKind != JsonValueKind.Null;
        return AZOAResult<ClientSigningObservation>.Success(new(failed ? ClientSigningVerdicts.FailedOnChain : ClientSigningVerdicts.Confirmed, signature));
    }

    /// <summary>Recompiles the saved plan and refuses one whose recovery fields do not reproduce its recorded message.</summary>
    private byte[] ClientPlanMessage(ClientSigningPlan plan, out DurableSolanaPlan durable)
    {
        if (plan.Version != 1 || plan.ChainType != ChainType || plan.Network != ActiveNetwork.ToString() || plan.Format != ClientSigningFormats.Solana
            || plan.RecoveryJson is null || plan.ValidThroughHeight is null)
            throw new InvalidOperationException("The saved plan is not a Solana client-signing plan for this network.");
        var saved = JsonSerializer.Deserialize<SolanaClientPlan>(plan.RecoveryJson)
            ?? throw new InvalidOperationException("The saved Solana plan is unreadable.");
        durable = saved.Plan;
        var message = CompileDurable(durable, new PublicKey(saved.Intent.Sender), ValueInstructions(saved.Intent, durable));
        if (!ValidValueIntent(saved.Intent) || saved.Intent.Sender != plan.Signer || durable.IntentHash != IntentDigest(saved.Intent)
            || durable.LastValidBlockHeight != plan.ValidThroughHeight || plan.ProtocolIdentity != "solana:" + durable.Genesis
            || durable.Genesis != ExpectedGenesis || Convert.ToBase64String(message) != plan.UnsignedPayloadBase64)
            throw new InvalidOperationException("The saved Solana plan does not reproduce its recorded message.");
        return message;
    }
}
