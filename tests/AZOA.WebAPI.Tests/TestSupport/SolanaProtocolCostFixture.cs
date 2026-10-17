using System.Buffers.Binary;
using System.Security.Cryptography;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using Solnet.Programs;
using Solnet.Wallet;

namespace AZOA.WebAPI.Tests.TestSupport;

internal static class SolanaProtocolCostFixture
{
    internal static void ConfigureMetadata(SolanaDurableFixture fixture, string prefix, string catalogue)
    {
        var guard = new Account().PublicKey;
        var guardHash = PutProgram(fixture, guard, [1, 2, 3], false);
        var metadataHash = PutProgram(fixture, SolanaNftMetadata.Program, [4, 5, 6], false);
        fixture.Configuration[prefix + "CostGuardProgram"] = guard.Key;
        fixture.Configuration[prefix + "CostGuardProgramDataHash"] = guardHash;
        fixture.Configuration[prefix + "MetadataProgramDataHash"] = metadataHash;
        fixture.Configuration[catalogue + ":0:CodeHash"] = metadataHash;
        fixture.Configuration[catalogue + ":0:SourceRevision"] = SolanaMetadataCreationPolicy.SupportedRevision;
        var clock = new byte[40]; BinaryPrimitives.WriteUInt64LittleEndian(clock, 90);
        BinaryPrimitives.WriteInt64LittleEndian(clock.AsSpan(32), 1_700_000_000);
        fixture.Accounts[SysVars.ClockKey.Key] = BinaryAccount("Sysvar1111111111111111111111111111111111111", clock);
        fixture.Accounts[SysVars.RentKey.Key] = BinaryAccount("Sysvar1111111111111111111111111111111111111", Rent(2));
    }

    internal static byte[] Rent(double threshold)
    {
        var rent = new byte[17]; BinaryPrimitives.WriteUInt64LittleEndian(rent, 3480);
        BinaryPrimitives.WriteDoubleLittleEndian(rent.AsSpan(8), threshold); rent[16] = 50; return rent;
    }

    internal static object BinaryAccount(string owner, byte[] data, bool executable = false)
        => new { owner, executable, lamports = 1UL, data = new[] { Convert.ToBase64String(data), "base64" } };

    internal static string PutProgram(SolanaDurableFixture fixture, PublicKey program, byte[] code, bool mutable)
    {
        var address = SolanaNativeLoader.ProgramData(program);
        var programBytes = new byte[36]; programBytes[0] = 2; address.KeyBytes.CopyTo(programBytes, 4);
        var data = new byte[45 + code.Length]; data[0] = 3;
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(4), 80); data[12] = mutable ? (byte)1 : (byte)0;
        code.CopyTo(data, 45);
        fixture.Accounts[program.Key] = BinaryAccount(SolanaNativeLoader.Program.Key, programBytes, true);
        fixture.Accounts[address.Key] = BinaryAccount(SolanaNativeLoader.Program.Key, data);
        return Convert.ToHexStringLower(SHA256.HashData(code));
    }
}
