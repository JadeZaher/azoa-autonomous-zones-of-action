namespace AZOA.WebAPI.Services.Governance;

/// <summary>Enables bounded signed checkpoints for the public governance audit history.</summary>
public sealed class NodeTransparencyHistoryOptions
{
    public const string SectionName = "NodeTransparencyHistory";

    public bool Enabled { get; set; }

    public int MaxAuditEntries { get; set; } = 512;
}
