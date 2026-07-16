using System.Text;
using System.Text.Json;

namespace AZOA.WebAPI.Services.Conformance;

/// <summary>Canonical byte writer for the public L0 protocol records.</summary>
public static class NodeConformanceCanonicalizer
{
    public const int SchemaVersion = 1;
    public const string Algorithm = "ECDSA_P256_SHA256";
    private const string ManifestDomain = "AZOA.node-conformance.manifest.v1\0";
    private const string ContinuityDomain = "AZOA.node-conformance.key-continuity.v1\0";

    /// <summary>Returns the domain-separated canonical bytes signed for a manifest.</summary>
    public static byte[] ManifestSigningBytes(NodeConformanceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return DomainSeparated(ManifestDomain, WriteDocument(document, includeSignature: false));
    }

    /// <summary>Returns the domain-separated canonical bytes signed by a prior key at rotation.</summary>
    public static byte[] ContinuitySigningBytes(NodePublicKey currentKey)
    {
        ArgumentNullException.ThrowIfNull(currentKey);
        return DomainSeparated(ContinuityDomain, WriteKey(currentKey));
    }

    /// <summary>Returns deterministic UTF-8 JSON for transport and golden-vector tests.</summary>
    public static byte[] SerializeDocument(NodeConformanceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return WriteDocument(document, includeSignature: true);
    }

    private static byte[] WriteDocument(NodeConformanceDocument document, bool includeSignature)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", document.SchemaVersion);
            writer.WritePropertyName("descriptor");
            WriteDescriptor(writer, document.Descriptor);
            writer.WritePropertyName("manifest");
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", document.Manifest.SchemaVersion);
            writer.WriteString("issuedAt", FormatTime(document.Manifest.IssuedAt));
            writer.WriteString("expiresAt", FormatTime(document.Manifest.ExpiresAt));
            writer.WritePropertyName("evidence");
            writer.WriteStartArray();
            foreach (var item in document.Manifest.Evidence.OrderBy(item => item.Gate, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("gate", item.Gate);
                writer.WriteString("artifactSha256", item.ArtifactSha256);
                writer.WriteNumber("passedTestCount", item.PassedTestCount);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (includeSignature)
                writer.WriteString("signature", document.Manifest.SignatureBase64);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static byte[] WriteKey(NodePublicKey key)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteKey(writer, key);
        }

        return stream.ToArray();
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

    private static byte[] DomainSeparated(string domain, byte[] payload)
        => System.Text.Encoding.UTF8.GetBytes(domain).Concat(payload).ToArray();

    private static string FormatTime(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
