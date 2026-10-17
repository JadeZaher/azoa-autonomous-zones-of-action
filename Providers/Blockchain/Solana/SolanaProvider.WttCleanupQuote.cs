using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;
using Solnet.Programs;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private sealed record WttSolanaCleanupPlan(int Version, string OriginalTransactionId, string OriginalBlockIdentity,
        ulong OriginalSlot, ulong OriginalBefore, ulong OriginalAfter, ulong OriginalFunding, ulong OriginalProtocolCost,
        ulong SnapshotSlot, ulong SnapshotBalance, WttStageKeyBinding PayerBinding, string OriginalObservationHash);

    private sealed record WttSolanaCleanupOrigin(WttSolanaWirePlan Wire, IReadOnlyList<SolanaLookupTableSnapshot> Tables,
        WttSigningAuthority Owner, string Payer, ulong Funding, ulong ProtocolCost, WttStageObservation Observation);

    /// <inheritdoc cref="IWttCleanupModule.QuoteWttCleanupAsync"/>
    public async Task<AZOAResult<WttCleanupQuote>> QuoteWttCleanupAsync(WttOperationIntent intent, WttStageQuote originalQuote,
        WttTargetProofs? proofs, WttStagePreparation original, CancellationToken ct = default)
    {
        try
        {
            var origin = await ReadWttCleanupOriginAsync(intent, originalQuote, proofs, original, ct);
            var transaction = await ReadWttFinalizedTransactionAsync(original.TransactionId, ct);
            if (transaction.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Original finalized cleanup evidence is unavailable.");
            var finalized = await RequireWttFinalityAsync(transaction, original, origin.Wire, origin.Tables, ct);
            if (finalized.Failed != (origin.Observation.Verdict == "FailedOnChain"))
                throw new InvalidDataException("Original stage status changed while constructing the return offer.");
            var index = finalized.Accounts.Keys.Select((key, position) => (key, position)).Single(value => value.key.Key == origin.Payer).position;
            var before = finalized.Meta.GetProperty("preBalances")[index].GetUInt64();
            var after = finalized.Meta.GetProperty("postBalances")[index].GetUInt64();
            var attributed = WttSolanaCleanupAccounting.OriginalDeposit(before, after, origin.Funding, origin.ProtocolCost,
                UInt64Text.Parse(original.Reservation.LockedDeposit), finalized.Failed);
            var response = await DurableRpcAsync("getMultipleAccounts", [new[] { origin.Payer, origin.Owner.Address },
                new { encoding = "base64", commitment = "finalized", minContextSlot = finalized.Slot }], ct);
            var slot = response.GetProperty("context").GetProperty("slot").GetUInt64();
            var accounts = response.GetProperty("value");
            if (slot < finalized.Slot || accounts.ValueKind != JsonValueKind.Array || accounts.GetArrayLength() != 2)
                throw new InvalidDataException("Cleanup account snapshot predates original finalization.");
            ulong Balance(JsonElement account)
            {
                if (account.ValueKind == JsonValueKind.Null) return 0;
                if (SolanaAccountData.ReadOwned(account, SystemProgram.ProgramIdKey.Key, false).Length != 0)
                    throw new InvalidDataException("Cleanup requires the original empty system payer and owner accounts.");
                return account.GetProperty("lamports").GetUInt64();
            }
            var current = WttSolanaCleanupAccounting.CurrentReturn(after, Balance(accounts[0]));
            var ownerBalance = Balance(accounts[1]);
            var evidenceHash = CanonicalJson.Hash(new { origin.Observation, finalized.BlockIdentity, finalized.Slot,
                Before = before, After = after, origin.Funding, origin.ProtocolCost, Original = CanonicalJson.Hash(original) });
            var plan = new WttSolanaCleanupPlan(1, original.TransactionId, finalized.BlockIdentity, finalized.Slot,
                before, after, origin.Funding, origin.ProtocolCost, slot, current, origin.Wire.StageKeys[origin.Payer], evidenceHash);
            var recovery = await NewPlanAsync("", 0, 0, false, originalQuote.IntentHash, ct);
            if ("solana:" + recovery.Genesis != original.ProtocolIdentity)
                throw new InvalidDataException("Cleanup offer belongs to another physical Solana network.");
            var message = CompileWttCleanup(recovery, origin.Owner.Address, origin.Payer, current, new string('0', 64));
            var fee = current == 0 ? 0 : await NativeMessageFeeAsync(message, ct);
            if (ownerBalance < fee) throw new InvalidDataException("Original owner cannot fund the separately disclosed cleanup network fee.");
            var offer = new WttCleanupQuote(1, "", originalQuote.IntentHash, originalQuote.QuoteId, originalQuote.Phase,
                original.Stage, original.Generation, CanonicalJson.Hash(original), original.ProtocolIdentity,
                origin.Payer, origin.Owner.Address, current.ToString(CultureInfo.InvariantCulture),
                attributed.ToString(CultureInfo.InvariantCulture), fee.ToString(CultureInfo.InvariantCulture), evidenceHash,
                CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(plan)), DateTimeOffset.UtcNow.AddSeconds(30));
            return Ok(offer with { QuoteId = CanonicalJson.Hash(offer) });
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException
            or FormatException or KeyNotFoundException or InvalidOperationException or OverflowException)
        { return Error<WttCleanupQuote>(exception.Message); }
    }

    private async Task<WttSolanaCleanupOrigin> ReadWttCleanupOriginAsync(WttOperationIntent intent, WttStageQuote quote,
        WttTargetProofs? proofs, WttStagePreparation original, CancellationToken ct)
    {
        WttSolanaCleanupOrigin result;
        if (quote.Phase == "Source")
        {
            var (_, plan, wire) = await ReadWttSourcePreparationAsync(intent, quote, original, ct);
            var observed = await ObserveWttSourceAsync(intent, quote, original, ct);
            if (observed.IsError || observed.Result is null) throw new InvalidDataException("Original source evidence is unavailable.");
            result = new(wire, plan.LookupTables, intent.SourceAuthority, WttSourceRole(wire.StageKeys, original.Stage, "payer").Key,
                plan.Steps.Single(value => value.Stage == original.Stage).Funding, plan.ProtocolFee, observed.Result);
        }
        else if (quote.Phase == "Target" && proofs is not null)
        {
            var (_, plan, wire) = await ReadWttTargetPreparationAsync(intent, proofs, quote, original, ct);
            var observed = await ObserveWttTargetAsync(intent, proofs, quote, original, ct);
            if (observed.IsError || observed.Result is null) throw new InvalidDataException("Original target evidence is unavailable.");
            var step = plan.Steps.Single(value => value.Stage == original.Stage);
            result = new(wire, plan.LookupTables, intent.TargetAuthority, WttTargetRole(wire.StageKeys, ":payer").Key,
                step.Funding, UInt64Text.Parse(step.Reservation.Value), observed.Result);
        }
        else throw new InvalidDataException("Cleanup requires the exact original source or target phase and proof.");
        if (result.Observation.Verdict is not ("Confirmed" or "FailedOnChain")
            || result.Observation.TransactionId != original.TransactionId || result.Observation.ProtocolIdentity != original.ProtocolIdentity
            || result.Owner.Context.UserId != intent.ActorUserId || result.Payer == result.Owner.Address)
            throw new InvalidDataException("Unresolved original stages cannot authorize a residual sweep.");
        return result;
    }

    private static byte[] CompileWttCleanup(DurableSolanaPlan recovery, string owner, string payer, ulong amount, string commitment)
    {
        if (owner == payer || commitment.Length != 64 || commitment.Any(value => !char.IsAsciiHexDigit(value)))
            throw new InvalidDataException("Cleanup requires distinct original roles and an exact generation commitment.");
        var memo = new TransactionInstruction
        {
            ProgramId = new PublicKey("MemoSq4gqABAXKb96qnH8TysNcWxMyWCqXgDLGmfcHr").KeyBytes,
            Keys = [], Data = System.Text.Encoding.UTF8.GetBytes(commitment),
        };
        return CompileDurable(recovery, new PublicKey(owner), [SystemProgram.Transfer(new PublicKey(payer), new PublicKey(owner), amount), memo]);
    }
}
