namespace AZOA.WebAPI.Models.Actions;

/// <summary>Versioned node outcome with immutable input, domain correlation and signing evidence.</summary>
public class ActionNodeExecution
{
    public long Version { get; set; }
    public DateTime? WaitUntil { get; set; }
    public bool ManualAdvanceReleased { get; set; }
    public string? FrozenInput { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SigningAuthorityJson { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? AdmissionRejection { get; set; }
    public ActionExternalOperationReference? ExternalOperation { get; set; }
    public string? RecoveryState { get; set; }
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    public Guid NodeId { get; set; }

    public ActionNodeState State { get; set; } = ActionNodeState.Pending;

    public string? Output { get; set; }

    public string? Error { get; set; }

    public string? TxHash { get; set; }

    public string? ChainType { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    public DateTime? EndedAt { get; set; }

    public ActionNodeExecution Clone() => new()
    {
        Id        = Id,
        RunId     = RunId,
        NodeId    = NodeId,
        State     = State,
        Output    = Output,
        Error     = Error,
        TxHash    = TxHash,
        ChainType = ChainType,
        StartedAt = StartedAt,
        EndedAt   = EndedAt,
        Version = Version,
        WaitUntil = WaitUntil,
        ManualAdvanceReleased = ManualAdvanceReleased,
        FrozenInput = FrozenInput,
        SigningAuthorityJson = SigningAuthorityJson,
        AdmissionRejection = AdmissionRejection,
        ExternalOperation = ExternalOperation,
        RecoveryState = RecoveryState,
    };
}
