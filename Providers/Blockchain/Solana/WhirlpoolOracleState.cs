using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

internal sealed record WhirlpoolOracleState(ulong TradeEnableTimestamp, ushort FilterPeriod, ushort DecayPeriod,
    ushort ReductionFactor, uint ControlFactor, uint MaximumVolatility, ushort TickGroupSize,
    ushort MajorSwapThreshold, ulong ReferenceTimestamp, ulong MajorSwapTimestamp,
    uint VolatilityReference, int TickGroupReference, uint VolatilityAccumulator)
{
    internal static WhirlpoolOracleState Read(string address, ReadOnlySpan<byte> bytes, WhirlpoolPoolState pool,
        ulong chainTimestamp)
    {
        if (address != WhirlpoolAccountState.OracleAddress(pool.Address) || bytes.Length != 254 ||
            !bytes[..8].SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes("account:Oracle"))[..8]) ||
            !bytes.Slice(8, 32).SequenceEqual(new PublicKey(pool.Address).KeyBytes))
            throw new InvalidDataException("Whirlpool Oracle PDA, layout or pool binding differs.");
        var state = new WhirlpoolOracleState(BinaryPrimitives.ReadUInt64LittleEndian(bytes[40..]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[48..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[50..]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[52..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[54..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[58..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[62..]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[64..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[82..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[90..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[98..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[102..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[106..]));
        if (state.TradeEnableTimestamp > chainTimestamp || state.ReferenceTimestamp > chainTimestamp ||
            state.MajorSwapTimestamp > chainTimestamp || state.FilterPeriod == 0 || state.DecayPeriod <= state.FilterPeriod ||
            state.ReductionFactor >= 10000 || state.ControlFactor >= 100000 || state.TickGroupSize == 0 ||
            state.TickGroupSize > pool.TickSpacing || pool.TickSpacing % state.TickGroupSize != 0 ||
            (ulong)state.MaximumVolatility * state.TickGroupSize > uint.MaxValue || state.MajorSwapThreshold == 0 ||
            state.MajorSwapThreshold > 88 * pool.TickSpacing || bytes.Slice(66, 16).ContainsAnyExcept((byte)0) ||
            bytes.Slice(110, 16).ContainsAnyExcept((byte)0) || bytes[126..].ContainsAnyExcept((byte)0))
            throw new InvalidDataException("Whirlpool Oracle trading time or adaptive fee policy is not valid for this snapshot.");
        return state;
    }
}
