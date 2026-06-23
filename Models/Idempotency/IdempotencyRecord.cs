using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AZOA.WebAPI.Models.Idempotency;

/// <summary>
/// Lifecycle state of an idempotent operation.
/// </summary>
public enum IdempotencyState
{
    /// <summary>The claim has been won and the irreversible effect is being executed.</summary>
    InProgress,

    /// <summary>The irreversible effect completed successfully; <see cref="IdempotencyRecord.ResultPayload"/> holds the cached result.</summary>
    Completed,

    /// <summary>The irreversible effect failed; <see cref="IdempotencyRecord.Error"/> holds the failure reason.</summary>
    Failed
}

/// <summary>
/// Persisted idempotency record. Exactly-once execution of an irreversible
/// operation (bridge redeem, faucet dispense, server-side submit) is enforced
/// by a UNIQUE constraint on <see cref="Key"/> combined with insert-wins
/// semantics: the first caller to insert an <see cref="IdempotencyState.InProgress"/>
/// row "wins" the claim; concurrent inserts fail the unique constraint and
/// re-read the existing record.
/// </summary>
[Table("IdempotencyRecords")]
public class IdempotencyRecord
{
    /// <summary>
    /// The idempotency key. Caller-supplied (Idempotency-Key header) or a
    /// deterministic content hash. Primary key + unique constraint.
    /// </summary>
    [Key]
    [MaxLength(200)]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Logical operation type (e.g., "bridge_redeem", "faucet_dispense").
    /// Scopes/aids diagnostics; the uniqueness guarantee is on <see cref="Key"/> alone.
    /// </summary>
    [MaxLength(64)]
    public string OperationType { get; set; } = string.Empty;

    public IdempotencyState State { get; set; } = IdempotencyState.InProgress;

    /// <summary>
    /// Serialized result of the completed operation. Replayed verbatim to
    /// duplicate callers so they observe the same outcome as the original.
    /// </summary>
    [MaxLength(4096)]
    public string? ResultPayload { get; set; }

    /// <summary>
    /// Failure reason when <see cref="State"/> is <see cref="IdempotencyState.Failed"/>.
    /// </summary>
    [MaxLength(1024)]
    public string? Error { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
