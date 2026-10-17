using System.Buffers.Binary;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Closed ComputeBudget prelude encoding from the pinned Solana SDK.</summary>
internal static class SolanaComputeBudget
{
    internal static readonly PublicKey Program = new("ComputeBudget111111111111111111111111111111");

    public static TransactionInstruction Limit(uint units)
    {
        if (units == 0) throw new ArgumentException("A nonzero bounded compute limit is required.", nameof(units));
        var data = new byte[5]; data[0] = 2; BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1), units);
        return new() { ProgramId = Program.KeyBytes, Keys = [], Data = data };
    }

    public static TransactionInstruction Price(ulong microLamports)
    {
        var data = new byte[9]; data[0] = 3; BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(1), microLamports);
        return new() { ProgramId = Program.KeyBytes, Keys = [], Data = data };
    }

    public static byte[] SimulationWire(byte[] message)
    {
        var parsed = SolanaMessageEnvelope.Read(message);
        var count = parsed.RequiredSignatures;
        if (count is 0 or > 127) throw new ArgumentException("Simulation requires a bounded explicit signer list.");
        var wire = new byte[1 + count * 64 + message.Length];
        if (wire.Length > 1232) throw new ArgumentException("Simulation message exceeds the Solana transaction packet ceiling.");
        wire[0] = (byte)count; message.CopyTo(wire, 1 + count * 64);
        return wire;
    }
}
