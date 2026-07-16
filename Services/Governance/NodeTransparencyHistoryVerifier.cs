using System.Security.Cryptography;
using AZOA.WebAPI.Services.Conformance;

namespace AZOA.WebAPI.Services.Governance;

/// <summary>Dependency-free verifier for a downloaded public governance-history checkpoint.</summary>
public static class NodeTransparencyHistoryVerifier
{
    /// <summary>Validates redacted event commitments, canonical ordering, head, and node signature.</summary>
    public static bool TryVerify(
        NodeTransparencyHistoryDocument? document,
        out NodeTransparencyHistoryVerificationFailure failure)
    {
        if (document?.Checkpoint is null || document.Checkpoint.SchemaVersion != NodeTransparencyHistoryCanonicalizer.SchemaVersion)
        {
            failure = NodeTransparencyHistoryVerificationFailure.UnsupportedVersion;
            return false;
        }

        if (document.Checkpoint.AuditEventCount != document.Entries.Count
            || document.Entries.Any(entry => !IsValidEntry(entry)))
        {
            failure = NodeTransparencyHistoryVerificationFailure.InvalidEntry;
            return false;
        }

        if (!IsCanonicalOrder(document.Entries))
        {
            failure = NodeTransparencyHistoryVerificationFailure.InvalidOrdering;
            return false;
        }

        if (!IsSha256(document.Checkpoint.HeadSha256)
            || !string.Equals(
                document.Checkpoint.HeadSha256,
                NodeTransparencyHistoryCanonicalizer.ComputeHead(document.Entries),
                StringComparison.Ordinal))
        {
            failure = NodeTransparencyHistoryVerificationFailure.InvalidHead;
            return false;
        }

        if (!TryImportKey(document.Checkpoint.Descriptor, out var publicKey))
        {
            failure = NodeTransparencyHistoryVerificationFailure.InvalidDescriptor;
            return false;
        }
        using (publicKey)
        {
            try
            {
                if (!publicKey.VerifyData(
                        NodeTransparencyHistoryCanonicalizer.CheckpointSigningBytes(document.Checkpoint),
                        Convert.FromBase64String(document.Checkpoint.SignatureBase64),
                        HashAlgorithmName.SHA256))
                {
                    failure = NodeTransparencyHistoryVerificationFailure.InvalidSignature;
                    return false;
                }
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
            {
                failure = NodeTransparencyHistoryVerificationFailure.InvalidSignature;
                return false;
            }
        }

        failure = default;
        return true;
    }

    private static bool IsValidEntry(NodeTransparencyHistoryEntry entry)
        => NodeTransparencyHistoryCanonicalizer.IsKnownKind(entry.Kind)
            && entry.OccurredAt != default
            && !string.IsNullOrWhiteSpace(entry.PayloadJson)
            && IsSha256(entry.EntrySha256)
            && string.Equals(
                entry.EntrySha256,
                NodeTransparencyHistoryCanonicalizer.ComputeEntrySha256(
                    entry.Kind, entry.OccurredAt, entry.PayloadJson),
                StringComparison.Ordinal);

    private static bool IsCanonicalOrder(IReadOnlyList<NodeTransparencyHistoryEntry> entries)
    {
        for (var index = 1; index < entries.Count; index++)
        {
            if (Compare(entries[index - 1], entries[index]) > 0)
                return false;
        }
        return true;
    }

    internal static int Compare(NodeTransparencyHistoryEntry left, NodeTransparencyHistoryEntry right)
    {
        var time = left.OccurredAt.CompareTo(right.OccurredAt);
        if (time != 0)
            return time;
        var kind = string.CompareOrdinal(left.Kind, right.Kind);
        return kind != 0 ? kind : string.CompareOrdinal(left.EntrySha256, right.EntrySha256);
    }

    private static bool TryImportKey(NodeDescriptor descriptor, out ECDsa key)
    {
        key = ECDsa.Create();
        if (descriptor is null || descriptor.CurrentKey is null)
        {
            key.Dispose();
            return false;
        }

        var publicKey = descriptor.CurrentKey;
        if (string.IsNullOrWhiteSpace(descriptor.NodeId)
            || descriptor.NodeId.Length > 128
            || !string.Equals(publicKey.Algorithm, NodeConformanceCanonicalizer.Algorithm, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(publicKey.KeyId))
        {
            key.Dispose();
            return false;
        }

        try
        {
            var spki = Convert.FromBase64String(publicKey.SubjectPublicKeyInfoBase64);
            key.ImportSubjectPublicKeyInfo(spki, out var read);
            if (read == spki.Length
                && string.Equals(publicKey.KeyId, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(spki)), StringComparison.Ordinal))
                return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
        }

        key.Dispose();
        return false;
    }

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(character => char.IsAsciiHexDigit(character));
}
