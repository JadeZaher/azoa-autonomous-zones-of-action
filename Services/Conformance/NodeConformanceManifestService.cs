using Microsoft.Extensions.Options;
using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Services.Conformance;

/// <summary>Builds only bounded public conformance facts from current local evidence.</summary>
public sealed class NodeConformanceManifestService : INodeConformanceManifestService
{
    private readonly INodeConformanceEvidenceSource _evidenceSource;
    private readonly INodeIdentityKeyService _identityKeys;
    private readonly IOptions<NodeConformanceOptions> _options;
    private readonly TimeProvider _clock;

    public NodeConformanceManifestService(
        INodeConformanceEvidenceSource evidenceSource,
        INodeIdentityKeyService identityKeys,
        IOptions<NodeConformanceOptions> options,
        TimeProvider? clock = null)
    {
        _evidenceSource = evidenceSource ?? throw new ArgumentNullException(nameof(evidenceSource));
        _identityKeys = identityKeys ?? throw new ArgumentNullException(nameof(identityKeys));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async Task<NodeConformanceDocumentAvailability> TryGetDocumentAsync(CancellationToken ct = default)
    {
        var options = _options.Value;
        if (!options.Enabled || !TryValidateOptions(options))
            return NodeConformanceDocumentAvailability.Unavailable;

        var evidence = await _evidenceSource.TryReadAsync(ct);
        if (evidence is null)
            return NodeConformanceDocumentAvailability.Unavailable;

        var issuedAt = _clock.GetUtcNow();
        var requestedExpiresAt = issuedAt.AddMinutes(options.ManifestLifetimeMinutes);
        var expiresAt = requestedExpiresAt < evidence.ValidUntil ? requestedExpiresAt : evidence.ValidUntil;
        if (expiresAt <= issuedAt)
            return NodeConformanceDocumentAvailability.Unavailable;

        using var key = _identityKeys.GetCurrent();
        var descriptor = key.Descriptor with { NodeId = options.NodeId.Trim() };
        var unsigned = new NodeConformanceDocument(
            NodeConformanceCanonicalizer.SchemaVersion,
            descriptor,
            new NodeConformanceManifest(
                NodeConformanceCanonicalizer.SchemaVersion,
                issuedAt,
                expiresAt,
                evidence.Evidence,
                string.Empty));
        var signed = unsigned with
        {
            Manifest = unsigned.Manifest with
            {
                SignatureBase64 = Convert.ToBase64String(key.Sign(
                    NodeConformanceCanonicalizer.ManifestSigningBytes(unsigned))),
            },
        };

        return NodeConformanceCanonicalizer.SerializeDocument(signed).Length <= options.MaxPayloadBytes
            ? new NodeConformanceDocumentAvailability(true, signed)
            : NodeConformanceDocumentAvailability.Unavailable;
    }

    private static bool TryValidateOptions(NodeConformanceOptions options)
        => !string.IsNullOrWhiteSpace(options.NodeId)
            && options.NodeId.Trim().Length <= 128
            && !string.IsNullOrWhiteSpace(options.KeyStoragePath)
            && !string.IsNullOrWhiteSpace(options.EvidenceDirectory)
            && !string.IsNullOrWhiteSpace(options.ExpectedRepository)
            && !string.IsNullOrWhiteSpace(options.ExpectedWorkflow)
            && options.MaxEvidenceAgeMinutes is > 0 and <= 24 * 60
            && options.ManifestLifetimeMinutes is > 0 and <= 24 * 60
            && options.MaxPayloadBytes is > 0 and <= 64 * 1024;
}
