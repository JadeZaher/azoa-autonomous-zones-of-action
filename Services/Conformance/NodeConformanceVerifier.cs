using System.Security.Cryptography;

namespace AZOA.WebAPI.Services.Conformance;

/// <summary>Dependency-free fail-closed verifier for a downloaded L0 conformance document.</summary>
public static class NodeConformanceVerifier
{
    private static readonly HashSet<string> RequiredGates = new(StringComparer.Ordinal)
    {
        "G1", "G2", "G3", "G5", "G7",
    };

    /// <summary>Verifies the document's format, evidence, freshness, rotation, and signature.</summary>
    public static bool TryVerify(
        NodeConformanceDocument? document,
        DateTimeOffset now,
        out NodeConformanceVerificationFailure failure,
        NodeConformanceDocument? previousDocument = null)
    {
        if (document is null || document.SchemaVersion != NodeConformanceCanonicalizer.SchemaVersion
            || document.Manifest.SchemaVersion != NodeConformanceCanonicalizer.SchemaVersion)
        {
            failure = NodeConformanceVerificationFailure.UnsupportedVersion;
            return false;
        }

        if (!TryValidateDescriptor(document.Descriptor, out var currentKey))
        {
            failure = NodeConformanceVerificationFailure.InvalidDescriptor;
            return false;
        }
        using var verifiedCurrentKey = currentKey;

        if (!TryValidateEvidence(document.Manifest.Evidence))
        {
            failure = NodeConformanceVerificationFailure.InvalidEvidence;
            return false;
        }

        if (document.Manifest.IssuedAt > now
            || document.Manifest.ExpiresAt <= now
            || document.Manifest.ExpiresAt <= document.Manifest.IssuedAt
            || document.Manifest.ExpiresAt - document.Manifest.IssuedAt > TimeSpan.FromHours(24))
        {
            failure = NodeConformanceVerificationFailure.Expired;
            return false;
        }

        if (!TryVerifyRotation(document, previousDocument))
        {
            failure = NodeConformanceVerificationFailure.KeyDiscontinuity;
            return false;
        }

        try
        {
            var signature = Convert.FromBase64String(document.Manifest.SignatureBase64);
            if (!verifiedCurrentKey.VerifyData(
                    NodeConformanceCanonicalizer.ManifestSigningBytes(document),
                    signature,
                    HashAlgorithmName.SHA256))
            {
                failure = NodeConformanceVerificationFailure.InvalidSignature;
                return false;
            }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            failure = NodeConformanceVerificationFailure.InvalidSignature;
            return false;
        }

        failure = default;
        return true;
    }

    private static bool TryValidateDescriptor(NodeDescriptor descriptor, out ECDsa currentKey)
    {
        currentKey = ECDsa.Create();
        if (string.IsNullOrWhiteSpace(descriptor.NodeId) || descriptor.NodeId.Length > 128
            || !TryImportAndMatch(descriptor.CurrentKey, currentKey))
        {
            currentKey.Dispose();
            return false;
        }

        return true;
    }

    private static bool TryValidateEvidence(IReadOnlyList<NodeConformanceGateEvidence>? evidence)
        => evidence is { Count: 5 }
            && evidence.Select(item => item.Gate).ToHashSet(StringComparer.Ordinal).SetEquals(RequiredGates)
            && evidence.All(item => item.PassedTestCount > 0 && IsSha256(item.ArtifactSha256));

    private static bool TryVerifyRotation(NodeConformanceDocument current, NodeConformanceDocument? previous)
    {
        if (current.Descriptor.PreviousKey is not null && !TryVerifyContinuity(current.Descriptor))
            return false;

        if (previous is null || string.Equals(
                previous.Descriptor.CurrentKey.KeyId,
                current.Descriptor.CurrentKey.KeyId,
                StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(previous.Descriptor.NodeId, current.Descriptor.NodeId, StringComparison.Ordinal)
            || current.Descriptor.PreviousKey is null
            || !SameKey(previous.Descriptor.CurrentKey, current.Descriptor.PreviousKey.Key))
        {
            return false;
        }

        return TryVerifyContinuity(current.Descriptor);
    }

    private static bool TryVerifyContinuity(NodeDescriptor descriptor)
    {
        var previous = descriptor.PreviousKey;
        if (previous is null)
            return true;

        try
        {
            using var key = ECDsa.Create();
            if (!TryImportAndMatch(previous.Key, key))
                return false;

            return key.VerifyData(
                NodeConformanceCanonicalizer.ContinuitySigningBytes(descriptor.CurrentKey),
                Convert.FromBase64String(previous.ContinuitySignatureBase64),
                HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static bool TryImportAndMatch(NodePublicKey key, ECDsa destination)
    {
        if (!string.Equals(key.Algorithm, NodeConformanceCanonicalizer.Algorithm, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(key.KeyId))
        {
            return false;
        }

        try
        {
            var spki = Convert.FromBase64String(key.SubjectPublicKeyInfoBase64);
            destination.ImportSubjectPublicKeyInfo(spki, out var bytesRead);
            return bytesRead == spki.Length
                && string.Equals(
                    key.KeyId,
                    "sha256:" + Convert.ToHexStringLower(SHA256.HashData(spki)),
                    StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static bool SameKey(NodePublicKey left, NodePublicKey right)
        => string.Equals(left.Algorithm, right.Algorithm, StringComparison.Ordinal)
            && string.Equals(left.KeyId, right.KeyId, StringComparison.Ordinal)
            && string.Equals(left.SubjectPublicKeyInfoBase64, right.SubjectPublicKeyInfoBase64, StringComparison.Ordinal);

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(character => char.IsAsciiHexDigit(character));
}
