using System.Buffers.Binary;
using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    /// <inheritdoc cref="IWttTargetModule.QuoteWttTargetAsync"/>
    public async Task<AZOAResult<WttStageQuote>> QuoteWttTargetAsync(WttOperationIntent intent,
        WttTargetProofs proofs, CancellationToken ct = default)
    {
        try
        {
            if (proofs?.Transfer?.Vaa is null) return Error<WttStageQuote>("An exact source transfer proof is required.");
            var transfer = await ReadWttAlgoTargetProofAsync(intent, proofs.Transfer, "Transfer", ct);
            if (transfer.Consumed) return Error<WttStageQuote>("This canonical transfer body is already consumed; reconcile its original target receipt.");
            var inspection = await InspectWttTargetAsync(intent.Asset, intent.RecipientAddress, intent.UnwrapNative, ct);
            if (inspection.IsError || inspection.Result is null) return Error<WttStageQuote>(inspection.Message);
            if (inspection.Result.Network != intent.Target || inspection.Result.Decimals != intent.TargetDecimals
                || intent.TargetAssetAddress is not null && inspection.Result.AssetAddress != intent.TargetAssetAddress)
                return Error<WttStageQuote>("The target representation differs from the frozen transfer intent.");
            WttAlgoTargetProof? attestation = null;
            if (!inspection.Result.Registered)
            {
                if (proofs.Attestation?.Vaa is null) return Error<WttStageQuote>("Wrapped registration requires its own exact verified metadata attestation.");
                attestation = await ReadWttAlgoTargetProofAsync(intent, proofs.Attestation, "Attestation", ct);
                if (attestation.Consumed) return Error<WttStageQuote>("Consumed registration proof has no matching target asset; reconcile authoritative state.");
            }
            var snapshot = await ReadWttAlgoSnapshotAsync(null, 0, null, ct);
            if (WttAlgoDescriptor(snapshot) != intent.Target) return Error<WttStageQuote>("Target deployment changed during quotation.");
            var parameters = await ReadWttAlgorandJsonAsync("/v2/transactions/params", ct);
            var minimum = WttAlgoUnsigned(parameters, "min-fee");
            if (WttAlgoText(parameters, "genesis-hash") != snapshot.Deployment.Genesis
                || WttAlgoUnsigned(parameters, "last-round") != snapshot.Round || minimum == 0)
                return Error<WttStageQuote>("Target fee evidence differs from the current physical snapshot.");
            var asset = inspection.Result.AssetAddress is null ? (ulong?)null : UInt64Text.Parse(inspection.Result.AssetAddress);
            var custodyIndex = intent.Asset.OriginChain == 8 ? BinaryPrimitives.ReadUInt64BigEndian(Convert.FromHexString(intent.Asset.OriginAddress).AsSpan(24)) : intent.Asset.OriginChain;
            var custodyEmitter = intent.Asset.OriginChain == 8 ? "native"u8.ToArray() : Convert.FromHexString(intent.Asset.OriginAddress);
            var custody = WttAlgorandStorage.Address(snapshot.Deployment.Token, custodyIndex, custodyEmitter);
            var steps = new List<WttAlgorandTargetStep>(); var storageAddresses = new HashSet<string>(StringComparer.Ordinal);
            if (attestation is not null) await AddSequenceAsync(attestation, "Attestation", ct);
            await AddSequenceAsync(transfer, "Transfer", ct);
            if (attestation is not null)
            {
                await AddStorageAsync("WrappedStorage", "Attestation", snapshot.Deployment.Token, custodyIndex, custodyEmitter, ct);
                steps.Add(new("Register", "Attestation", 0, 0, "", new("Register", "0", "100000", "0", intent.TargetAuthority.Address)));
            }
            if (asset is > 0 && !inspection.Result.RecipientReady)
            {
                var recipient = await ReadWttAlgorandJsonAsync("/v2/accounts/" + intent.RecipientAddress, ct, allowMissing: true);
                if (recipient.ValueKind != JsonValueKind.Undefined)
                {
                    if (WttAlgoUnsigned(recipient, "round") != snapshot.Round || WttAlgoText(recipient, "address") != intent.RecipientAddress)
                        throw new InvalidDataException("Recipient prerequisite evidence changed during quotation.");
                    if (recipient.TryGetProperty("assets", out var holdings) && holdings.ValueKind == JsonValueKind.Array
                        && holdings.EnumerateArray().Any(item => WttAlgoUnsigned(item, "asset-id") == asset.Value))
                        throw new InvalidDataException("Existing frozen recipient holding requires authoritative unfreezing, not another opt-in.");
                }
            }
            if (asset != 0 && !inspection.Result.RecipientReady)
                steps.Add(new("RecipientOptIn", "Transfer", 0, 0, "", new("RecipientOptIn", "0", "100000", "0", intent.RecipientAddress)));
            steps.Add(new("Redeem", "Transfer", 0, 0, "", new("Redeem", "0", "0", "0", intent.TargetAuthority.Address)));
            var deployment = snapshot.Deployment;
            var plan = new WttAlgorandTargetPlan(1, CanonicalJson.Hash(intent), deployment.Genesis, WttAlgoText(parameters, "genesis-id"),
                deployment.Core, deployment.Token, deployment.CoreApproval, deployment.CoreClear, deployment.TokenApproval, deployment.TokenClear,
                asset, inspection.Result.Decimals, custody, transfer.Proof, attestation?.Proof, minimum, minimum, checked(minimum * 2),
                checked(minimum * (ulong)(((attestation?.Proof.Vaa.Signatures.Count ?? 0) + 5) / 6 + 1)), checked(minimum * 2),
                checked(minimum * (ulong)((transfer.Proof.Vaa.Signatures.Count + 5) / 6 + 1)),
                checked(minimum * (intent.Amount.Fee == "0" ? 2UL : 3UL)), steps);
            plan = PriceWttAlgoTargetPlan(intent, plan, WttAlgoUnsigned(parameters, "fee"), snapshot.Round);
            RequireWttAlgoBudget(plan.Steps.Select(item => item.Reservation), intent.TargetBudget);
            var payer = await ReadWttAlgorandJsonAsync("/v2/accounts/" + intent.TargetAuthority.Address, ct);
            if (WttAlgoText(payer, "address") != intent.TargetAuthority.Address || WttAlgoUnsigned(payer, "round") != snapshot.Round
                || payer.TryGetProperty("auth-addr", out var rekey) && (rekey.ValueKind != JsonValueKind.String || rekey.GetString() != intent.TargetAuthority.Address))
                return Error<WttStageQuote>("Target payer account or signing authority differs from its current snapshot.");
            var payerCost = plan.Steps.Where(item => item.Reservation.ParticipantAddress == intent.TargetAuthority.Address)
                .Aggregate(0UL, (sum, item) => checked(sum + UInt64Text.Parse(item.Reservation.NetworkFee)
                    + UInt64Text.Parse(item.Reservation.LockedDeposit) + UInt64Text.Parse(item.Reservation.Value)));
            if (WttAlgoUnsigned(payer, "amount") < checked(WttAlgoUnsigned(payer, "min-balance") + payerCost))
                return Error<WttStageQuote>("Target payer must fund the disclosed fees and deposits before preparation.");
            await RequireWttAlgoRoundAsync(snapshot.Round, ct);
            var encoded = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(plan));
            var expiry = DateTimeOffset.UtcNow.AddSeconds(30);
            var id = CanonicalJson.Hash(new { Version = 1, IntentHash = plan.IntentHash, Plan = encoded, ExpiresAt = expiry });
            return Ok(new WttStageQuote(id, plan.IntentHash, intent.Target.ProtocolIdentity, "Target",
                plan.Steps.Select(item => item.Reservation).ToArray(), encoded, expiry));

            async Task AddSequenceAsync(WttAlgoTargetProof proof, string purpose, CancellationToken token)
            {
                var emitter = new byte[34]; BinaryPrimitives.WriteUInt16BigEndian(emitter, proof.Proof.Vaa.EmitterChain);
                Convert.FromHexString(proof.Proof.Vaa.EmitterAddress).CopyTo(emitter, 2);
                await AddStorageAsync(purpose + "SequenceStorage", purpose, snapshot.Deployment.Token,
                    UInt64Text.Parse(proof.Proof.Vaa.Sequence) / (127 * 15 * 8), emitter, token, UInt64Text.Parse(proof.Proof.Vaa.Sequence));
            }
            async Task AddStorageAsync(string stage, string purpose, ulong application, ulong index, byte[] emitter, CancellationToken token, ulong? sequence = null)
            {
                var address = WttAlgorandStorage.Address(application, index, emitter);
                var firstReference = storageAddresses.Add(address);
                var account = await ReadWttAlgorandJsonAsync("/v2/accounts/" + address, token, allowMissing: true);
                if (account.ValueKind != JsonValueKind.Undefined)
                {
                    if (WttAlgorandStorage.Read(account, application, address, snapshot.Round) is { } blob)
                    {
                        if (stage == "WrappedStorage" && (blob.Metadata.Length != 0 || blob.Bytes.Any(value => value != 0)))
                            throw new InvalidDataException("New wrapped storage already has a foreign role or registration.");
                        if (sequence is { } expectedSequence && WttAlgorandReplayBitmap.IsConsumed(blob, expectedSequence))
                            throw new InvalidDataException("Target proof was consumed before its quotation snapshot completed.");
                        return;
                    }
                    if (!WttAlgorandStorage.IsUnallocated(account, address, snapshot.Round))
                        throw new InvalidDataException("Target prerequisite address has incompatible authority or local state.");
                }
                if (!firstReference) return;
                steps.Add(new(stage, purpose, application, index, Convert.ToHexStringLower(emitter),
                    new(stage, "0", "1002000", "0", intent.TargetAuthority.Address)));
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or ArgumentException or OverflowException)
        { return Error<WttStageQuote>(exception.Message); }
    }
}
