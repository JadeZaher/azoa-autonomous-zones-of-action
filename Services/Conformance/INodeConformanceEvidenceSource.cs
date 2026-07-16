namespace AZOA.WebAPI.Services.Conformance;

/// <summary>Reads gate evidence from a verifiable build artifact, never an operator claim.</summary>
public interface INodeConformanceEvidenceSource
{
    /// <summary>Loads exactly the required G-suite evidence or returns false.</summary>
    Task<IReadOnlyList<NodeConformanceGateEvidence>?> TryReadAsync(CancellationToken ct = default);
}
