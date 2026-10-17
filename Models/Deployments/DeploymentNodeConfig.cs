namespace AZOA.WebAPI.Models.Deployments;

public sealed class DeploymentNodeConfig
{
    public DeploymentStartRequest? Deployment { get; set; }
}

public sealed class DeploymentGenerationNodeConfig
{
    public Guid BundleId { get; set; }
    public DeploymentGenerateRequest? Request { get; set; }
}
