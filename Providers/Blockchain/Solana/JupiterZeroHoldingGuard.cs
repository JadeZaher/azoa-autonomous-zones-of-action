using System.Buffers.Binary;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Asserts the original owner's exact empty SPL holding immediately before atomic closure.</summary>
internal static class JupiterZeroHoldingGuard
{
    internal static void RequireEmpty(ReadOnlySpan<byte> data, ulong lamports, string mint, string owner)
    {
        var holding = SolanaSplAssetCodec.ReadHolding(data);
        var wrapped = mint == "So11111111111111111111111111111111111111112";
        if (holding.Mint != mint || holding.Owner != owner || holding.Amount != 0 ||
            holding.CloseAuthority is { } close && close != owner || holding.Native != wrapped ||
            holding.Native && lamports != BinaryPrimitives.ReadUInt64LittleEndian(data[113..]))
            throw new InvalidDataException("Cleanup requires the exact owner's empty holding without unsynchronized native value.");
    }

    internal static TransactionInstruction Build(PublicKey guard, PublicKey account, PublicKey mint, PublicKey owner)
    {
        if (account.Key == owner.Key || guard.Key == account.Key || guard.Key == owner.Key)
            throw new InvalidDataException("The cleanup guard, holding and owner must be distinct.");
        var data = new byte[97]; data[0] = 5;
        account.KeyBytes.CopyTo(data, 1); mint.KeyBytes.CopyTo(data, 33); owner.KeyBytes.CopyTo(data, 65);
        return new TransactionInstruction
        {
            ProgramId = guard.KeyBytes, Data = data,
            Keys = [AccountMeta.Writable(account, false), AccountMeta.Writable(owner, true)]
        };
    }
}
