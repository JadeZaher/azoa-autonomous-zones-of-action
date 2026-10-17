using System.Text;
using AZOA.WebAPI.Models.Deployments;
using Solnet.Programs;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Metaplex DataV2 create/update codec; official generated instruction provenance is in AGENTS.md.</summary>
internal static class SolanaNativeMetadata
{
    internal sealed record Value(string Authority, string Mint, string Name, string Symbol, string Uri,
        ushort Royalty, IReadOnlyList<SolanaMetadataCreator>? Creators, bool PrimarySale, bool Mutable,
        byte[]? Collection, byte[]? Uses);

    internal static Value Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, false);
        if (reader.ReadByte() != 4) throw new InvalidDataException("Expected Metaplex MetadataV1 account.");
        var authority = Key(reader); var mint = Key(reader);
        var name = Text(reader, 32); var symbol = Text(reader, 10); var uri = Text(reader, 200);
        var royalty = reader.ReadUInt16();
        List<SolanaMetadataCreator>? creators = null;
        if (Option(reader))
        {
            var count = reader.ReadUInt32();
            if (count > 5) throw new InvalidDataException("Metadata creator count exceeds protocol bounds.");
            creators = [];
            for (var index = 0; index < count; index++) creators.Add(new(Key(reader), reader.ReadBoolean(), reader.ReadByte()));
        }
        var primarySale = reader.ReadBoolean(); var mutable = reader.ReadBoolean();
        if (Option(reader)) _ = reader.ReadByte();
        if (Option(reader)) _ = reader.ReadByte();
        var collection = Option(reader) ? Bytes(reader, 33) : null;
        var uses = Option(reader) ? Bytes(reader, 17) : null;
        return new(authority, mint, name, symbol, uri, royalty, creators, primarySale, mutable, collection, uses);
    }

    internal static TransactionInstruction Create(PublicKey mint, PublicKey mintAuthority, PublicKey payer,
        PublicKey updateAuthority, SolanaAccountArtifact artifact)
    {
        if (artifact.Creators?.Any(creator => creator.Verified && creator.Address != updateAuthority.Key) == true)
            throw new ArgumentException("Only the signing update authority can start as a verified creator.");
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write((byte)33);
        Data(writer, artifact.Name!, artifact.Symbol ?? "", artifact.Uri!, artifact.SellerFeeBasisPoints ?? 0, artifact.Creators, null, null);
        writer.Write(artifact.Mutable ?? false); writer.Write((byte)0);
        return new TransactionInstruction { ProgramId = SolanaNftMetadata.Program.KeyBytes, Keys = new List<AccountMeta>
        {
            AccountMeta.Writable(SolanaNftMetadata.Metadata(mint), false), AccountMeta.ReadOnly(mint, false),
            AccountMeta.ReadOnly(mintAuthority, true), AccountMeta.Writable(payer, true), AccountMeta.ReadOnly(updateAuthority, true),
            AccountMeta.ReadOnly(SystemProgram.ProgramIdKey, false), AccountMeta.ReadOnly(SysVars.RentKey, false),
        }, Data = stream.ToArray() };
    }

    internal static TransactionInstruction Update(PublicKey mint, PublicKey authority, SolanaAccountArtifact artifact, Value existing)
    {
        if (!existing.Mutable || existing.Authority != authority.Key || existing.Mint != mint.Key)
            throw new ArgumentException("Current mutable metadata authority is required.");
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write((byte)15); writer.Write((byte)1);
        Data(writer, artifact.Name ?? existing.Name, artifact.Symbol ?? existing.Symbol, artifact.Uri ?? existing.Uri,
            artifact.SellerFeeBasisPoints ?? existing.Royalty, artifact.Creators ?? existing.Creators, existing.Collection, existing.Uses);
        writer.Write((byte)(artifact.UpdateAuthority is null ? 0 : 1));
        if (artifact.UpdateAuthority is not null) writer.Write(new PublicKey(artifact.UpdateAuthority).KeyBytes);
        writer.Write((byte)0);
        writer.Write((byte)1); writer.Write(artifact.Mutable ?? existing.Mutable);
        return new TransactionInstruction { ProgramId = SolanaNftMetadata.Program.KeyBytes,
            Keys = new List<AccountMeta> { AccountMeta.Writable(SolanaNftMetadata.Metadata(mint), false), AccountMeta.ReadOnly(authority, true) },
            Data = stream.ToArray() };
    }

    private static void Data(BinaryWriter writer, string name, string symbol, string uri, ushort royalty,
        IReadOnlyList<SolanaMetadataCreator>? creators, byte[]? collection, byte[]? uses)
    {
        if (royalty > 10_000 || creators is { Count: > 5 } || creators is { Count: > 0 } && creators.Sum(creator => creator.Share) != 100
            || creators is not null && creators.Select(creator => creator.Address).Distinct(StringComparer.Ordinal).Count() != creators.Count)
            throw new ArgumentException("Invalid Metaplex royalties or creator shares.");
        WriteText(writer, name, 32); WriteText(writer, symbol, 10); WriteText(writer, uri, 200); writer.Write(royalty);
        writer.Write((byte)(creators is null ? 0 : 1));
        if (creators is not null)
        {
            writer.Write((uint)creators.Count);
            foreach (var creator in creators) { writer.Write(new PublicKey(creator.Address).KeyBytes); writer.Write(creator.Verified); writer.Write(creator.Share); }
        }
        writer.Write((byte)(collection is null ? 0 : 1)); if (collection is not null) writer.Write(collection);
        writer.Write((byte)(uses is null ? 0 : 1)); if (uses is not null) writer.Write(uses);
    }

    private static void WriteText(BinaryWriter writer, string value, int maximum)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > maximum || value.Contains('\0')) throw new ArgumentException("Metadata text exceeds protocol bounds.");
        writer.Write((uint)bytes.Length); writer.Write(bytes);
    }
    private static string Text(BinaryReader reader, int maximum)
    {
        var length = reader.ReadUInt32();
        if (length > maximum) throw new InvalidDataException("Metadata text exceeds protocol bounds.");
        return new UTF8Encoding(false, true).GetString(Bytes(reader, (int)length)).TrimEnd('\0');
    }
    private static string Key(BinaryReader reader) => new PublicKey(Bytes(reader, 32)).Key;
    private static byte[] Bytes(BinaryReader reader, int count)
    {
        var bytes = reader.ReadBytes(count);
        return bytes.Length == count ? bytes : throw new EndOfStreamException("Truncated Metaplex metadata.");
    }
    private static bool Option(BinaryReader reader) => reader.ReadByte() switch
    { 0 => false, 1 => true, _ => throw new InvalidDataException("Invalid Borsh option.") };
}
