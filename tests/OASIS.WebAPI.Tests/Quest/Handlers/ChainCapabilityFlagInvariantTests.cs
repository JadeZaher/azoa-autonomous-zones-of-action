using System;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Moq;
using OASIS.WebAPI.Interfaces.QuestExecution;
using OASIS.WebAPI.Models.Quest;
using OASIS.WebAPI.Services.Quest;
using OASIS.WebAPI.Services.Quest.Handlers;
using Xunit;

namespace OASIS.WebAPI.Tests.Quest.Handlers;

/// <summary>
/// T11 — the tier-flag invariant for the WHOLE handler set (economic-primitive-nodes D1).
///
/// <para>Drives the assertion off the <b>actually registered</b> handler types
/// (the same namespace scan <c>Program.cs</c> uses to register them), not a
/// hand-maintained list that would silently drift. For every concrete
/// <see cref="IQuestNodeHandler"/> it asserts
/// <see cref="IQuestNodeHandler.RequiresChainCapability"/> equals "is this a
/// Tier-2 node?". This single invariant catches BOTH failure modes at once: a
/// Tier-1 handler that leaked <c>true</c>, and a Tier-2 handler that forgot to
/// override the SPI default to <c>true</c>.</para>
/// </summary>
public class ChainCapabilityFlagInvariantTests
{
    /// <summary>
    /// The four — and only four — Tier-2 chain-action node types. Every other
    /// node type is Tier-1 (chain-free). This set is the single source of truth
    /// the invariant is checked against.
    /// </summary>
    private static readonly QuestNodeType[] Tier2 =
    {
        QuestNodeType.Swap,
        QuestNodeType.Grant,
        QuestNodeType.Transfer,
        QuestNodeType.Refund,
    };

    /// <summary>
    /// The concrete handler types, discovered with the SAME scan
    /// <c>Program.cs</c> uses: every non-abstract class in the
    /// <c>OASIS.WebAPI.Services.Quest.Handlers</c> namespace that implements
    /// <see cref="IQuestNodeHandler"/>.
    /// </summary>
    public static TheoryData<Type> HandlerTypes()
    {
        var data = new TheoryData<Type>();
        foreach (var t in DiscoverHandlerTypes())
            data.Add(t);
        return data;
    }

    [Theory]
    [MemberData(nameof(HandlerTypes))]
    public void RequiresChainCapability_MatchesTierMembership(Type handlerType)
    {
        var handler = Instantiate(handlerType);

        var isTier2 = Tier2.Contains(handler.NodeType);
        handler.RequiresChainCapability.Should().Be(
            isTier2,
            $"{handlerType.Name} ({handler.NodeType}) must report RequiresChainCapability == " +
            $"{isTier2}: Tier-2 chain actions (Swap/Grant/Transfer/Refund) require the " +
            "capability; every other (Tier-1) handler must be chain-free.");
    }

    [Fact]
    public void EntireTier1SetIsChainFree_AndEntireTier2SetRequiresCapability()
    {
        var handlers = DiscoverHandlerTypes().Select(Instantiate).ToList();

        // The scan must actually find handlers (guards a broken discovery path
        // from silently passing with an empty set).
        handlers.Should().HaveCountGreaterThan(Tier2.Length,
            "the namespace scan must discover the full handler set, not just Tier-2");

        var tier2Found = handlers.Where(h => h.RequiresChainCapability)
            .Select(h => h.NodeType).OrderBy(t => t).ToList();
        var tier1Found = handlers.Where(h => !h.RequiresChainCapability)
            .Select(h => h.NodeType).ToList();

        // Every capability-requiring handler is exactly one of the four Tier-2 types.
        tier2Found.Should().BeEquivalentTo(Tier2,
            "exactly the four Tier-2 chain actions may require a chain capability");

        // No Tier-1 handler leaked a chain-capability requirement.
        tier1Found.Should().NotContain(Tier2,
            "no Tier-1 handler may require a chain capability");
    }

    // ── discovery + instantiation ───────────────────────────────────────────

    private static System.Collections.Generic.IEnumerable<Type> DiscoverHandlerTypes() =>
        typeof(QuestNodeHandlerRegistry).Assembly
            .GetTypes()
            .Where(t => t.Namespace == "OASIS.WebAPI.Services.Quest.Handlers"
                        && typeof(IQuestNodeHandler).IsAssignableFrom(t)
                        && t is { IsClass: true, IsAbstract: false })
            .OrderBy(t => t.Name);

    /// <summary>
    /// Instantiates a handler by filling every constructor parameter with a Moq
    /// mock (all handler ctor deps are interfaces). The tier flag and node type
    /// are pure declarations, so the mocked collaborators are never invoked.
    /// </summary>
    private static IQuestNodeHandler Instantiate(Type handlerType)
    {
        var ctor = handlerType.GetConstructors()
            .OrderBy(c => c.GetParameters().Length)
            .First();

        var args = ctor.GetParameters()
            .Select(p => MockFor(p.ParameterType))
            .ToArray();

        return (IQuestNodeHandler)ctor.Invoke(args);
    }

    private static object MockFor(Type type)
    {
        // All handler constructor dependencies are interfaces ⇒ Mock.Of<T>()
        // via the open generic. Defensive fallback for any non-interface param.
        if (type.IsInterface)
        {
            var mockOf = typeof(Mock)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == nameof(Mock.Of) && m.GetParameters().Length == 0);
            return mockOf.MakeGenericMethod(type).Invoke(null, null)!;
        }

        return Activator.CreateInstance(type)
               ?? throw new InvalidOperationException($"cannot construct ctor arg of type {type}");
    }
}
