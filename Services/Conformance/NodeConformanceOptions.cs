namespace AZOA.WebAPI.Services.Conformance;

/// <summary>Configures the optional local-only node conformance surface.</summary>
public sealed class NodeConformanceOptions
{
    public const string SectionName = "NodeConformance";

    public bool Enabled { get; set; }
    public string NodeId { get; set; } = string.Empty;
    public string KeyStoragePath { get; set; } = string.Empty;
    public string EvidenceDirectory { get; set; } = string.Empty;
    public int ManifestLifetimeMinutes { get; set; } = 60;
    public int MaxPayloadBytes { get; set; } = 16 * 1024;
}
