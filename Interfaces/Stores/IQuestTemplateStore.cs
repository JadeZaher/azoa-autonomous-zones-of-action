using AZOA.WebAPI.Models.Quest;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// Per-aggregate storage seam for definition-side quest catalog data
/// (<see cref="QuestTemplate"/> + <see cref="QuestNodeTemplate"/>).
///
/// Scope: read-only catalog lookups used by
/// <c>Services/Quest/QuestInstantiator.cs</c>. Write paths (template authoring,
/// versioning, marketplace publish) are out of scope here — the wave-2 close-out
/// only needs the read seam so the EF deletion (Stream D) can proceed.
///
/// This interface is INTENTIONALLY narrow: it exposes only the two lookups the
/// instantiator performs (template by id, node-template by id). Adding broader
/// query methods (list-by-author, search-by-tag, etc.) belongs to the future
/// template-marketplace track, not to wave-3.
///
/// Boundary with <c>quest-temporal-fork-model</c>: that track owns the runtime
/// quest_run + quest_node_execution tables (definition-vs-runtime split per
/// ADR §2.2). The template tables this interface covers are definition-side
/// and unaffected by the fork model — no scope collision.
/// </summary>
public interface IQuestTemplateStore
{
    /// <summary>
    /// Fetch a template by id, including its embedded node + edge slot
    /// definitions. Returns <c>null</c> when the template does not exist.
    ///
    /// The returned <see cref="QuestTemplate"/> has its <c>Nodes</c> and
    /// <c>Edges</c> collections fully populated from the same record-id read
    /// (no separate query round-trip).
    /// </summary>
    Task<QuestTemplate?> GetTemplateAsync(Guid templateId, CancellationToken ct);

    /// <summary>
    /// Fetch a reusable node-template definition by id. Returns <c>null</c>
    /// when no row exists — the instantiator surfaces this as
    /// "QuestNodeTemplate {id} (slot: {slot}) not found." so author
    /// dangling-reference bugs are visible at instantiation time.
    /// </summary>
    Task<QuestNodeTemplate?> GetNodeTemplateAsync(Guid nodeTemplateId, CancellationToken ct);
}
