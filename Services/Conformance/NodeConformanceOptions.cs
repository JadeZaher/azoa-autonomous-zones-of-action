using System.Reflection;

namespace AZOA.WebAPI.Services.Conformance;

/// <summary>Configures the optional local-only node conformance surface.</summary>
public sealed class NodeConformanceOptions
{
    public const string SectionName = "NodeConformance";

    public bool Enabled { get; set; }
    public string NodeId { get; set; } = string.Empty;
    public string KeyStoragePath { get; set; } = string.Empty;
    public string EvidenceDirectory { get; set; } = string.Empty;
    public string ExpectedRepository { get; set; } = string.Empty;
    public string ExpectedWorkflow { get; set; } = string.Empty;
    public string ExpectedSourceRevision { get; } = NodeConformanceRuntimeRevision.Value;
    public int MaxEvidenceAgeMinutes { get; set; } = 24 * 60;
    public int ManifestLifetimeMinutes { get; set; } = 60;
    public int MaxPayloadBytes { get; set; } = 16 * 1024;
}

internal static class NodeConformanceRuntimeRevision
{
    public static string Value { get; } = TryGet();

    private static string TryGet()
    {
        var informationalVersion = typeof(NodeConformanceOptions).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var separator = informationalVersion?.LastIndexOf('+') ?? -1;
        var candidate = separator >= 0 ? informationalVersion![(separator + 1)..] : null;
        return candidate is { Length: 40 }
            && candidate.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
                ? candidate
                : string.Empty;
    }
}
