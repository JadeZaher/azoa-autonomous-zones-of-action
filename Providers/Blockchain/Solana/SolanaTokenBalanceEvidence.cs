using System.Text.Json;
using AZOA.WebAPI.Helpers;
using Solnet.Programs;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Reads one exact finalized legacy SPL balance with its mint, owner and precision.</summary>
internal static class SolanaTokenBalanceEvidence
{
    internal static ulong Read(JsonElement balances, int accountIndex, string mint, string owner, byte decimals, bool allowMissing)
    {
        if (balances.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Exact token balance metadata is missing.");
        var matches = balances.EnumerateArray().Where(value => value.GetProperty("accountIndex").GetInt32() == accountIndex).ToArray();
        if (matches.Length == 0 && allowMissing) return 0;
        if (matches.Length != 1 || matches[0].GetProperty("mint").GetString() != mint || matches[0].GetProperty("owner").GetString() != owner
            || matches[0].GetProperty("programId").GetString() != TokenProgram.ProgramIdKey.Key
            || matches[0].GetProperty("uiTokenAmount").GetProperty("decimals").GetByte() != decimals)
            throw new InvalidDataException("Finalized token balance identity or precision is inconsistent.");
        return UInt64Text.Parse(matches[0].GetProperty("uiTokenAmount").GetProperty("amount").GetString()!);
    }
}
