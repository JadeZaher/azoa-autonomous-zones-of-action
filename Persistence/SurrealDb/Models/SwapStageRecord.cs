using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>Immutable swap plan and canonical signed candidate, separately authorized for egress.</summary>
[SurrealTable("swap_stage", Aggregate = "Swap", Guardrail = "G6 SCHEMAFULL")]
[Index("swap_stage_identity", Fields = new[] { "operation_id", "stage", "generation" }, Unique = true)]
[Slice("swap")]
public partial class SwapStageRecord : ISurrealRecord
{
    public const string SchemaNameConst = "swap_stage";
    public string SchemaName => SchemaNameConst;
    [Id] public string Id { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string IntentHash { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public int Generation { get; set; }
    public string PreparationKey { get; set; } = string.Empty;
    public string? PreviousStageId { get; set; }
    public string PlanHash { get; set; } = string.Empty;
    public string PlanJson { get; set; } = string.Empty;
    public string? PreparedHash { get; set; }
    public string? PreparedJson { get; set; }
    public string State { get; set; } = "Planned";
    public string? ObservationJson { get; set; }
    public string? ResolvedLockedBalance { get; set; }
    public string? RentAccountingEvidenceJson { get; set; }
    public string? RentReleaseCleanupStageId { get; set; }
    public string? RentReleaseScanJson { get; set; }
    public string? ReleasedLockedBalance { get; set; }
    public string? RentReleaseEvidenceJson { get; set; }
    public DateTimeOffset? AuthorizedAt { get; set; }
    public DateTimeOffset? ServerSubmissionWithdrawnAt { get; set; }
    public DateTimeOffset? ExternalExposedAt { get; set; }
    public string? ExternalExposureHash { get; set; }
    public string? ExternalTransactionIdsJson { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
