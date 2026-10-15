// SPDX-License-Identifier: UNLICENSED

using System.Reflection;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Services.Actions.Handlers;

namespace AZOA.WebAPI.Services.Governance;

/// <summary>
/// Refuses to let the governance tally run unless it still reads the claim ledger and
/// still reads no ballot store (D39, AC2).
/// </summary>
/// <remarks>
/// <para>
/// Every defect this track corrects came from a control that existed and went unread,
/// so the guard is aimed at the READING. It is checked in
/// <c>GovernanceTallyNodeHandler</c>'s own constructor, which means a tally whose
/// ledger dependency has been removed does not publish a wrong count — it does not
/// construct at all, and every test that runs a tally goes red with the reason in the
/// message.
/// </para>
/// <para>
/// <b>What it catches:</b> deleting the claim-ledger dependency from the tally;
/// swapping it for something else; and reintroducing a ballot read by giving the
/// tally an <see cref="IHolonStore"/> (the unscoped holon read the retired
/// <c>GovernanceBallotRoll</c> owned) or a counter that holds one.
/// <b>What it does not catch:</b> a body that keeps the dependency and stops calling
/// it — that case is covered by the attack tests in
/// <c>tests/AZOA.WebAPI.Tests/Governance</c>, which read the published counts and go
/// red the moment the tally stops reflecting the claim rows. The two together are the
/// guard; neither alone is.
/// </para>
/// <para>
/// The proposal itself is still read through the consumer-scoped
/// <c>IHolonManager</c>, and that is deliberate: the proposal is the QUESTION (its
/// declared options, its close time, and whether this caller may see it at all). The
/// ANSWER comes only from the ledger. <see cref="ForbiddenTallyDependencies"/>
/// therefore names the unscoped ballot-reading surfaces, not the manager.
/// </para>
/// </remarks>
public static class GovernanceVoteOfRecordGuard
{
    /// <summary>The dependency the tally must keep: without it there is no vote of record to read.</summary>
    public static readonly Type RequiredTallyDependency = typeof(IPersonOperationClaimStore);

    /// <summary>
    /// Surfaces that can read ballot holons unscoped. A tally that holds one of these
    /// has had the forgeable path handed back to it.
    /// </summary>
    public static readonly IReadOnlyList<Type> ForbiddenTallyDependencies = new[] { typeof(IHolonStore) };

    // typeof, not a resolved type name: a guard that looks its subject up by string
    // is a guard that goes quietly inert when the subject is renamed.
    private static readonly Lazy<IReadOnlyList<string>> CachedViolations =
        new(() => Violations(typeof(GovernanceTallyNodeHandler)));

    /// <summary>
    /// Throws when the tally no longer reads the claim ledger, or has regained a way
    /// to read ballots. Cheap after the first call — the answer is a property of the
    /// compiled types, so it is computed once per process.
    /// </summary>
    public static void ThrowIfTallyIsNotLedgerOnly()
    {
        var violations = CachedViolations.Value;
        if (violations.Count == 0)
            return;

        throw new InvalidOperationException(
            "The governance tally is no longer the vote of record (D39): "
            + string.Join(" ", violations)
            + " See Services/Governance/AGENTS.md §consumer-governance.");
    }

    /// <summary>
    /// The reasons <paramref name="tallyHandlerType"/> is not a ledger-only tally;
    /// empty when it is. Public so an architecture test can name the same rule
    /// without reaching into the handler.
    /// </summary>
    public static IReadOnlyList<string> Violations(Type tallyHandlerType)
    {
        ArgumentNullException.ThrowIfNull(tallyHandlerType);

        var reachable = ReachableDependencyTypes(tallyHandlerType);
        var violations = new List<string>();

        if (!reachable.Any(RequiredTallyDependency.IsAssignableFrom))
            violations.Add(
                $"it no longer depends on {RequiredTallyDependency.Name}, so nothing it reads is the claim ledger.");

        foreach (var forbidden in ForbiddenTallyDependencies)
        {
            if (reachable.Any(forbidden.IsAssignableFrom))
                violations.Add(
                    $"it depends on {forbidden.Name}, which reads ballot holons — the forgeable path D39 retired.");
        }

        return violations;
    }

    /// <summary>
    /// The handler's own constructor parameters and fields, plus the same for any
    /// governance collaborator it holds. One hop is enough and is what makes the rule
    /// honest: hiding <see cref="IHolonStore"/> behind a helper the tally constructs
    /// (exactly how <c>GovernanceBallotRoll</c> was reached) is still a violation.
    /// </summary>
    private static IReadOnlyList<Type> ReachableDependencyTypes(Type root)
    {
        var direct = DeclaredDependencyTypes(root);
        var reachable = new List<Type>(direct);

        foreach (var collaborator in direct.Where(t => t.Namespace == typeof(GovernanceVoteOfRecordGuard).Namespace))
            reachable.AddRange(DeclaredDependencyTypes(collaborator));

        return reachable;
    }

    private static IReadOnlyList<Type> DeclaredDependencyTypes(Type type)
    {
        var fromConstructors = type
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType);

        var fromFields = type
            .GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            .Select(field => field.FieldType);

        return fromConstructors.Concat(fromFields).Distinct().ToList();
    }
}
