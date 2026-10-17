namespace AZOA.WebAPI.Models.Actions;

/// <summary>One durable workflow attempt with its immutable graph and original authentication evidence.</summary>
public class ActionRun
{
    public ActionRun Clone() => (ActionRun)MemberwiseClone();
    public long Version { get; set; }
    public string? DefinitionSnapshotJson { get; set; }
    public string? DefinitionSnapshotHash { get; set; }
    public string? InvocationIdentity { get; set; }
    public string? InternalDependenciesJson { get; set; }
    public string? OccurrenceBoundariesJson { get; set; }
    public string? InitialInputJson { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? AuthenticationWitnessJson { get; set; }
    public Guid Id { get; set; }

    public Guid ActionId { get; set; }

    public Guid UserId { get; set; }

    public Guid? ActingTenantId { get; set; }

    public ActionRunStatus Status { get; set; } = ActionRunStatus.Pending;

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    public DateTime? EndedAt { get; set; }

    public Guid? ParentRunId { get; set; }

    public Guid? ForkedAtNodeId { get; set; }

    public string? ForkReason { get; set; }

    public string? FailReason { get; set; }

    public Guid? SourceActionId { get; set; }

    public Guid? OriginUserId { get; set; }

    public string? PublishedVersionHash { get; set; }
}
