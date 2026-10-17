using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;
using Solnet.Programs;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private sealed record WttSolanaSourceStep(string Stage, ulong Funding, bool CreateSourceAta, uint ComputeUnits,
        ulong ComputePrice, WttStageReservation Reservation);
    private sealed record WttSolanaSourcePlan(int Version, string IntentHash, string Sender, string Core, string TokenBridge,
        string CoreHash, string TokenHash, WttSolanaGuard Guard, ulong ProtocolFee,
        IReadOnlyList<SolanaLookupTableSnapshot> LookupTables, IReadOnlyList<WttSolanaSourceStep> Steps);

    private static byte[] CompileWttSource(WttOperationIntent intent, WttSolanaSourcePlan plan, WttSolanaSourceStep step,
        DurableSolanaPlan recovery, PublicKey bridgePayer, PublicKey message)
    {
        if (step.Stage is not ("Attest" or "Transfer") || plan.Sender != intent.SourceAuthority.Address
            || plan.IntentHash != CanonicalJson.Hash(intent) || bridgePayer.Key == message.Key
            || bridgePayer.Key == plan.Sender || message.Key == plan.Sender)
            throw new InvalidDataException("WTT source stage must retain its exact intent and distinct participant roles.");
        var user = new PublicKey(plan.Sender); var core = new PublicKey(plan.Core); var token = new PublicKey(plan.TokenBridge);
        var mint = new PublicKey(intent.Asset.TokenAddress);
        var source = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(user, mint);
        var instructions = new List<TransactionInstruction>
        {
            SolanaComputeBudget.Limit(step.ComputeUnits), SolanaComputeBudget.Price(step.ComputePrice),
            WttSolanaCostGuard.Build(new PublicKey(plan.Guard.Program), core, token, plan.ProtocolFee,
                Convert.FromBase64String(plan.Guard.RentBase64), Convert.FromHexString(plan.CoreHash), Convert.FromHexString(plan.TokenHash)),
            SystemProgram.Transfer(user, bridgePayer, step.Funding),
        };
        if (step.Stage == "Attest") instructions.Add(WttSolanaAccounts.Attest(token, core, bridgePayer, message, mint, intent.Nonce));
        else
        {
            if (step.CreateSourceAta)
            {
                instructions.Add(SolanaAssociatedTokenAccount.CreateIdempotent(user, user, mint));
            }
            var amount = UInt64Text.Parse(intent.Amount.Debited);
            if (intent.Asset.NativeAsset)
            {
                instructions.Add(SystemProgram.Transfer(user, source, amount));
                instructions.Add(TokenProgram.SyncNative(source));
            }
            instructions.Add(TokenProgram.Approve(source, WttSolanaAccounts.Derive(token, "authority_signer"u8.ToArray()), user, amount));
            var fee = WttPayloadCodec.ParseAmount(intent.Amount.Fee) * System.Numerics.BigInteger.Pow(10, Math.Max(0, intent.Asset.Decimals - 8));
            instructions.Add(WttSolanaAccounts.Transfer(token, core, bridgePayer, message, source, mint,
                intent.Asset.OriginChain, WttPayloadCodec.FixedHex(intent.Asset.OriginAddress, 32), intent.Nonce, amount,
                checked((ulong)fee), WttPayloadCodec.FixedHex(intent.RecipientProtocolAddress, 32), intent.Target.ProtocolChain, user));
            instructions.Add(TokenProgram.Revoke(source, user));
        }
        var legacy = CompileDurable(recovery, user, instructions);
        var compiled = SolanaMessageEnvelope.CompileVersionZero(legacy, plan.LookupTables, out _);
        _ = SolanaComputeBudget.SimulationWire(compiled);
        return compiled;
    }
}
