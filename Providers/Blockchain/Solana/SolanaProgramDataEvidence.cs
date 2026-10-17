using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Validates one loader-v3 executable and its exact pinned program-data code snapshot.</summary>
internal static class SolanaProgramDataEvidence
{
    internal static void Require(JsonElement program, JsonElement data, PublicKey expectedData, string hash)
    {
        var programBytes = SolanaAccountData.ReadOwned(program, SolanaNativeLoader.Program.Key, true);
        var dataBytes = SolanaAccountData.ReadOwned(data, SolanaNativeLoader.Program.Key, false);
        if (programBytes.Length != SolanaNativeLoader.ProgramSize || BinaryPrimitives.ReadUInt32LittleEndian(programBytes) != 2
            || !programBytes.AsSpan(4).SequenceEqual(expectedData.KeyBytes) || dataBytes.Length < SolanaNativeLoader.ProgramDataHeaderSize
            || BinaryPrimitives.ReadUInt32LittleEndian(dataBytes) != 3
            || Convert.ToHexString(SHA256.HashData(dataBytes[SolanaNativeLoader.ProgramDataHeaderSize..])).ToLowerInvariant() != hash)
            throw new InvalidDataException("Executable program or program-data code differs from the pinned deployment.");
    }
}
