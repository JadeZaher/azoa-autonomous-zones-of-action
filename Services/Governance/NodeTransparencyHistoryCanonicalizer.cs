using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Services.Conformance;

namespace AZOA.WebAPI.Services.Governance;

/// <summary>Canonical byte writer and hash chain for public governance-history checkpoints.</summary>
public static class NodeTransparencyHistoryCanonicalizer
{
    public const int SchemaVersion = 1;
    private const string EntryDomain = "AZOA.node-transparency.audit-entry.v1\0";
    private const string ChainDomain = "AZOA.node-transparency.audit-chain.v1\0";
    private const string CheckpointDomain = "AZOA.node-transparency.audit-checkpoint.v1\0";

    /// <summary>Creates the entry commitment from a redacted public payload.</summary>
    public static string ComputeEntrySha256(string kind, DateTimeOffset occurredAt, string payloadJson)
    {
        if (!IsKnownKind(kind) || occurredAt == default || string.IsNullOrWhiteSpace(payloadJson))
            throw new ArgumentException("A public audit entry is structurally invalid.");

        return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            EntryDomain + kind + "\0" + FormatTime(occurredAt) + "\0" + payloadJson)));
    }

    /// <summary>Returns the ordered chain head including every supplied public entry.</summary>
    public static string ComputeHead(IReadOnlyList<NodeTransparencyHistoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var previous = new byte[32];
        foreach (var entry in entries)
        {
            var entryHash = Convert.FromHexString(ComputeEntrySha256(entry.Kind, entry.OccurredAt, entry.PayloadJson));
            previous = SHA256.HashData(Concat(System.Text.Encoding.UTF8.GetBytes(ChainDomain), previous, entryHash));
        }

        return Convert.ToHexStringLower(previous);
    }

    /// <summary>Returns domain-separated canonical bytes signed for a history checkpoint.</summary>
    public static byte[] CheckpointSigningBytes(NodeTransparencyHistoryCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return Concat(System.Text.Encoding.UTF8.GetBytes(CheckpointDomain), WriteCheckpoint(checkpoint, includeSignature: false));
    }

    /// <summary>Returns deterministic UTF-8 JSON for public transport and golden-vector tests.</summary>
    public static byte[] SerializeDocument(NodeTransparencyHistoryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("checkpoint");
            WriteCheckpoint(writer, document.Checkpoint, includeSignature: true);
            writer.WritePropertyName("entries");
            writer.WriteStartArray();
            foreach (var entry in document.Entries)
                WriteEntry(writer, entry);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Validates that a kind is part of this versioned public protocol.</summary>
    public static bool IsKnownKind(string value)
        => value is "governance" or "fee-schedule" or "treasury";

    private static byte[] WriteCheckpoint(NodeTransparencyHistoryCheckpoint checkpoint, bool includeSignature)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCheckpoint(writer, checkpoint, includeSignature);
        return stream.ToArray();
    }

    private static void WriteCheckpoint(
        Utf8JsonWriter writer,
        NodeTransparencyHistoryCheckpoint checkpoint,
        bool includeSignature)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", checkpoint.SchemaVersion);
        writer.WritePropertyName("descriptor");
        WriteDescriptor(writer, checkpoint.Descriptor);
        writer.WriteString("issuedAt", FormatTime(checkpoint.IssuedAt));
        writer.WriteNumber("auditEventCount", checkpoint.AuditEventCount);
        writer.WriteString("headSha256", checkpoint.HeadSha256);
        if (includeSignature)
            writer.WriteString("signature", checkpoint.SignatureBase64);
        writer.WriteEndObject();
    }

    private static void WriteEntry(Utf8JsonWriter writer, NodeTransparencyHistoryEntry entry)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", entry.Kind);
        writer.WriteString("occurredAt", FormatTime(entry.OccurredAt));
        writer.WriteString("payloadJson", entry.PayloadJson);
        writer.WriteString("entrySha256", entry.EntrySha256);
        writer.WriteEndObject();
    }

    private static void WriteDescriptor(Utf8JsonWriter writer, NodeDescriptor descriptor)
    {
        writer.WriteStartObject();
        writer.WriteString("nodeId", descriptor.NodeId);
        writer.WritePropertyName("currentKey");
        WriteKey(writer, descriptor.CurrentKey);
        if (descriptor.PreviousKey is not null)
        {
            writer.WritePropertyName("previousKey");
            writer.WriteStartObject();
            writer.WritePropertyName("key");
            WriteKey(writer, descriptor.PreviousKey.Key);
            writer.WriteString("continuitySignature", descriptor.PreviousKey.ContinuitySignatureBase64);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static void WriteKey(Utf8JsonWriter writer, NodePublicKey key)
    {
        writer.WriteStartObject();
        writer.WriteString("algorithm", key.Algorithm);
        writer.WriteString("keyId", key.KeyId);
        writer.WriteString("subjectPublicKeyInfo", key.SubjectPublicKeyInfoBase64);
        writer.WriteEndObject();
    }

    private static string FormatTime(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static byte[] Concat(params byte[][] parts)
    {
        var length = parts.Sum(part => part.Length);
        var result = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }
        return result;
    }
}
