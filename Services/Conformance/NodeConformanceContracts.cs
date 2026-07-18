using System.Text.Json.Serialization;

namespace AZOA.WebAPI.Services.Conformance;

/// <summary>Trusted CI metadata that binds local conformance artifacts to one source revision.</summary>
public sealed record NodeConformanceEvidenceMetadata(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("commit")] string Commit,
    [property: JsonPropertyName("workflow")] string Workflow,
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("generatedAt")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("files")] IReadOnlyList<NodeConformanceEvidenceFile> Files);

/// <summary>One named SHA-256 artifact declared by CI metadata.</summary>
public sealed record NodeConformanceEvidenceFile(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>Public descriptor and proof document served from the well-known endpoint.</summary>
public sealed record NodeConformanceDocument(
    int SchemaVersion,
    NodeDescriptor Descriptor,
    NodeConformanceManifest Manifest);

/// <summary>Public identity facts for one sovereign node.</summary>
public sealed record NodeDescriptor(
    string NodeId,
    NodePublicKey CurrentKey,
    NodePreviousKeyContinuity? PreviousKey);

/// <summary>Public signing key in SubjectPublicKeyInfo form.</summary>
public sealed record NodePublicKey(
    string Algorithm,
    string KeyId,
    string SubjectPublicKeyInfoBase64);

/// <summary>Proof that the immediately preceding node key authorized the current key.</summary>
public sealed record NodePreviousKeyContinuity(
    NodePublicKey Key,
    string ContinuitySignatureBase64);

/// <summary>Versioned, expiring statement over independently hashed gate artifacts.</summary>
public sealed record NodeConformanceManifest(
    int SchemaVersion,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<NodeConformanceGateEvidence> Evidence,
    string SignatureBase64);

/// <summary>Digest and test count derived from one required CI test-result artifact.</summary>
public sealed record NodeConformanceGateEvidence(
    string Gate,
    string ArtifactSha256,
    int PassedTestCount);

/// <summary>Validated gate evidence and the latest instant it may support a manifest.</summary>
public sealed record NodeConformanceEvidenceSnapshot(
    IReadOnlyList<NodeConformanceGateEvidence> Evidence,
    DateTimeOffset ValidUntil);

/// <summary>Result shape for the public endpoint; unavailable detail is intentionally non-public.</summary>
public sealed record NodeConformanceDocumentAvailability(
    bool IsAvailable,
    NodeConformanceDocument? Document)
{
    public static NodeConformanceDocumentAvailability Unavailable { get; } = new(false, null);
}

/// <summary>Fail-closed reason emitted by the dependency-free offline verifier.</summary>
public enum NodeConformanceVerificationFailure
{
    UnsupportedVersion,
    InvalidDescriptor,
    InvalidEvidence,
    Expired,
    InvalidSignature,
    KeyDiscontinuity,
}
