using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;
using Solnet.Programs;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private sealed record WttSolanaTargetProof(string Label, WttVaa Vaa, IReadOnlyList<string> Guardians, int VerificationGeneration,
        bool AlreadyPosted = false, string? PostedSignatureSet = null);
    private sealed record WttSolanaTargetStep(string Stage, string Kind, string ProofLabel, int SignatureStart, int SignatureCount,
        ulong Funding, bool CreateRecipientAta, bool CreateFeeAta, uint ComputeUnits, ulong ComputePrice, WttStageReservation Reservation);
    private sealed record WttSolanaTargetPlan(int Version, string IntentHash, string Sender, string Core, string TokenBridge,
        string CoreHash, string TokenHash, WttSolanaGuard Guard, ulong ProtocolFee, string Mint,
        IReadOnlyList<WttSolanaTargetProof> Proofs, IReadOnlyList<SolanaLookupTableSnapshot> LookupTables,
        IReadOnlyList<WttSolanaTargetStep> Steps, SolanaMetadataCreationPolicy? MetadataPolicy = null);

    private static byte[] CompileWttTarget(WttOperationIntent intent, WttSolanaTargetPlan plan, WttSolanaTargetStep step,
        DurableSolanaPlan recovery, PublicKey bridgePayer, PublicKey? signatureSet)
    {
        if (plan.IntentHash != CanonicalJson.Hash(intent) || plan.Sender != intent.TargetAuthority.Address
            || bridgePayer.Key == plan.Sender || signatureSet?.Key == plan.Sender || signatureSet?.Key == bridgePayer.Key
            || step.Kind is not ("Verify" or "Post" or "Register" or "Redeem"))
            throw new InvalidDataException("Target instruction requires exact intent and distinct authorized account roles.");
        var proof = plan.Proofs.Single(value => value.Label == step.ProofLabel);
        WttSolanaCoreInstructions.RequireCanonicalVaa(proof.Vaa);
        var vaa = proof.Vaa; var user = new PublicKey(plan.Sender); var core = new PublicKey(plan.Core);
        var token = new PublicKey(plan.TokenBridge); var mint = new PublicKey(plan.Mint);
        var guard = step.Kind == "Register"
            ? WttRegistrationGuard(plan, core, token)
            : WttSolanaCostGuard.Build(new PublicKey(plan.Guard.Program), core, token, plan.ProtocolFee,
                Convert.FromBase64String(plan.Guard.RentBase64), Convert.FromHexString(plan.CoreHash), Convert.FromHexString(plan.TokenHash));
        var instructions = new List<TransactionInstruction>
        {
            SolanaComputeBudget.Limit(step.ComputeUnits), SolanaComputeBudget.Price(step.ComputePrice),
            guard,
            SystemProgram.Transfer(user, bridgePayer, step.Funding),
        };
        if (step.Kind == "Verify")
        {
            if (signatureSet is null || step.CreateRecipientAta || step.CreateFeeAta)
                throw new InvalidDataException("Verification requires its exact signature-set account without token-account effects.");
            var pair = WttSolanaCoreInstructions.Verify(core, bridgePayer, signatureSet, vaa, proof.Guardians,
                step.SignatureStart, step.SignatureCount, checked((byte)instructions.Count));
            instructions.Add(pair.Secp); instructions.Add(pair.Verify);
        }
        else if (step.Kind == "Post")
        {
            if (signatureSet is null || step.SignatureCount != 0 || step.SignatureStart != 0 || step.CreateRecipientAta || step.CreateFeeAta)
                throw new InvalidDataException("Posting requires its exact verified signature-set account and no unrelated effects.");
            instructions.Add(WttSolanaCoreInstructions.Post(core, bridgePayer, signatureSet, vaa));
        }
        else
        {
            if (signatureSet is not null || step.SignatureCount != 0 || step.SignatureStart != 0)
                throw new InvalidDataException("Token bridge stages do not request signature-set authority.");
            var posted = WttSolanaCoreInstructions.PostedVaa(core, vaa);
            if (step.Kind == "Register")
            {
                if (step.CreateRecipientAta || step.CreateFeeAta || proof.Label != "Attestation")
                    throw new InvalidDataException("Wrapped registration requires only the exact original-chain metadata proof.");
                var payload = WttPayloadCodec.ParseAttestation(Convert.FromHexString(vaa.PayloadHex));
                if (payload.TokenChain != intent.Asset.OriginChain || payload.TokenAddress != intent.Asset.OriginAddress
                    || WttSolanaAccounts.WrappedMint(token, payload.TokenChain, Convert.FromHexString(payload.TokenAddress)).Key != mint.Key)
                    throw new InvalidDataException("Registration proof belongs to another original asset.");
                instructions.Add(WttSolanaAccounts.Register(token, core, bridgePayer, posted, vaa.EmitterChain,
                    Convert.FromHexString(vaa.EmitterAddress), UInt64Text.Parse(vaa.Sequence), payload.TokenChain, Convert.FromHexString(payload.TokenAddress)));
            }
            else
            {
                if (proof.Label != "Transfer") throw new InvalidDataException("Redemption requires the exact transfer proof.");
                var payload = WttPayloadCodec.ParseTransfer(Convert.FromHexString(vaa.PayloadHex));
                var recipient = new PublicKey(intent.RecipientAddress);
                var destination = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(recipient, mint);
                var feeDestination = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(user, mint);
                if (payload != new WttTransferPayload(intent.Amount.Amount, intent.Asset.OriginAddress, intent.Asset.OriginChain,
                        intent.RecipientProtocolAddress, 1, intent.Amount.Fee)
                    || Convert.ToHexStringLower(destination.KeyBytes) != intent.RecipientProtocolAddress
                    || (intent.Asset.OriginChain == 1 ? new PublicKey(Convert.FromHexString(intent.Asset.OriginAddress))
                        : WttSolanaAccounts.WrappedMint(token, intent.Asset.OriginChain, Convert.FromHexString(intent.Asset.OriginAddress))).Key != mint.Key)
                    throw new InvalidDataException("Redemption mint, token-account recipient or amount differs from frozen consent.");
                void CreateAta(PublicKey owner) => instructions.Add(SolanaAssociatedTokenAccount.CreateIdempotent(user, owner, mint));
                if (step.CreateRecipientAta) CreateAta(recipient);
                if (step.CreateFeeAta && feeDestination.Key != destination.Key) CreateAta(user);
                instructions.Add(WttSolanaAccounts.Complete(token, core, bridgePayer, posted, vaa.EmitterChain,
                    Convert.FromHexString(vaa.EmitterAddress), UInt64Text.Parse(vaa.Sequence), intent.Asset.OriginChain,
                    Convert.FromHexString(intent.Asset.OriginAddress), destination, feeDestination));
            }
        }
        var legacy = CompileDurable(recovery, user, instructions);
        var compiled = SolanaMessageEnvelope.CompileVersionZero(legacy, plan.LookupTables, out _);
        _ = SolanaComputeBudget.SimulationWire(compiled);
        return compiled;
    }

    private static TransactionInstruction WttRegistrationGuard(WttSolanaTargetPlan plan, PublicKey core, PublicKey token)
    {
        var policy = plan.MetadataPolicy ?? throw new InvalidDataException("Wrapped registration requires its reviewed metadata executable policy.");
        policy.RequireSupported();
        return WttSolanaCostGuard.BuildRegistration(new PublicKey(plan.Guard.Program), core, token, plan.ProtocolFee,
            Convert.FromBase64String(plan.Guard.RentBase64), Convert.FromHexString(plan.CoreHash),
            Convert.FromHexString(plan.TokenHash), Convert.FromHexString(policy.CodeHash));
    }
}
