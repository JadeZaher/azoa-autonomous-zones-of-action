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
    /// <summary>Finalized blocks one reconcile pass walks while proving a plan never landed (the cursor resumes the rest).</summary>
    private const int ClientPlanScanBlocksPerPass = 32;

    /// <summary>Upper bound on blocks between the plan's context block and its expiry (a blockhash lives ~150 blocks).</summary>
    private const ulong ClientPlanMaximumValidityBlocks = 1024;

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
        string? evidenceJson, CancellationToken ct)
    {
        var message = ClientPlanMessage(plan, out var durable);
        if (await VerifyGenesisAsync(ct) != durable.Genesis)
            return AZOAResult<ClientSigningObservation>.Failure("The saved plan belongs to another Solana cluster.");
        var pending = Observation(ClientSigningVerdicts.Pending, transactionId, null, evidenceJson);
        if (transactionId is not null)
        {
            var status = (await DurableRpcAsync("getSignatureStatuses", [new[] { transactionId }, new { searchTransactionHistory = true }], ct))
                .GetProperty("value")[0];
            // RV-L5-02: a null status only means this node lacks the signature; absence is proven by the ancestry scan below.
            if (status.ValueKind != JsonValueKind.Null)
            {
                if (status.GetProperty("confirmationStatus").GetString() != "finalized") return pending;
                return await PlannedOutcomeAsync(transactionId, message, ct) ?? AZOAResult<ClientSigningObservation>.Failure(
                    "The finalized transaction does not carry the saved message.");
            }
        }
        if ((await DurableRpcAsync("getBlockHeight", [new { commitment = "finalized" }], ct)).GetInt64() <= durable.LastValidBlockHeight)
            return pending;
        return await ProveClientPlanAbsenceAsync(message, durable, transactionId, evidenceJson, ct);
    }

    /// <summary>
    /// RV-L5-02: the reviewed WTT finalized-ancestry scan, started at the first block past the blockhash expiry and resumed from
    /// <paramref name="evidenceJson"/>. Only a complete scan settles <c>Expired</c>; a node without that history yields <c>Unproven</c>.
    /// </summary>
    private async Task<AZOAResult<ClientSigningObservation>> ProveClientPlanAbsenceAsync(byte[] message, DurableSolanaPlan durable,
        string? transactionId, string? evidenceJson, CancellationToken ct)
    {
        WttMessageHistoryCursor? cursor;
        string? signed;
        try
        {
            var previous = ReadWttMessageCursor(evidenceJson);
            var upper = previous is null ? await FirstSlotPastValidityAsync(durable, ct) : (ulong?)null;
            (cursor, signed) = await ScanWttMessageHistoryAsync(message, durable, previous, ct, upper, ClientPlanScanBlocksPerPass);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or JsonException
            or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            return Observation(ClientSigningVerdicts.Unproven, transactionId,
                "The blockhash expired, but this node cannot prove from finalized history that the plan never landed: " + exception.Message,
                evidenceJson);
        }
        if (signed is not null)
        {
            var signature = Encoders.Base58.EncodeData(Convert.FromBase64String(signed).AsSpan(1, 64).ToArray());
            return await PlannedOutcomeAsync(signature, message, ct)
                ?? Observation(ClientSigningVerdicts.Pending, signature, "The plan's message is in a finalized block; its outcome is not yet readable.", evidenceJson);
        }
        if (cursor is null) return Observation(ClientSigningVerdicts.Pending, transactionId, null, evidenceJson);
        if (!cursor.Complete)
            return Observation(ClientSigningVerdicts.Pending, transactionId, $"Scanned {cursor.ScannedBlocks} finalized blocks toward the plan's context slot.",
                WttCanonical(new { format = WttMessageScanFormat, history = cursor }));
        return Observation(ClientSigningVerdicts.Expired, null,
            "Every finalized block from the plan's context slot past its blockhash expiry lacks the plan's message.");
    }

    /// <summary>The finalized slot whose block height is the first past the plan's last valid height (consecutive heights per block).</summary>
    private async Task<ulong> FirstSlotPastValidityAsync(DurableSolanaPlan plan, CancellationToken ct)
    {
        var first = await DurableRpcAsync("getBlocksWithLimit", [(ulong)plan.ContextSlot, 1, new { commitment = "finalized" }], ct);
        if (first.ValueKind != JsonValueKind.Array || first.GetArrayLength() == 0 || !first[0].TryGetUInt64(out var start))
            throw new InvalidDataException("Finalized block history from the plan's context slot is unavailable.");
        var head = JupiterBlockHeader(await DurableRpcAsync("getBlock", [start, new { encoding = "base64", transactionDetails = "none",
            rewards = false, commitment = "finalized", maxSupportedTransactionVersion = 0 }], ct), start);
        if (head.Height > (ulong)plan.LastValidBlockHeight) return start;
        var offset = (ulong)plan.LastValidBlockHeight + 1 - head.Height;
        if (offset > ClientPlanMaximumValidityBlocks)
            throw new InvalidDataException("The plan's validity window is wider than any blockhash allows.");
        var slots = await DurableRpcAsync("getBlocksWithLimit", [start, offset + 1, new { commitment = "finalized" }], ct);
        if (slots.ValueKind != JsonValueKind.Array || (ulong)slots.GetArrayLength() <= offset || !slots[(int)offset].TryGetUInt64(out var upper))
            throw new InvalidDataException("Finalized block history past the plan's validity is unavailable.");
        return upper;
    }

    private static AZOAResult<ClientSigningObservation> Observation(string verdict, string? transactionId, string? detail, string? evidenceJson)
        => AZOAResult<ClientSigningObservation>.Success(new(verdict, transactionId, detail, evidenceJson));

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
