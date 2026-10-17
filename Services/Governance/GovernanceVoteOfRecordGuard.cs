// SPDX-License-Identifier: UNLICENSED

using System.Reflection;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Actions.Handlers;

namespace AZOA.WebAPI.Services.Governance;
/// <summary>Checks that governance finalization uses the atomic ledger store rather than ballot storage.</summary>
public static class GovernanceVoteOfRecordGuard
{
    public static readonly Type RequiredTallyDependency = typeof(SurrealGovernanceStore);
    public static readonly IReadOnlyList<Type> ForbiddenTallyDependencies = new[] { typeof(IHolonStore) };
    private static readonly Lazy<IReadOnlyList<string>> CachedViolations =
        new(() => Violations(typeof(GovernanceTallyNodeHandler)));
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
