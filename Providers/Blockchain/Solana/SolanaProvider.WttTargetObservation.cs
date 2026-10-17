using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;
using Solnet.Programs;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    /// <inheritdoc cref="IWttTargetModule.ObserveWttTargetAsync"/>
    public async Task<AZOAResult<WttStageObservation>> ObserveWttTargetAsync(WttOperationIntent intent, WttTargetProofs proofs,
        WttStageQuote quote, WttStagePreparation prepared, CancellationToken ct = default)
    {
        var (_, plan, wire) = await ReadWttTargetPreparationAsync(intent, proofs, quote, prepared, ct);
        try
        {
            var transaction = await ReadWttFinalizedTransactionAsync(prepared.TransactionId, ct);
            if (transaction.ValueKind == JsonValueKind.Null)
            {
                var height = await DurableRpcAsync("getBlockHeight", [new { commitment = "finalized" }], ct);
                return Ok(new WttStageObservation(height.GetInt64() > wire.Recovery.LastValidBlockHeight ? "NeedsOperator" : "Pending",
                    prepared.TransactionId, prepared.ProtocolIdentity, Detail: "Missing finalized target history is not proof of no target effect."));
            }
            var finalized = await RequireWttFinalityAsync(transaction, prepared, wire, plan.LookupTables, ct);
            if (finalized.Failed)
                return Ok(new WttStageObservation("FailedOnChain", prepared.TransactionId, prepared.ProtocolIdentity,
                    JsonSerializer.Serialize(new { finalized.BlockIdentity, noProtocolEffect = true }), NetworkFeePaid: finalized.Fee.ToString(CultureInfo.InvariantCulture)));
            var step = plan.Steps.Single(value => value.Stage == prepared.Stage);
            var proof = plan.Proofs.Single(value => value.Label == step.ProofLabel);
            var core = new PublicKey(plan.Core); var token = new PublicKey(plan.TokenBridge); var mint = new PublicKey(plan.Mint);
            async Task<byte[]> Account(PublicKey address, string owner)
            {
                var response = await DurableRpcAsync("getAccountInfo", [address.Key,
                    new { encoding = "base64", commitment = "finalized", minContextSlot = finalized.Slot }], ct);
                if (response.GetProperty("context").GetProperty("slot").GetUInt64() < finalized.Slot)
                    throw new InvalidDataException("Target account evidence predates the finalized stage.");
                return SolanaAccountData.ReadOwned(response.GetProperty("value"), owner, false);
            }
            if (step.Kind == "Verify")
            {
                var signatureSet = WttTargetRole(wire.StageKeys, ":signature-set");
                var bitmap = WttSolanaPostedEvidence.SignatureSet(await Account(signatureSet, core.Key), proof.Vaa, proof.Guardians.Count);
                if (proof.Vaa.Signatures.Skip(step.SignatureStart).Take(step.SignatureCount).Any(value => !bitmap[value.GuardianIndex]))
                    throw new InvalidDataException("Target signature-set account does not contain the exact verified guardian batch.");
            }
            else
            {
                var posted = WttSolanaPostedEvidence.PostedVaa(await Account(WttSolanaCoreInstructions.PostedVaa(core, proof.Vaa), core.Key), proof.Vaa);
                if (step.Kind == "Post" && posted.SignatureSet != WttTargetRole(wire.StageKeys, ":signature-set").Key)
                    throw new InvalidDataException("Posted body references another verification generation.");
                if (step.Kind is "Register" or "Redeem")
                {
                    var claim = WttSolanaAccounts.Claim(token, proof.Vaa.EmitterChain, Convert.FromHexString(proof.Vaa.EmitterAddress), UInt64Text.Parse(proof.Vaa.Sequence));
                    if (!(await Account(claim, token.Key)).AsSpan().SequenceEqual(new byte[] { 1 }))
                        throw new InvalidDataException("Canonical target claim is absent or malformed.");
                }
                if (step.Kind == "Register")
                {
                    var wrapped = WttSolanaAssetCodec.ReadWrapped(await Account(WttSolanaAccounts.Derive(token, "meta"u8.ToArray(), mint.KeyBytes), token.Key));
                    var asset = SolanaSplAssetCodec.ReadMint(await Account(mint, TokenProgram.ProgramIdKey.Key));
                    var attestation = WttPayloadCodec.ParseAttestation(Convert.FromHexString(proof.Vaa.PayloadHex));
                    if (wrapped.Chain != attestation.TokenChain || Convert.ToHexStringLower(wrapped.Address) != attestation.TokenAddress
                        || wrapped.OriginalDecimals != attestation.Decimals || asset.Decimals != Math.Min(attestation.Decimals, (byte)8)
                        || asset.MintAuthority != WttSolanaAccounts.Derive(token, "mint_signer"u8.ToArray()).Key || asset.FreezeAuthority is not null)
                        throw new InvalidDataException("Registered target mint differs from the exact attested origin or bridge authority.");
                }
                if (step.Kind == "Redeem") RequireWttTargetTokenEffect(intent, mint, finalized.Accounts.Keys, finalized.Meta);
            }
            var payer = WttTargetRole(wire.StageKeys, ":payer").Key;
            var payerIndex = finalized.Accounts.Keys.Select((key, index) => (key, index)).Single(value => value.key.Key == payer).index;
            var retained = finalized.Meta.GetProperty("postBalances")[payerIndex].GetUInt64();
            return Ok(new WttStageObservation("Confirmed", prepared.TransactionId, prepared.ProtocolIdentity,
                JsonSerializer.Serialize(new { finalized.BlockIdentity, stage = step.Kind, proof.Vaa.BodyDigest,
                    bridgePayer = payer, retainedLamports = retained.ToString(CultureInfo.InvariantCulture), refundDestination = intent.TargetAuthority.Address,
                    redemptionCompleted = step.Kind == "Redeem", recipientUnwrapStillRequired = step.Kind == "Redeem" && intent.UnwrapNative }),
                NetworkFeePaid: finalized.Fee.ToString(CultureInfo.InvariantCulture)));
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException or FormatException
            or KeyNotFoundException or InvalidOperationException or OverflowException)
        { return Ok(new WttStageObservation("NeedsOperator", prepared.TransactionId, prepared.ProtocolIdentity, Detail: exception.Message)); }
    }

    private static void RequireWttTargetTokenEffect(WttOperationIntent intent, PublicKey mint, IReadOnlyList<PublicKey> keys, JsonElement meta)
    {
        var recipient = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(new PublicKey(intent.RecipientAddress), mint);
        var feeAccount = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(new PublicKey(intent.TargetAuthority.Address), mint);
        var amount = WttPayloadCodec.ParseAmount(WttPayloadCodec.Denormalize(intent.Amount.Amount, intent.TargetDecimals));
        var fee = WttPayloadCodec.ParseAmount(WttPayloadCodec.Denormalize(intent.Amount.Fee, intent.TargetDecimals));
        if (amount > ulong.MaxValue || fee > amount) throw new InvalidDataException("Target amount exceeds the exact SPL range.");
        BigInteger Delta(PublicKey address, string owner)
        {
            var index = keys.Select((key, position) => (key, position)).Single(value => value.key.Key == address.Key).position;
            var before = WttTokenBalance(meta.GetProperty("preTokenBalances"), index, mint.Key, owner, intent.TargetDecimals, true);
            var after = WttTokenBalance(meta.GetProperty("postTokenBalances"), index, mint.Key, owner, intent.TargetDecimals, false);
            return new BigInteger(after) - before;
        }
        if (Delta(recipient, intent.RecipientAddress) != (recipient.Key == feeAccount.Key ? amount : amount - fee)
            || recipient.Key != feeAccount.Key && Delta(feeAccount, intent.TargetAuthority.Address) != fee)
            throw new InvalidDataException("Finalized recipient or target payer token credit differs from the immutable transfer.");
        if (intent.Asset.OriginChain == 1)
        {
            var token = new PublicKey(intent.Target.TokenBridge); var custody = WttSolanaAccounts.Derive(token, mint.KeyBytes);
            if (Delta(custody, WttSolanaAccounts.Derive(token, "custody_signer"u8.ToArray()).Key) != -amount)
                throw new InvalidDataException("Original token custody did not release the exact redeemed amount.");
        }
    }
}
