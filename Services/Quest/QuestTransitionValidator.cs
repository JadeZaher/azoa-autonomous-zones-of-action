using AZOA.WebAPI.Interfaces;
using QuestEntity = AZOA.WebAPI.Models.Quest.Quest;

namespace AZOA.WebAPI.Services.Quest;

/// <summary>
/// Semantic, transition-legality validation layer for a quest DAG
/// (smart-gates-holon-state §8.2). It is ADDED ALONGSIDE the structural Kahn
/// validation in <see cref="QuestDagValidator"/> — it does not replace or modify it.
/// Where the structural validator proves the graph is a well-formed,
/// entry/terminal-bounded, acyclic, reachable DAG, this validator proves that any
/// gated edge encoding a <i>phase transition</i> encodes a LEGAL one.
///
/// <para><b>Transition encoding.</b> A gated edge declares a phase transition via its
/// <see cref="AZOA.WebAPI.Models.Quest.QuestEdge.Condition"/> using the closed
/// convention <c>phase:FROM-&gt;TO</c> (e.g. <c>phase:DRAFT-&gt;PUBLISHED</c>). Edges
/// whose condition does not start with the <see cref="PhasePrefix"/> are NOT phase
/// transitions and are ignored here (they are still subject to structural
/// validation). This keeps the feature additive: a DAG that uses no phase-transition
/// edges passes this validator trivially.</para>
///
/// <para><b>Legal-transition map.</b> The legality is a configurable map from a
/// source phase to the set of phases it may legally transition INTO. The default
/// (<see cref="ProjectLifecycle"/>) seeds the project lifecycle
/// <c>DRAFT → PUBLISHED → SEEKING_SUPPORT → FUNDED → IN_PROGRESS → COMPLETED</c>, so
/// an illegal jump (e.g. <c>DRAFT → IN_PROGRESS</c> skipping FUNDED) is rejected. A
/// different lifecycle is supplied by constructing with a different map — the
/// validator derives no domain meaning, it only enforces the supplied legality.</para>
/// </summary>
public sealed class QuestTransitionValidator
{
    /// <summary>The condition prefix that marks an edge as a phase transition.</summary>
    public const string PhasePrefix = "phase:";

    private static readonly string[] TransitionSeparators = { "->", "→" };

    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> _legalTransitions;

    /// <summary>
    /// Construct with a custom legal-transition map (from-phase → the set of phases it
    /// may legally transition into). Phase names are compared case-insensitively.
    /// </summary>
    public QuestTransitionValidator(IReadOnlyDictionary<string, IReadOnlySet<string>> legalTransitions)
    {
        // Normalize keys/values to a case-insensitive comparison so DRAFT == draft.
        var normalized = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, tos) in legalTransitions)
            normalized[from] = new HashSet<string>(tos, StringComparer.OrdinalIgnoreCase);
        _legalTransitions = normalized;
    }

    /// <summary>Construct with the default project-lifecycle map (<see cref="ProjectLifecycle"/>).</summary>
    public QuestTransitionValidator() : this(ProjectLifecycle) { }

    /// <summary>
    /// The seed legal-transition map for the project lifecycle
    /// <c>DRAFT → PUBLISHED → SEEKING_SUPPORT → FUNDED → IN_PROGRESS → COMPLETED</c>.
    /// Each phase may transition only to its immediate successor; any other jump (e.g.
    /// <c>DRAFT → IN_PROGRESS</c>) is illegal. <c>COMPLETED</c> is terminal (no
    /// outgoing transitions).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> ProjectLifecycle { get; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["DRAFT"] = Set("PUBLISHED"),
            ["PUBLISHED"] = Set("SEEKING_SUPPORT"),
            ["SEEKING_SUPPORT"] = Set("FUNDED"),
            ["FUNDED"] = Set("IN_PROGRESS"),
            ["IN_PROGRESS"] = Set("COMPLETED"),
            ["COMPLETED"] = Set(),
        };

    private static IReadOnlySet<string> Set(params string[] values) =>
        new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Validate that every phase-transition edge in the quest encodes a LEGAL
    /// transition. Edges that do not encode a phase transition are ignored.
    /// Reuses <see cref="DagValidationResult"/> so a caller can surface transition
    /// errors the same way it surfaces structural ones. <c>IsValid == true</c> when
    /// every phase-transition edge is legal (including the trivial no-transition case).
    /// </summary>
    public DagValidationResult Validate(QuestEntity quest)
    {
        var result = new DagValidationResult { IsValid = true };

        foreach (var edge in quest.Edges)
        {
            if (!TryParseTransition(edge.Condition, out var from, out var to))
                continue; // not a phase-transition edge — structural validation owns it

            // An unknown source phase cannot be proven legal — reject closed.
            if (!_legalTransitions.TryGetValue(from, out var allowed))
            {
                result.Errors.Add(
                    $"Edge {edge.Id}: transition from unknown phase '{from}' is not permitted " +
                    $"(no legal transitions defined for it).");
                result.IsValid = false;
                continue;
            }

            if (!allowed.Contains(to))
            {
                var legal = allowed.Count == 0 ? "(none — terminal phase)" : string.Join(", ", allowed);
                result.Errors.Add(
                    $"Edge {edge.Id}: illegal phase transition '{from}' -> '{to}'. " +
                    $"Legal transitions from '{from}': {legal}.");
                result.IsValid = false;
            }
        }

        return result;
    }

    /// <summary>
    /// Parse a <c>phase:FROM-&gt;TO</c> edge condition. Returns false (not a transition)
    /// for a null/empty condition or one that does not start with <see cref="PhasePrefix"/>.
    /// A malformed phase condition (prefix present but no <c>-&gt;</c>, or an empty side)
    /// is reported by the caller as an error via the unknown-phase path — here it simply
    /// fails to parse cleanly, so we treat a prefixed-but-malformed condition as a
    /// transition with an empty/unknown phase to force a fail-closed error rather than a
    /// silent skip.
    /// </summary>
    private static bool TryParseTransition(string? condition, out string from, out string to)
    {
        from = string.Empty;
        to = string.Empty;
        if (string.IsNullOrWhiteSpace(condition)) return false;

        var trimmed = condition.Trim();
        if (!trimmed.StartsWith(PhasePrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var body = trimmed.Substring(PhasePrefix.Length);
        var parts = body.Split(TransitionSeparators, StringSplitOptions.None);

        // A prefixed condition is asserting "this is a phase transition"; if it is not a
        // clean FROM->TO it stays a transition (returns true) but with empty phases so
        // the validator rejects it (fail-closed), never silently ignores a malformed
        // transition declaration.
        if (parts.Length != 2)
        {
            from = body.Trim();
            to = string.Empty;
            return true;
        }

        from = parts[0].Trim();
        to = parts[1].Trim();
        return true;
    }
}
