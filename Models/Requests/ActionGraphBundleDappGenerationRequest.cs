namespace AZOA.WebAPI.Models.Requests;

public class ActionGraphBundleDappGenerationRequest
{
    public AZOA.WebAPI.Models.Deployments.DeploymentGenerateRequest? Generation { get; set; }
    public string TargetChain { get; set; } = string.Empty;
    public List<Guid> BoundHolonIds { get; set; } = new();
    public Dictionary<string, string> Config { get; set; } = new();
}
