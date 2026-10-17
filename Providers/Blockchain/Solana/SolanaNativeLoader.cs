using System.Security.Cryptography;
using System.Text;
using Solnet.Programs;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Solana loader-v3 bincode instructions and seeded account identities; provenance is in AGENTS.md.</summary>
internal static class SolanaNativeLoader
{
    internal static readonly PublicKey Program = new("BPFLoaderUpgradeab1e11111111111111111111111");
    internal const int BufferHeaderSize = 37;
    internal const int ProgramSize = 36;
    internal const int ProgramDataHeaderSize = 45;
    internal const int WriteChunkSize = 512;

    internal static string Seed(string operationKey, string role)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"azoa:loader-v3:{role}:{operationKey}"))).ToLowerInvariant()[..32];

    internal static PublicKey Seeded(PublicKey authority, string seed)
        => new(SHA256.HashData([.. authority.KeyBytes, .. Encoding.UTF8.GetBytes(seed), .. Program.KeyBytes]));

    internal static PublicKey ProgramData(PublicKey program)
    {
        if (!PublicKey.TryFindProgramAddress([program.KeyBytes], Program, out var address, out _))
            throw new InvalidOperationException("Loader program-data derivation failed.");
        return address;
    }

    internal static TransactionInstruction InitializeBuffer(PublicKey buffer, PublicKey authority)
        => Instruction(0, [AccountMeta.Writable(buffer, false), AccountMeta.ReadOnly(authority, false)]);

    internal static TransactionInstruction Write(PublicKey buffer, PublicKey authority, uint offset, byte[] bytes)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write((uint)1); writer.Write(offset); writer.Write((ulong)bytes.Length); writer.Write(bytes);
        return new TransactionInstruction { ProgramId = Program.KeyBytes,
            Keys = new List<AccountMeta> { AccountMeta.Writable(buffer, false), AccountMeta.ReadOnly(authority, true) }, Data = stream.ToArray() };
    }

    internal static TransactionInstruction Deploy(PublicKey payer, PublicKey program, PublicKey buffer, PublicKey authority, uint maximumLength)
        => Instruction(2, [AccountMeta.Writable(payer, true), AccountMeta.Writable(ProgramData(program), false),
            AccountMeta.Writable(program, false), AccountMeta.Writable(buffer, false), AccountMeta.ReadOnly(SysVars.RentKey, false),
            AccountMeta.ReadOnly(SysVars.ClockKey, false), AccountMeta.ReadOnly(SystemProgram.ProgramIdKey, false), AccountMeta.ReadOnly(authority, true)],
            writer => writer.Write((ulong)maximumLength));

    internal static TransactionInstruction Upgrade(PublicKey payer, PublicKey program, PublicKey buffer, PublicKey authority)
        => Instruction(3, [AccountMeta.Writable(ProgramData(program), false), AccountMeta.Writable(program, false),
            AccountMeta.Writable(buffer, false), AccountMeta.Writable(payer, false), AccountMeta.ReadOnly(SysVars.RentKey, false),
            AccountMeta.ReadOnly(SysVars.ClockKey, false), AccountMeta.ReadOnly(authority, true)]);

    internal static TransactionInstruction RemoveAuthority(PublicKey program, PublicKey authority)
        => Instruction(4, [AccountMeta.Writable(ProgramData(program), false), AccountMeta.ReadOnly(authority, true)]);

    internal static TransactionInstruction CloseBuffer(PublicKey buffer, PublicKey recipient, PublicKey authority)
        => Instruction(5, [AccountMeta.Writable(buffer, false), AccountMeta.Writable(recipient, false), AccountMeta.ReadOnly(authority, true)]);

    internal static bool IsElf(byte[] bytes)
        => bytes.Length is >= 64 and <= 1_048_576 && bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0x7f, 0x45, 0x4c, 0x46 })
            && bytes[4] == 2 && bytes[5] == 1 && bytes[6] == 1
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(18, 2)) == 247;

    private static TransactionInstruction Instruction(uint discriminator, IReadOnlyList<AccountMeta> accounts, Action<BinaryWriter>? append = null)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(discriminator); append?.Invoke(writer);
        return new TransactionInstruction { ProgramId = Program.KeyBytes, Keys = accounts.ToList(), Data = stream.ToArray() };
    }
}
