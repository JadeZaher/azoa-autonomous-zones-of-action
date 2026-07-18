namespace AZOA.WebAPI.Services.Conformance;

/// <summary>Reads gate evidence from a verifiable build artifact, never an operator claim.</summary>
public interface INodeConformanceEvidenceSource
{
    /// <summary>Loads exactly the required G-suite evidence and its validity boundary.</summary>
    Task<NodeConformanceEvidenceSnapshot?> TryReadAsync(CancellationToken ct = default);
}
