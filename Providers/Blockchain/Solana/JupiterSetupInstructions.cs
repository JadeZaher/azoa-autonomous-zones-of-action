using System.Buffers.Binary;
using Solnet.Programs;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Validates the closed ATA setup and compute prelude supplied by Jupiter.</summary>
internal static class JupiterSetupInstructions
{
    internal static JupiterInstruction CreateOwnerAta(string owner, string mint)
    {
        var ata = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(new PublicKey(owner), new PublicKey(mint)).Key;
        return new(AssociatedTokenAccountProgram.ProgramIdKey.Key,
        [
            new(owner, true, true), new(ata, false, true), new(owner, false, false),
            new(mint, false, false), new(SystemProgram.ProgramIdKey.Key, false, false),
            new(TokenProgram.ProgramIdKey.Key, false, false)
        ], [1]);
    }

    internal static IReadOnlyList<string> ValidateSetup(IReadOnlyList<JupiterInstruction> instructions,
        string owner, string inputMint, string outputMint)
    {
        if (instructions.Count > 2) throw new InvalidDataException("Jupiter setup exceeds the two owner token accounts.");
        var created = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instruction in instructions)
        {
            if (instruction.Program != AssociatedTokenAccountProgram.ProgramIdKey.Key ||
                !instruction.Data.AsSpan().SequenceEqual(new byte[] { 1 }) || instruction.Accounts.Count != 6)
                throw new InvalidDataException("Jupiter setup is not an exact idempotent ATA creation.");
            var mint = instruction.Accounts[3].Address;
            if (mint != inputMint && mint != outputMint)
                throw new InvalidDataException("Jupiter setup changed the allowed mint.");
            var expected = CreateOwnerAta(owner, mint);
            if (!instruction.Accounts.SequenceEqual(expected.Accounts) || !created.Add(expected.Accounts[1].Address))
                throw new InvalidDataException("Jupiter setup changed a role, privilege, or repeated an account creation.");
        }
        return created.Order(StringComparer.Ordinal).ToArray();
    }

    internal static (uint? Units, ulong? MicroLamports) ValidateCompute(IReadOnlyList<JupiterInstruction> instructions,
        uint maximumUnits, ulong maximumMicroLamports)
    {
        if (maximumUnits is 0 or > 1_400_000 || instructions.Count > 2)
            throw new InvalidDataException("Jupiter compute policy is outside the protocol limit.");
        uint? units = null;
        ulong? price = null;
        foreach (var instruction in instructions)
        {
            if (instruction.Program != SolanaComputeBudget.Program.Key || instruction.Accounts.Count != 0)
                throw new InvalidDataException("Jupiter compute prelude changed program or account authority.");
            var data = instruction.Data.AsSpan();
            if (data.Length == 5 && data[0] == 2 && units is null)
            {
                units = BinaryPrimitives.ReadUInt32LittleEndian(data[1..]);
                if (units == 0 || units > maximumUnits) throw new InvalidDataException("Jupiter compute units exceed policy.");
            }
            else if (data.Length == 9 && data[0] == 3 && price is null)
            {
                price = BinaryPrimitives.ReadUInt64LittleEndian(data[1..]);
                if (price > maximumMicroLamports) throw new InvalidDataException("Jupiter compute price exceeds policy.");
            }
            else throw new InvalidDataException("Jupiter compute prelude contains duplicate or unreviewed instructions.");
        }
        return (units, price);
    }
}
