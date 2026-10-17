using System.Text;
using Solnet.Programs;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Metaplex generated Borsh instruction/account subset; provenance and layout notes are in AGENTS.md.</summary>
internal static class SolanaNftMetadata
{
    internal static readonly PublicKey Program = new("metaqbxxUerdq28cj1RbAWkYQm3ybzjb6a8bt518x1s");
    internal const string MasterEditionStandard = "MetaplexNonFungible";
    internal const string CredentialStandard = "SPLFrozenCredential";

    internal static PublicKey Metadata(PublicKey mint) => Derive(mint, false);
    internal static PublicKey Edition(PublicKey mint) => Derive(mint, true);
    private static PublicKey Derive(PublicKey mint, bool edition)
    {
        var seeds = new List<byte[]> { Encoding.UTF8.GetBytes("metadata"), Program.KeyBytes, mint.KeyBytes };
        if (edition) seeds.Add(Encoding.UTF8.GetBytes("edition"));
        if (!PublicKey.TryFindProgramAddress(seeds, Program, out var address, out _)) throw new InvalidOperationException("Metaplex PDA derivation failed.");
        return address;
    }

    internal static TransactionInstruction Create(PublicKey mint, PublicKey mintAuthority, PublicKey payer, PublicKey issuer, string name, string uri)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write((byte)33);
        WriteText(writer, name, 32); WriteText(writer, string.Empty, 10); WriteText(writer, uri, 200);
        writer.Write((ushort)0);
        writer.Write((byte)1); writer.Write((uint)1); writer.Write(issuer.KeyBytes); writer.Write(true); writer.Write((byte)100);
        writer.Write((byte)0); writer.Write((byte)0);
        writer.Write(false); writer.Write((byte)0);
        return new TransactionInstruction { ProgramId = Program.KeyBytes, Keys = new List<AccountMeta>
        {
            AccountMeta.Writable(Metadata(mint), false), AccountMeta.ReadOnly(mint, false),
            AccountMeta.ReadOnly(mintAuthority, true), AccountMeta.Writable(payer, true),
            AccountMeta.ReadOnly(issuer, true), AccountMeta.ReadOnly(SystemProgram.ProgramIdKey, false), AccountMeta.ReadOnly(SysVars.RentKey, false),
        }, Data = stream.ToArray() };
    }

    internal static TransactionInstruction CreateEdition(PublicKey mint, PublicKey authority, PublicKey payer)
    {
        var data = new byte[10]; data[0] = 17; data[1] = 1;
        return new TransactionInstruction { ProgramId = Program.KeyBytes, Keys = new List<AccountMeta>
        {
            AccountMeta.Writable(Edition(mint), false), AccountMeta.Writable(mint, false),
            AccountMeta.ReadOnly(authority, true), AccountMeta.ReadOnly(authority, true), AccountMeta.Writable(payer, true),
            AccountMeta.Writable(Metadata(mint), false), AccountMeta.ReadOnly(TokenProgram.ProgramIdKey, false),
            AccountMeta.ReadOnly(SystemProgram.ProgramIdKey, false), AccountMeta.ReadOnly(SysVars.RentKey, false),
        }, Data = data };
    }

    internal sealed record MetadataEvidence(string Mint, string Issuer, string Name, string Uri, bool Immutable, bool VerifiedCreator);
    internal static MetadataEvidence Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream, Encoding.UTF8, false);
        if (reader.ReadByte() != 4) throw new InvalidDataException("Metaplex account is not MetadataV1.");
        var issuer = ReadPublicKey(reader); var mint = ReadPublicKey(reader);
        var name = ReadText(reader, 32); _ = ReadText(reader, 10); var uri = ReadText(reader, 200);
        _ = reader.ReadUInt16();
        var verifiedCreator = false;
        var creators = reader.ReadByte();
        if (creators > 1) throw new InvalidDataException("Invalid Metaplex creator option.");
        if (creators == 1)
        {
            var count = reader.ReadUInt32(); if (count > 5) throw new InvalidDataException("Invalid Metaplex creators.");
            for (var i = 0; i < count; i++)
            {
                var address = ReadPublicKey(reader); var verified = reader.ReadBoolean(); var share = reader.ReadByte();
                verifiedCreator |= address == issuer && verified && share == 100;
            }
        }
        _ = reader.ReadBoolean(); var mutable = reader.ReadBoolean();
        return new(mint, issuer, name, uri, !mutable, verifiedCreator);
    }

    private static string ReadPublicKey(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(32); if (bytes.Length != 32) throw new InvalidDataException("Truncated metadata key.");
        return new PublicKey(bytes).Key;
    }
    private static string ReadText(BinaryReader reader, int maximum)
    {
        var length = reader.ReadUInt32(); if (length > maximum) throw new InvalidDataException("Metaplex string exceeds layout limit.");
        var bytes = reader.ReadBytes((int)length); if (bytes.Length != length) throw new InvalidDataException("Truncated metadata string.");
        return new UTF8Encoding(false, true).GetString(bytes).TrimEnd('\0');
    }
    private static void WriteText(BinaryWriter writer, string text, int maximum)
    {
        var bytes = Encoding.UTF8.GetBytes(text); if (bytes.Length > maximum) throw new ArgumentException("Metadata exceeds Metaplex byte limits.");
        writer.Write((uint)bytes.Length); writer.Write(bytes);
    }
}
