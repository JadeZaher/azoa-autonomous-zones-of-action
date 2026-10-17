using System.Text.Json;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Wormhole;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    /// <inheritdoc cref="IWttTargetModule.PrepareWttTargetAsync"/>
    public async Task<AZOAResult<WttStagePreparation>> PrepareWttTargetAsync(WttOperationIntent intent, WttTargetProofs proofs,
        WttStageQuote quote, string stage, int generation, CancellationToken ct = default)
    {
        try
        {
            var plan = ReadWttTargetPlan(intent, proofs, quote);
            var step = plan.Steps.SingleOrDefault(value => value.Stage == stage && value.Kind != "Cleanup")
                ?? throw new InvalidDataException("Target stage is absent from the admitted quote.");
            if (generation < 0) throw new InvalidDataException("Target generation cannot be negative.");
            var proof = plan.Proofs.Single(value => value.Label == step.ProofLabel);
            var binding = WttTargetBinding(intent, quote, stage, generation);
            var keys = WttTargetKeys(binding, intent.TargetAuthority, step, proof);
            var references = step.Kind == "Post" ? keys.Where(value => value.StageRole.EndsWith(":signature-set", StringComparison.Ordinal)).ToHashSet() : null;
            byte[] Compile(DurableSolanaPlan recovery, IReadOnlyDictionary<string, WttStageKeyBinding> roles)
                => CompileWttTarget(intent, plan, step, recovery, WttTargetRole(roles, ":payer"),
                    step.Kind is "Verify" or "Post" ? WttTargetRole(roles, ":signature-set") : null);
            async Task Check(CancellationToken token)
            {
                var deployment = ReadWttSolanaDeployment() ?? throw new InvalidDataException("Pinned target deployment is unavailable.");
                var snapshot = await ReadWttSolanaSnapshotAsync(deployment, [], token);
                if (WttDescriptor(snapshot) != intent.Target || snapshot.MessageFee != plan.ProtocolFee
                    || await ReadWttSolanaGuardAsync(token) != plan.Guard)
                    throw new InvalidDataException("Target protocol, guard, fee or Rent changed before signing.");
                var current = await ReadWttTargetProofAsync(intent, proof.Vaa, proof.Label, proof.VerificationGeneration, token);
                if (step.Kind is "Register" or "Redeem" && !current.AlreadyPosted)
                    throw new InvalidDataException("Exact verified posted body is required before target token effects.");
                if (step.Kind is "Verify" or "Post" && current.AlreadyPosted)
                    throw new InvalidDataException("Proof is already posted; reconcile the admitted stage before creating another wire.");
                if (!current.AlreadyPosted && !current.Guardians.SequenceEqual(proof.Guardians))
                    throw new InvalidDataException("Guardian membership changed from the exact verification plan.");
                if (step.Kind == "Register" && await ReadWttMetadataPolicyAsync(plan.Guard, token) != plan.MetadataPolicy)
                    throw new InvalidDataException("Reviewed metadata executable policy changed before registration.");
            }
            async Task Verify(byte[] message, CancellationToken token)
            {
                var tables = WttUsedTables(message, plan.LookupTables);
                await RequireCurrentLookupResolutionAsync(message, tables, token);
                if (await MeasureSolanaComputeAsync(message, tables.Count == 0 ? 0 : checked((long)tables.Max(value => value.Slot)), token) > step.ComputeUnits)
                    throw new InvalidDataException("Exact target stage exceeds its frozen compute ceiling.");
            }
            return await PrepareWttSolanaWireAsync(binding, quote, intent.TargetAuthority, keys, Compile, Check, ct, Verify, references);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException or FormatException or OverflowException)
        { return Error<WttStagePreparation>(exception.Message); }
    }

    /// <inheritdoc cref="IWttTargetModule.SubmitWttTargetAsync"/>
    public async Task SubmitWttTargetAsync(WttOperationIntent intent, WttTargetProofs proofs, WttStageQuote quote,
        WttStagePreparation prepared, CancellationToken ct = default)
    {
        var (_, plan, wire) = await ReadWttTargetPreparationAsync(intent, proofs, quote, prepared, ct);
        if (await VerifyGenesisAsync(ct) != wire.Recovery.Genesis) throw new InvalidDataException("Target wire belongs to another genesis.");
        var height = await DurableRpcAsync("getBlockHeight", [new { commitment = "finalized" }], ct);
        if (height.GetInt64() > wire.Recovery.LastValidBlockHeight) return;
        var message = Convert.FromBase64String(wire.MessageBase64);
        await RequireCurrentLookupResolutionAsync(message, WttUsedTables(message, plan.LookupTables), ct);
        var result = await DurableRpcAsync("sendTransaction", [prepared.SignedTransactionBase64,
            new { encoding = "base64", skipPreflight = false, preflightCommitment = "finalized", maxRetries = 0 }], ct);
        if (result.ValueKind != JsonValueKind.String || result.GetString() != prepared.TransactionId)
            throw new InvalidDataException("Target send response differs from the saved wire; exact observation is required.");
    }

    private async Task<(WttPreparation Saved, WttSolanaTargetPlan Plan, WttSolanaWirePlan Wire)> ReadWttTargetPreparationAsync(
        WttOperationIntent intent, WttTargetProofs proofs, WttStageQuote quote, WttStagePreparation prepared, CancellationToken ct)
    {
        var plan = ReadWttTargetPlan(intent, proofs, quote);
        var step = plan.Steps.SingleOrDefault(value => value.Stage == prepared.Stage && value.Kind != "Cleanup")
            ?? throw new InvalidDataException("Target stage differs from the saved plan.");
        var proof = plan.Proofs.Single(value => value.Label == step.ProofLabel);
        var binding = WttTargetBinding(intent, quote, prepared.Stage, prepared.Generation);
        var keys = WttTargetKeys(binding, intent.TargetAuthority, step, proof);
        using var scope = _custodyScopeFactory?.CreateScope();
        var store = _wttPreparationStore ?? scope?.ServiceProvider.GetRequiredService<IWttPreparationStore>()
            ?? throw new InvalidOperationException("Durable WTT preparation storage is required.");
        var saved = await store.GetAsync(WttPreparationIdentity.Id(binding), ct) ?? throw new InvalidDataException("Target preparation is absent.");
        byte[] Compile(DurableSolanaPlan recovery, IReadOnlyDictionary<string, WttStageKeyBinding> roles)
            => CompileWttTarget(intent, plan, step, recovery, WttTargetRole(roles, ":payer"),
                step.Kind is "Verify" or "Post" ? WttTargetRole(roles, ":signature-set") : null);
        var exact = await ReadWttSolanaWireAsync(saved, binding, step.Reservation, keys, Compile, ct);
        if (exact != prepared) throw new InvalidDataException("Target candidate differs from the winning durable wire.");
        return (saved, plan, ReadWttSolanaPlan(saved, binding, keys, Compile));
    }

    private WttSolanaTargetPlan ReadWttTargetPlan(WttOperationIntent intent, WttTargetProofs proofs, WttStageQuote quote)
    {
        if (!ValidWttTargetIntent(intent) || quote.Phase != "Target" || quote.ProtocolIdentity != intent.Target.ProtocolIdentity
            || quote.IntentHash != CanonicalJson.Hash(intent)) throw new InvalidDataException("Target quote differs from the frozen intent.");
        var plan = JsonSerializer.Deserialize<WttSolanaTargetPlan>(quote.PlanJson) ?? throw new InvalidDataException("Target plan is missing.");
        if (plan.Version != 1 || plan.IntentHash != quote.IntentHash || plan.Sender != intent.TargetAuthority.Address
            || plan.Core != intent.Target.CoreBridge || plan.TokenBridge != intent.Target.TokenBridge || plan.Guard is null
            || plan.Steps is null || plan.Proofs is null || plan.LookupTables is null
            || plan.Steps.Any(value => value is null || value.Reservation is null)
            || plan.Proofs.Any(value => value is null || value.Vaa is null || value.Guardians is null)
            || !plan.Steps.Select(value => value.Reservation).SequenceEqual(quote.Stages)
            || plan.Steps.Select(value => value.Stage).Distinct(StringComparer.Ordinal).Count() != plan.Steps.Count
            || CanonicalJson.Hash(new { CoreHash = plan.CoreHash, TokenHash = plan.TokenHash }) != intent.Target.CodeIdentity
            || CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(plan)) != quote.PlanJson
            || CanonicalJson.Hash(new { Version = 1, IntentHash = quote.IntentHash, Plan = quote.PlanJson, ExpiresAt = quote.ExpiresAt }) != quote.QuoteId)
            throw new InvalidDataException("Target executable, proof plan or quote identity differs from original consent.");
        var transfer = WttProofBinding.Transfer(intent, proofs.Transfer);
        if (plan.Proofs.Count is < 1 or > 2 || plan.Proofs.Select(value => value.Label).Distinct().Count() != plan.Proofs.Count
            || plan.Proofs.Last().Label != "Transfer" || CanonicalJson.Hash(plan.Proofs.Last().Vaa) != CanonicalJson.Hash(transfer))
            throw new InvalidDataException("Target plan changed the exact transfer envelope or proof sequence.");
        if (plan.Proofs.Count == 2 && (proofs.Attestation is null || plan.Proofs[0].Label != "Attestation"
            || CanonicalJson.Hash(plan.Proofs[0].Vaa) != CanonicalJson.Hash(WttProofBinding.Attestation(intent, proofs.Attestation))))
            throw new InvalidDataException("Registration requires the exact original-chain attestation.");
        RequireWttTargetReservations(intent, plan);
        return plan;
    }

    private static WttPreparationBinding WttTargetBinding(WttOperationIntent intent, WttStageQuote quote, string stage, int generation)
        => new(quote.ProtocolIdentity, intent.ActorUserId, intent.TenantId, intent.OperationId, "Target", stage, generation, quote.IntentHash, quote.QuoteId);

    private static IReadOnlyList<WttStageKeyBinding> WttTargetKeys(WttPreparationBinding binding, WttSigningAuthority authority,
        WttSolanaTargetStep step, WttSolanaTargetProof proof)
        => step.Kind is "Verify" or "Post" ? WttSolanaKeyRoles.Verification(binding, authority, proof.Vaa, proof.VerificationGeneration)
            : WttSolanaKeyRoles.TargetPayer(binding, authority);

    private static PublicKey WttTargetRole(IReadOnlyDictionary<string, WttStageKeyBinding> keys, string suffix)
    {
        var matches = keys.Where(value => value.Value.StageRole.EndsWith(suffix, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("Target protected role is missing or duplicated.");
        return new(matches[0].Key);
    }
}
