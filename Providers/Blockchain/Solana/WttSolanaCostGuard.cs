using System.Buffers.Binary;
using Solnet.Programs;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Closed instruction for the pinned immutable WTT execution-time cost guard.</summary>
internal static class WttSolanaCostGuard
{
    public static TransactionInstruction BuildMetadata(PublicKey guard, byte[] rentBytes, byte[] metadataCodeHash)
    {
        if (rentBytes.Length != 17 || metadataCodeHash.Length != 32 || guard.Key == SolanaNftMetadata.Program.Key)
            throw new ArgumentException("Metadata cost guard requires exact Rent and executable identity.");
        return new() { ProgramId = guard.KeyBytes, Data = [3, .. rentBytes, .. metadataCodeHash], Keys = new List<AccountMeta>
        {
            AccountMeta.ReadOnly(SysVars.RentKey, false), AccountMeta.ReadOnly(SysVars.ClockKey, false),
            AccountMeta.ReadOnly(SolanaNftMetadata.Program, false),
            AccountMeta.ReadOnly(SolanaNativeLoader.ProgramData(SolanaNftMetadata.Program), false),
        } };
    }

    public static ulong MinimumBalance(byte[] rentBytes, int dataLength)
    {
        if (rentBytes.Length != 17 || dataLength < 0 || rentBytes[16] > 100)
            throw new ArgumentException("Exact Rent sysvar bytes and account data length are required.");
        var rate = BinaryPrimitives.ReadUInt64LittleEndian(rentBytes);
        var threshold = BinaryPrimitives.ReadDoubleLittleEndian(rentBytes.AsSpan(8));
        if (!double.IsFinite(threshold) || threshold < 0) throw new ArgumentException("Rent exemption threshold is invalid.");
        var yearly = checked((128UL + (ulong)dataLength) * rate);
        var minimum = yearly * threshold;
        if (!double.IsFinite(minimum) || minimum >= 18446744073709551616d)
            throw new ArgumentException("Rent minimum exceeds the unsigned protocol range.");
        return (ulong)minimum;
    }

    public static TransactionInstruction Build(PublicKey guard, PublicKey core, PublicKey tokenBridge,
        ulong exactProtocolFee, byte[] rentBytes, byte[] coreCodeHash, byte[] tokenCodeHash)
    {
        if (rentBytes.Length != 17 || coreCodeHash.Length != 32 || tokenCodeHash.Length != 32
            || core.Key == tokenBridge.Key || guard.Key == core.Key || guard.Key == tokenBridge.Key)
            throw new ArgumentException("WTT cost guard requires distinct pinned programs, exact Rent bytes and SHA256 code identities.");
        var bridge = WttSolanaAccounts.Derive(core, "Bridge"u8.ToArray());
        var coreData = SolanaNativeLoader.ProgramData(core); var tokenData = SolanaNativeLoader.ProgramData(tokenBridge);
        var data = new byte[90]; data[0] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(1), exactProtocolFee);
        rentBytes.CopyTo(data, 9); coreCodeHash.CopyTo(data, 26); tokenCodeHash.CopyTo(data, 58);
        return new() { ProgramId = guard.KeyBytes, Data = data, Keys = new List<AccountMeta>
        {
            AccountMeta.ReadOnly(bridge, false), AccountMeta.ReadOnly(SysVars.RentKey, false), AccountMeta.ReadOnly(SysVars.ClockKey, false),
            AccountMeta.ReadOnly(core, false), AccountMeta.ReadOnly(coreData, false),
            AccountMeta.ReadOnly(tokenBridge, false), AccountMeta.ReadOnly(tokenData, false),
            AccountMeta.ReadOnly(WttSolanaAccounts.Derive(tokenBridge, "config"u8.ToArray()), false),
        } };
    }

    public static TransactionInstruction BuildRegistration(PublicKey guard, PublicKey core, PublicKey tokenBridge,
        ulong exactProtocolFee, byte[] rentBytes, byte[] coreCodeHash, byte[] tokenCodeHash, byte[] metadataCodeHash)
    {
        if (metadataCodeHash.Length != 32 || new[] { guard.Key, core.Key, tokenBridge.Key }.Contains(SolanaNftMetadata.Program.Key))
            throw new ArgumentException("Registration requires the distinct canonical metadata program and its exact SHA256 identity.");
        var instruction = Build(guard, core, tokenBridge, exactProtocolFee, rentBytes, coreCodeHash, tokenCodeHash);
        instruction.Data = [.. instruction.Data, .. metadataCodeHash];
        instruction.Data[0] = 2;
        instruction.Keys.Add(AccountMeta.ReadOnly(SolanaNftMetadata.Program, false));
        instruction.Keys.Add(AccountMeta.ReadOnly(SolanaNativeLoader.ProgramData(SolanaNftMetadata.Program), false));
        return instruction;
    }
}
