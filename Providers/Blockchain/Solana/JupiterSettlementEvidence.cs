using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Dex;
using Solnet.Programs;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

internal sealed record JupiterSettlement(ulong Input, ulong Output, ulong PlatformFee, ulong RetainedRent,
    IReadOnlyList<DexEffectDisclosure> RetainedEffects);

/// <summary>Accounts for exact owner, treasury and retained-account effects of a finalized reviewed message.</summary>
internal static class JupiterSettlementEvidence
{
    internal static JupiterSettlement Read(DexQuoteTerms terms, JupiterBuildPlan build,
        IReadOnlyList<JupiterInstruction> instructions, JupiterNativeAccount? native, ulong tokenRent,
        SolanaObservedTransaction.Accounts accounts, JsonElement meta, ulong networkFee)
    {
        var keys = accounts.Keys.Select(key => key.Key).ToArray();
        var pre = Lamports(meta, "preBalances", keys.Length);
        var post = Lamports(meta, "postBalances", keys.Length);
        var beforeTokens = meta.GetProperty("preTokenBalances");
        var afterTokens = meta.GetProperty("postTokenBalances");
        var owner = terms.Owner.Address;
        var ownerIndex = Index(keys, owner);
        var source = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(new PublicKey(owner), new PublicKey(build.InputMint)).Key;
        var destination = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(new PublicKey(owner), new PublicKey(build.OutputMint)).Key;
        ulong retainedRent = 0;
        var retainedEffects = new List<DexEffectDisclosure>();
        var createdAccounts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var setup in instructions.Where(instruction => instruction.Program == AssociatedTokenAccountProgram.ProgramIdKey.Key))
        {
            _ = JupiterSetupInstructions.ValidateSetup([setup], owner, build.InputMint, build.OutputMint);
            var address = setup.Accounts[1].Address;
            if (!createdAccounts.Add(address)) throw new InvalidDataException("Finalized preparation repeats an ATA creation.");
            var index = Index(keys, address);
            var mint = address == source ? build.InputMint : build.OutputMint;
            var decimals = checked((byte)(address == source ? terms.InputAsset.Decimals : terms.OutputAsset.Decimals));
            _ = SolanaTokenBalanceEvidence.Read(beforeTokens, index, mint, owner, decimals, true);
            _ = SolanaTokenBalanceEvidence.Read(afterTokens, index, mint, owner, decimals, false);
            var existed = beforeTokens.EnumerateArray().Any(item => item.GetProperty("accountIndex").GetInt32() == index);
            var expected = existed ? pre[index] : Math.Max(pre[index], tokenRent);
            if (post[index] != expected) throw new InvalidDataException("Finalized ATA rent differs from the exact idempotent creation effect.");
            retainedRent = checked(retainedRent + post[index] - pre[index]);
            if (!existed) retainedEffects.Add(new("CreateAta", mint,
                (post[index] - pre[index]).ToString(System.Globalization.CultureInfo.InvariantCulture), owner, address, true));
        }
        var gross = UInt64Text.Parse(terms.GrossInput);
        var fee = UInt64Text.Parse(terms.PlatformFee.Amount);
        if (!terms.InputAsset.Native)
        {
            var index = Index(keys, source);
            var old = SolanaTokenBalanceEvidence.Read(beforeTokens, index, build.InputMint, owner, checked((byte)terms.InputAsset.Decimals), false);
            var current = SolanaTokenBalanceEvidence.Read(afterTokens, index, build.InputMint, owner, checked((byte)terms.InputAsset.Decimals), false);
            if (new BigInteger(old) - current != gross) throw new InvalidDataException("Finalized owner token debit differs from the exact gross input.");
        }
        if (fee > 0)
        {
            var treasury = terms.PlatformFee.TreasuryAddress;
            if (terms.InputAsset.Native)
            {
                var index = Index(keys, treasury);
                if (new BigInteger(post[index]) - pre[index] != fee)
                    throw new InvalidDataException("Finalized treasury SOL credit differs from the platform fee.");
            }
            else
            {
                var address = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(new PublicKey(treasury), new PublicKey(build.InputMint)).Key;
                var index = Index(keys, address);
                var old = SolanaTokenBalanceEvidence.Read(beforeTokens, index, build.InputMint, treasury, checked((byte)terms.InputAsset.Decimals), false);
                var current = SolanaTokenBalanceEvidence.Read(afterTokens, index, build.InputMint, treasury, checked((byte)terms.InputAsset.Decimals), false);
                if (new BigInteger(current) - old != fee) throw new InvalidDataException("Finalized treasury token credit differs from the platform fee.");
            }
        }
        var ownerNativeDelta = new BigInteger(post[ownerIndex]) - pre[ownerIndex];
        BigInteger output;
        if (terms.OutputAsset.Native)
            output = ownerNativeDelta + networkFee + retainedRent;
        else
        {
            var index = Index(keys, destination);
            var old = SolanaTokenBalanceEvidence.Read(beforeTokens, index, build.OutputMint, owner, checked((byte)terms.OutputAsset.Decimals), createdAccounts.Contains(destination));
            var current = SolanaTokenBalanceEvidence.Read(afterTokens, index, build.OutputMint, owner, checked((byte)terms.OutputAsset.Decimals), false);
            output = new BigInteger(current) - old;
            if (ownerNativeDelta != -(new BigInteger(networkFee) + retainedRent + (terms.InputAsset.Native ? gross : 0)))
                throw new InvalidDataException("Finalized owner SOL changes contain an unexplained fee, rent or transfer.");
        }
        if (output < UInt64Text.Parse(terms.MinimumOutput) || output > ulong.MaxValue)
            throw new InvalidDataException("Finalized owner output does not satisfy the accepted minimum.");
        if (native is not null)
        {
            var index = Index(keys, native.Address);
            if (pre[index] != 0 || post[index] != 0 || beforeTokens.EnumerateArray().Concat(afterTokens.EnumerateArray())
                .Any(item => item.GetProperty("accountIndex").GetInt32() == index))
                throw new InvalidDataException("The dedicated native account was not created and closed atomically.");
        }
        return new(gross, (ulong)output, fee, retainedRent, retainedEffects);
    }

    private static ulong[] Lamports(JsonElement meta, string name, int count)
    {
        var values = meta.GetProperty(name);
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != count)
            throw new InvalidDataException("Complete finalized lamport balances are required.");
        return values.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var amount)
            ? amount : throw new InvalidDataException("Finalized lamport balance is not an unsigned integer.")).ToArray();
    }

    private static int Index(IReadOnlyList<string> keys, string address)
    {
        for (var index = 0; index < keys.Count; index++) if (keys[index] == address) return index;
        throw new InvalidDataException("Finalized message omits a required economic account.");
    }
}
