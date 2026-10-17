using Solnet.Programs;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Builds the closed Rent-only assertion for the independently pinned immutable guard.</summary>
internal static class SolanaRentGuard
{
    internal static TransactionInstruction Build(PublicKey guard, byte[] rent)
    {
        if (rent.Length != 17 || guard.Key == SysVars.RentKey.Key)
            throw new InvalidDataException("Rent guard requires exactly the canonical Rent bytes and a distinct executable.");
        return new() { ProgramId = guard.KeyBytes, Data = [6, .. rent],
            Keys = [AccountMeta.ReadOnly(SysVars.RentKey, false)] };
    }
}
