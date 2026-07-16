namespace AZOA.WebAPI.Services.Governance;

public sealed class NodeGovernanceOptions
{
    public const string SectionName = "NodeGovernance";

    public string[]? AllowedChains { get; set; }
    public string[]? AllowedAssetTypes { get; set; }
}
