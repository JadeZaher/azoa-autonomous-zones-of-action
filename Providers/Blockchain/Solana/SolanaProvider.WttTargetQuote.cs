using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;
using Microsoft.Extensions.Configuration;
using Solnet.Programs;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider : IWttTargetModule
{
    /// <inheritdoc cref="IWttTargetModule.QuoteWttTargetAsync"/>
    public async Task<AZOAResult<WttStageQuote>> QuoteWttTargetAsync(WttOperationIntent intent, WttTargetProofs proofs,
        CancellationToken ct = default)
    {
        if (!ValidWttTargetIntent(intent) || proofs?.Transfer is null)
            return Error<WttStageQuote>("Exact target authority, recipient and transfer proof are required.");
        try
        {
            var transfer = WttProofBinding.Transfer(intent, proofs.Transfer);
            var inspected = await InspectWttTargetAsync(intent.Asset, intent.RecipientAddress, intent.UnwrapNative, ct);
            if (inspected.IsError || inspected.Result is null) return Error<WttStageQuote>(inspected.Message);
            var target = inspected.Result;
            if (target.Network != intent.Target || target.Decimals != intent.TargetDecimals
                || target.RecipientProtocolAddress != intent.RecipientProtocolAddress
                || intent.TargetAssetAddress is not null && target.AssetAddress != intent.TargetAssetAddress)
                throw new InvalidDataException("Target representation, token-account recipient or deployment differs from frozen consent.");
            var deployment = ReadWttSolanaDeployment() ?? throw new InvalidDataException("Pinned target deployment is unavailable.");
            var mint = intent.Asset.OriginChain == 1 ? new PublicKey(Convert.FromHexString(intent.Asset.OriginAddress))
                : WttSolanaAccounts.WrappedMint(deployment.TokenBridge, intent.Asset.OriginChain, Convert.FromHexString(intent.Asset.OriginAddress));
            var user = new PublicKey(intent.TargetAuthority.Address);
            var feeAta = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(user, mint);
            var claim = WttSolanaAccounts.Claim(deployment.TokenBridge, transfer.EmitterChain,
                Convert.FromHexString(transfer.EmitterAddress), UInt64Text.Parse(transfer.Sequence));
            var snapshot = await ReadWttSolanaSnapshotAsync(deployment, [claim, feeAta], ct);
            if (WttDescriptor(snapshot) != intent.Target || snapshot.Accounts[0].ValueKind != JsonValueKind.Null)
                throw new InvalidDataException("Target deployment changed or this source claim already exists; reconcile its owned operation.");
            var createFeeAta = snapshot.Accounts[1].ValueKind == JsonValueKind.Null;
            if (!createFeeAta)
            {
                var holding = SolanaSplAssetCodec.ReadHolding(SolanaAccountData.ReadOwned(snapshot.Accounts[1], TokenProgram.ProgramIdKey.Key, false));
                if (holding.Mint != mint.Key || holding.Owner != user.Key || holding.Frozen)
                    throw new InvalidDataException("Target payer fee token account differs from its immutable mint or owner.");
            }
            var guard = await ReadWttSolanaGuardAsync(ct);
            var metadata = target.Registered ? null : await ReadWttMetadataPolicyAsync(guard, ct);
            var verified = new List<WttSolanaTargetProof>();
            if (!target.Registered)
            {
                if (proofs.Attestation is null) throw new InvalidDataException("Original-chain metadata attestation is required before wrapped registration.");
                verified.Add(await ReadWttTargetProofAsync(intent, WttProofBinding.Attestation(intent, proofs.Attestation), "Attestation", 0, ct));
            }
            verified.Add(await ReadWttTargetProofAsync(intent, transfer, "Transfer", 0, ct));
            var prefix = $"AZOA:Solana:Wtt:Networks:{ActiveNetwork}:";
            if (!uint.TryParse(_config[prefix + "MaximumComputeUnits"], NumberStyles.None, CultureInfo.InvariantCulture, out var ceiling)
                || ceiling is 0 or > 1400000 || !UInt64Text.TryParse(_config[prefix + "MicroLamportsPerComputeUnit"], out var price))
                throw new InvalidDataException("An explicit bounded target compute policy is required.");
            var tables = await ReadLookupTablesAsync(_config.GetSection(prefix + "LookupTables").Get<string[]>() ?? [], snapshot.Slot, ct);
            var steps = new List<WttSolanaTargetStep>(); var hash = CanonicalJson.Hash(intent);
            var plan = new WttSolanaTargetPlan(1, hash, user.Key, deployment.Core.Key, deployment.TokenBridge.Key,
                deployment.CoreHash, deployment.TokenHash, guard, snapshot.MessageFee, mint.Key, verified, tables, steps, metadata);
            var rent = Convert.FromBase64String(guard.RentBase64);
            var recovery = await NewPlanAsync("", 0, 0, false, hash, ct);
            ulong Minimum(int size) => WttSolanaCostGuard.MinimumBalance(rent, size);
            async Task Add(string stage, string kind, WttSolanaTargetProof proof, int start, int count, ulong accountRent,
                ulong levy = 0, bool recipientAta = false, bool payerAta = false)
            {
                var retained = checked(Minimum(0) + accountRent);
                var ataRent = checked((recipientAta ? Minimum(165) : 0) + (payerAta && user.Key != intent.RecipientAddress ? Minimum(165) : 0));
                var step = new WttSolanaTargetStep(stage, kind, proof.Label, start, count, checked(retained + levy),
                    recipientAta, payerAta, ceiling, price,
                    new(stage, "0", checked(retained + ataRent).ToString(CultureInfo.InvariantCulture), levy.ToString(CultureInfo.InvariantCulture), user.Key));
                var payer = WttQuotePlaceholder(intent.OperationId, stage, "payer");
                var signatureSet = kind is "Verify" or "Post" ? WttQuotePlaceholder(intent.OperationId, proof.Label, "signature-set") : null;
                var compiled = CompileWttTarget(intent, plan, step, recovery, payer, signatureSet);
                if (steps.Count == 0 && await MeasureSolanaComputeAsync(compiled, checked((long)snapshot.Slot), ct) > ceiling)
                    throw new InvalidDataException("Current target stage exceeds its explicit guarded compute ceiling.");
                var fee = await NativeMessageFeeAsync(compiled, ct);
                steps.Add(step with { Reservation = step.Reservation with { NetworkFee = fee.ToString(CultureInfo.InvariantCulture) } });
                var cleanup = CompileDurable(recovery, user, [SystemProgram.Transfer(payer, user, 1)]);
                var cleanupFee = await NativeMessageFeeAsync(cleanup, ct);
                steps.Add(new("Cleanup:" + stage, "Cleanup", proof.Label, 0, 0, 0, false, false, 0, 0,
                    new("Cleanup:" + stage, cleanupFee.ToString(CultureInfo.InvariantCulture), "0", "0", user.Key)));
            }
            foreach (var proof in verified)
            {
                if (!proof.AlreadyPosted)
                {
                    // Small guardian batches leave packet capacity for the immutable cost guard.
                    for (var offset = 0; offset < proof.Vaa.Signatures.Count; offset += 3)
                        await Add("Verify:" + proof.Label + ":" + offset.ToString(CultureInfo.InvariantCulture), "Verify", proof,
                            offset, Math.Min(3, proof.Vaa.Signatures.Count - offset), Minimum(40 + proof.Guardians.Count));
                    await Add("Post:" + proof.Label, "Post", proof, 0, 0, Minimum(95 + proof.Vaa.PayloadHex.Length / 2));
                }
                if (proof.Label == "Attestation")
                {
                    var costs = metadata!.Costs(rent);
                    await Add("Register", "Register", proof, 0, 0, checked(Minimum(1) + Minimum(82) + Minimum(35) + costs.RetainedRent), costs.ProtocolLevy);
                }
                else await Add("Redeem", "Redeem", proof, 0, 0, Minimum(1), recipientAta: !target.RecipientReady, payerAta: createFeeAta);
            }
            var budget = intent.TargetBudget;
            if (steps.Aggregate(BigInteger.Zero, (sum, value) => sum + WttPayloadCodec.ParseAmount(value.Reservation.NetworkFee)) > WttPayloadCodec.ParseAmount(budget.MaximumNetworkFee)
                || steps.Aggregate(BigInteger.Zero, (sum, value) => sum + WttPayloadCodec.ParseAmount(value.Reservation.LockedDeposit)) > WttPayloadCodec.ParseAmount(budget.MaximumLockedDeposit)
                || steps.Aggregate(BigInteger.Zero, (sum, value) => sum + WttPayloadCodec.ParseAmount(value.Reservation.Value)) > WttPayloadCodec.ParseAmount(budget.MaximumValue))
                throw new InvalidDataException("Complete target registration, redemption and cleanup reservations exceed immutable caller limits.");
            var json = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(plan)); var expires = DateTimeOffset.UtcNow.AddSeconds(30);
            var id = CanonicalJson.Hash(new { Version = 1, IntentHash = hash, Plan = json, ExpiresAt = expires });
            return Ok(new WttStageQuote(id, hash, intent.Target.ProtocolIdentity, "Target", steps.Select(value => value.Reservation).ToArray(), json, expires));
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or FormatException or OverflowException)
        { return Error<WttStageQuote>(exception.Message); }
    }
}
