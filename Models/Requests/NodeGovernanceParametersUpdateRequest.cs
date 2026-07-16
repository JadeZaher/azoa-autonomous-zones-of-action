namespace AZOA.WebAPI.Models.Requests;

public sealed class NodeGovernanceParametersUpdateRequest
{
    public long? ExpectedVersion { get; set; }

    public IReadOnlyList<string>? AllowedChains { get; set; }

    public IReadOnlyList<string>? AllowedAssetTypes { get; set; }
}
