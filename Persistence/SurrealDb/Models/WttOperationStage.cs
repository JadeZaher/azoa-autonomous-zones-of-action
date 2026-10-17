using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>One WTT stage generation: its exact quote, fill-once candidate, exposure, first-send authority and evidence.</summary>
[SurrealTable("wtt_operation_stage", Aggregate = "Wtt", Guardrail = "G6 SCHEMAFULL")]
[Index("wtt_operation_stage_owner", Fields = new[] { "operation_id", "created_at" })]
[Slice("wtt")]
public partial class WttOperationStage : ISurrealRecord
{
    public const string SchemaNameConst = "wtt_operation_stage";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string IntentHash { get; set; } = string.Empty;
    [Inside("Source", "Target", "Participant", "NativeOutput", "Cleanup")]
    public string Phase { get; set; } = "Source";
    public string Stage { get; set; } = string.Empty;
    public int Generation { get; set; }
    public string? PreviousStageId { get; set; }
    public string? OriginalStageId { get; set; }
    public string QuoteJson { get; set; } = string.Empty;
    public string QuoteHash { get; set; } = string.Empty;
    public string? ConsentJson { get; set; }
    public string? ParticipantAddress { get; set; }
    public string? ParticipantJson { get; set; }
    public string? PreparedJson { get; set; }
    public string? PreparedHash { get; set; }
    public string? ObservationJson { get; set; }
    [Inside("Planned", "Preparing", "Prepared", "Exposed", "SubmissionAuthorized", "Confirmed", "FailedOnChain", "ExpiredNoEffect",
        "NeedsOperator", "Cancelled", "Retired")]
    public string State { get; set; } = "Planned";
    public DateTimeOffset? ExposedAt { get; set; }
    public DateTimeOffset? AuthorizedAt { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
