using System.Numerics;
using System.Text.Json;
using Solnet.Programs;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Proves exact account removal and owner-only lamport return without crediting historical rent.</summary>
internal static class JupiterCleanupEvidence
{
    internal static ulong Read(string owner, string mint, byte decimals, SolanaObservedTransaction.Accounts accounts,
        JsonElement meta, ulong fee)
    {
        var account = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(new PublicKey(owner), new PublicKey(mint)).Key;
        var keys = accounts.Keys.Select(key => key.Key).ToArray();
        var ownerIndex = Array.IndexOf(keys, owner); var accountIndex = Array.IndexOf(keys, account);
        if (ownerIndex < 0 || accountIndex < 0 || ownerIndex == accountIndex)
            throw new InvalidDataException("Cleanup omitted its exact owner or holding.");
        var pre = meta.GetProperty("preBalances"); var post = meta.GetProperty("postBalances");
        if (pre.ValueKind != JsonValueKind.Array || post.ValueKind != JsonValueKind.Array ||
            pre.GetArrayLength() != keys.Length || post.GetArrayLength() != keys.Length)
            throw new InvalidDataException("Cleanup balance evidence is incomplete.");
        var returned = pre[accountIndex].GetUInt64();
        if (returned == 0 || post[accountIndex].GetUInt64() != 0 ||
            (BigInteger)post[ownerIndex].GetUInt64() - pre[ownerIndex].GetUInt64() != (BigInteger)returned - fee ||
            SolanaTokenBalanceEvidence.Read(meta.GetProperty("preTokenBalances"), accountIndex, mint, owner, decimals, false) != 0)
            throw new InvalidDataException("Cleanup did not close the exact empty holding to its original owner.");
        var after = meta.GetProperty("postTokenBalances");
        if (after.ValueKind != JsonValueKind.Array || after.EnumerateArray().Any(item => item.GetProperty("accountIndex").GetInt32() == accountIndex))
            throw new InvalidDataException("Cleanup left a token holding after closure.");
        for (var index = 0; index < keys.Length; index++)
            if (index != ownerIndex && index != accountIndex && pre[index].GetUInt64() != post[index].GetUInt64())
                throw new InvalidDataException("Cleanup changed an unrelated account balance.");
        return returned;
    }
}
