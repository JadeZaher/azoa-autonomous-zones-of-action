namespace AZOA.WebAPI.Models.Actions;

/// <summary>Server-validated source revision and per-actor concurrency budget.</summary>
public sealed record ActionSourceAdmission(Guid ActionId, long Version, string? PublishedHash, int? ConcurrentLimit);

/// <summary>One atomic run, execution set and initial saga admission.</summary>
public sealed record ActionWorkflowAdmission(ActionRun Run, ActionDefinition Graph,
    IReadOnlyList<ActionSourceAdmission> Sources, Guid EntryNodeId,
    AZOA.WebAPI.Models.Deployments.DeploymentAuthorityWitness? AuthorityWitness = null);
