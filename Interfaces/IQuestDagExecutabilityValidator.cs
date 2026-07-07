using AZOA.WebAPI.Models.Quest;

namespace AZOA.WebAPI.Interfaces;

/// <summary>
/// Publish-time gate that rejects a DAG whose <c>$from</c> binding inputs cannot
/// be satisfied at runtime (unreachable source, absent output field, or a provable
/// scalar type mismatch). See Services/Quest/AGENTS.md §executability-validation.
/// </summary>
public interface IQuestDagExecutabilityValidator
{
    /// <summary>
    /// Validates that every <c>$from</c> binding in the quest is executable.
    /// Reuses <see cref="DagValidationResult"/>'s error/warning shape; only
    /// <see cref="DagValidationResult.IsValid"/> and
    /// <see cref="DagValidationResult.Errors"/> are populated.
    /// </summary>
    DagValidationResult Validate(Quest quest);
}
