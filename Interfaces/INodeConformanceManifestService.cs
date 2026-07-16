using AZOA.WebAPI.Services.Conformance;

namespace AZOA.WebAPI.Interfaces;

/// <summary>Builds the public, independently verifiable local node conformance document.</summary>
public interface INodeConformanceManifestService
{
    /// <summary>Builds the enabled node's document from current gate artifacts.</summary>
    Task<NodeConformanceDocumentAvailability> TryGetDocumentAsync(CancellationToken ct = default);
}
