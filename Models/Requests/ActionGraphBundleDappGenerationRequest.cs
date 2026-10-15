namespace AZOA.WebAPI.Models.Requests;

public class ActionGraphBundleDappGenerationRequest
{
    public string TargetChain { get; set; } = string.Empty;
    public List<Guid> BoundHolonIds { get; set; } = new();
    public Dictionary<string, string> Config { get; set; } = new();
}
