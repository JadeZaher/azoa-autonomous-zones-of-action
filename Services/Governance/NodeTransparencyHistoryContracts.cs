using AZOA.WebAPI.Services.Conformance;

namespace AZOA.WebAPI.Services.Governance;

/// <summary>A redacted event committed into the public governance-history chain.</summary>
public sealed record NodeTransparencyHistoryEntry(
    string Kind,
    DateTimeOffset OccurredAt,
    string PayloadJson,
    string EntrySha256);

/// <summary>A node-identity-signed checkpoint over an ordered public audit history.</summary>
public sealed record NodeTransparencyHistoryCheckpoint(
    int SchemaVersion,
    NodeDescriptor Descriptor,
    DateTimeOffset IssuedAt,
    int AuditEventCount,
    string HeadSha256,
    string SignatureBase64);

/// <summary>Bounded public history and the checkpoint that authenticates it.</summary>
public sealed record NodeTransparencyHistoryDocument(
    NodeTransparencyHistoryCheckpoint Checkpoint,
    IReadOnlyList<NodeTransparencyHistoryEntry> Entries);

/// <summary>Describes a fail-closed local checkpoint result.</summary>
public sealed record NodeTransparencyHistoryAvailability(
    bool IsAvailable,
    NodeTransparencyHistoryDocument? Document)
{
    public static NodeTransparencyHistoryAvailability Unavailable { get; } = new(false, null);
}

/// <summary>Failure reasons emitted by the dependency-free history verifier.</summary>
public enum NodeTransparencyHistoryVerificationFailure
{
    UnsupportedVersion,
    InvalidDescriptor,
    InvalidEntry,
    InvalidOrdering,
    InvalidHead,
    InvalidSignature,
}
