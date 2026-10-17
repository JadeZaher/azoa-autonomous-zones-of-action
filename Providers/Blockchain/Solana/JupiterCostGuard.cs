using Solnet.Programs;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Builds the closed execution-time Rent and Jupiter/Whirlpool executable assertion.</summary>
internal static class JupiterCostGuard
{
    internal static TransactionInstruction Build(PublicKey guard, byte[] rent, byte[] jupiterHash, byte[] whirlpoolHash)
    {
        var jupiter = JupiterRouteAccounts.Program;
        var whirlpool = WhirlpoolAccountState.Program;
        PublicKey[] keys = [SysVars.RentKey, SysVars.ClockKey, jupiter, SolanaNativeLoader.ProgramData(jupiter),
            whirlpool, SolanaNativeLoader.ProgramData(whirlpool)];
        if (rent.Length != 17 || jupiterHash.Length != 32 || whirlpoolHash.Length != 32 ||
            keys.Select(key => key.Key).Distinct(StringComparer.Ordinal).Count() != keys.Length || keys.Any(key => key.Key == guard.Key))
            throw new InvalidDataException("Jupiter guard requires exact distinct account roles, Rent and executable hashes.");
        return new()
        {
            ProgramId = guard.KeyBytes, Data = [4, ..rent, ..jupiterHash, ..whirlpoolHash],
            Keys = keys.Select(key => AccountMeta.ReadOnly(key, false)).ToList()
        };
    }
}
