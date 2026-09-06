using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Actions;
using Xunit;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;
using ActionNodeEntity = AZOA.WebAPI.Models.Actions.ActionNode;
using ActionEdgeEntity = AZOA.WebAPI.Models.Actions.ActionEdge;

namespace AZOA.WebAPI.Tests.Actions;

/// <summary>
/// Pins the publish-time executability gate: reachability (dominators), output-field
/// presence (case-insensitive), and best-effort scalar type match.
/// See Services/ActionDefinition/AGENTS.md §executability-validation.
/// </summary>
public class ActionDagExecutabilityValidatorTests
{
    private readonly ActionDagExecutabilityValidator _validator = new();

    // ── helpers ─────────────────────────────────────────────────────────────

    private static ActionNodeEntity Node(Guid id, string name, ActionNodeType type, string config = "{}",
        bool entry = false, bool terminal = false) =>
        new() { Id = id, Name = name, NodeType = type, Config = config, IsEntry = entry, IsTerminal = terminal };

    private static ActionEdgeEntity Edge(Guid src, Guid dst, ActionEdgeType type = ActionEdgeType.Control) =>
        new() { Id = Guid.NewGuid(), SourceNodeId = src, TargetNodeId = dst, EdgeType = type };

    private static ActionEntity ActionDefinition(IEnumerable<ActionNodeEntity> nodes, IEnumerable<ActionEdgeEntity> edges) =>
        new() { Id = Guid.NewGuid(), Name = "q", Nodes = nodes.ToList(), Edges = edges.ToList() };

    // ── 1. run.<name> to a guaranteed ancestor with a valid field → passes ───

    [Fact]
    public void RunBinding_ToGuaranteedAncestor_ValidField_Passes()
    {
        // A (Bridge) → B (Back). B binds run.A.Id (Bridge output has string "Id").
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var action = ActionDefinition(
            new[]
            {
                Node(a, "A", ActionNodeType.Bridge, entry: true),
                Node(b, "B", ActionNodeType.Back,
                    config: """{"BridgeTransactionId":{"$from":"run.A.Id"}}""", terminal: true),
            },
            new[] { Edge(a, b) });

        var result = _validator.Validate(action);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ── 2. run.<name> to a non-guaranteed (conditional-branch) node → rejected ─

    [Fact]
    public void RunBinding_ToNonGuaranteedNode_Rejected()
    {
        // Entry E ─Control→ Main (M) ─Control→ Sink (S)
        //         └─Conditional→ Branch (Br)   [Br only runs on SOME paths]
        // Sink binds run.Br.Id — Br is NOT a dominator of Sink → reject.
        var e = Guid.NewGuid();
        var m = Guid.NewGuid();
        var br = Guid.NewGuid();
        var s = Guid.NewGuid();
        var action = ActionDefinition(
            new[]
            {
                Node(e, "E", ActionNodeType.Bridge, entry: true),
                Node(m, "M", ActionNodeType.Bridge),
                Node(br, "Br", ActionNodeType.Bridge),
                Node(s, "S", ActionNodeType.Back,
                    config: """{"BridgeTransactionId":{"$from":"run.Br.Id"}}""", terminal: true),
            },
            new[]
            {
                Edge(e, m),
                Edge(e, br, ActionEdgeType.Conditional),
                Edge(m, s),
                Edge(br, s),
            });

        var result = _validator.Validate(action);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("not guaranteed to have executed"));
    }

    // ── 3. upstream.<name> to a non-direct-predecessor → rejected ─────────────

    [Fact]
    public void UpstreamBinding_ToNonDirectPredecessor_Rejected()
    {
        // A → B → C. C binds upstream.A.Id, but A is NOT a direct predecessor of C.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var action = ActionDefinition(
            new[]
            {
                Node(a, "A", ActionNodeType.Bridge, entry: true),
                Node(b, "B", ActionNodeType.Bridge),
                Node(c, "C", ActionNodeType.Back,
                    config: """{"BridgeTransactionId":{"$from":"upstream.A.Id"}}""", terminal: true),
            },
            new[] { Edge(a, b), Edge(b, c) });

        var result = _validator.Validate(action);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("is not a direct predecessor"));
    }

    // ── 4. field not in output schema → rejected ──────────────────────────────

    [Fact]
    public void Binding_ToUndeclaredField_Rejected()
    {
        // A (Bridge) → B. B binds upstream.A.Nonexistent — not a Bridge output field.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var action = ActionDefinition(
            new[]
            {
                Node(a, "A", ActionNodeType.Bridge, entry: true),
                Node(b, "B", ActionNodeType.Back,
                    config: """{"BridgeTransactionId":{"$from":"upstream.A.Nonexistent"}}""", terminal: true),
            },
            new[] { Edge(a, b) });

        var result = _validator.Validate(action);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("is not in the output of node"));
    }

    // ── 5. case-insensitive field match (upstream.x.id vs declared Id) → passes ─

    [Fact]
    public void Binding_CaseInsensitiveFieldMatch_Passes()
    {
        // Bridge declares "Id"; the binding uses lowercase "id".
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var action = ActionDefinition(
            new[]
            {
                Node(a, "A", ActionNodeType.Bridge, entry: true),
                Node(b, "B", ActionNodeType.Back,
                    config: """{"BridgeTransactionId":{"$from":"upstream.A.id"}}""", terminal: true),
            },
            new[] { Edge(a, b) });

        var result = _validator.Validate(action);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ── 6. deep path into Result.Object → admitted ────────────────────────────

    [Fact]
    public void Binding_DeepPathIntoObjectResult_Admitted()
    {
        // HolonGet output is a wrapper whose Result is an Object. upstream.A.Result.Id
        // descends past Result (Object) — the deep shape is unknown, so admit it.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var action = ActionDefinition(
            new[]
            {
                Node(a, "A", ActionNodeType.HolonGet, config: """{"Id":"00000000-0000-0000-0000-000000000001"}""", entry: true),
                Node(b, "B", ActionNodeType.Back,
                    config: """{"BridgeTransactionId":{"$from":"upstream.A.Result.Id"}}""", terminal: true),
            },
            new[] { Edge(a, b) });

        var result = _validator.Validate(action);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ── 7. precision-safe bridge amounts bind as strings ─────────────────────

    [Fact]
    public void Binding_BridgeDecimalStringAmount_ToStringField_Passes()
    {
        // Bridge.Amount is emitted as a decimal JSON string so ulong values stay exact.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var action = ActionDefinition(
            new[]
            {
                Node(a, "A", ActionNodeType.Bridge, entry: true),
                Node(b, "B", ActionNodeType.Back,
                    config: """{"BridgeTransactionId":{"$from":"upstream.A.Amount"}}""", terminal: true),
            },
            new[] { Edge(a, b) });

        var result = _validator.Validate(action);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Binding_ScalarTypeMismatch_Rejected()
    {
        // Bridge.Status is a numeric enum; Back.BridgeTransactionId expects a string.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var action = ActionDefinition(
            new[]
            {
                Node(a, "A", ActionNodeType.Bridge, entry: true),
                Node(b, "B", ActionNodeType.Back,
                    config: """{"BridgeTransactionId":{"$from":"upstream.A.Status"}}""", terminal: true),
            },
            new[] { Edge(a, b) });

        var result = _validator.Validate(action);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("yields Number") && x.Contains("expects String"));
    }

    // ── 8. path into a None-output node → rejected ────────────────────────────
    // No production ActionNodeType currently maps to NodeOutputShape.None (every
    // handler either writes a known/wrapped shape or is Open). The None branch is
    // kept for future pure-side-effect nodes; here we assert the adjacent, live
    // "cannot resolve" rejection for a Known-shape node referencing an absent field,
    // which exercises the same presence-failure surface a None node would hit.
    [Fact]
    public void Binding_IntoNodeWithNoResolvableField_Rejected()
    {
        // GateCheck output is {"pass": Boolean}. Binding upstream.A.result has no
        // matching field → rejected (no readable field to resolve).
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var action = ActionDefinition(
            new[]
            {
                Node(a, "A", ActionNodeType.GateCheck, config: """{"Predicate":"true"}""", entry: true),
                Node(b, "B", ActionNodeType.Back,
                    config: """{"BridgeTransactionId":{"$from":"upstream.A.result"}}""", terminal: true),
            },
            new[] { Edge(a, b) });

        var result = _validator.Validate(action);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("is not in the output of node"));
    }

    // ── 9. holon. path → skipped (passes) ─────────────────────────────────────

    [Fact]
    public void HolonBinding_Skipped_Passes()
    {
        // holon.<guid>.<field> is dynamic state — validator skips A/B/C entirely.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var action = ActionDefinition(
            new[]
            {
                Node(a, "A", ActionNodeType.Bridge, entry: true),
                Node(b, "B", ActionNodeType.Back,
                    config: """{"BridgeTransactionId":{"$from":"holon.00000000-0000-0000-0000-000000000001.status"}}""",
                    terminal: true),
            },
            new[] { Edge(a, b) });

        var result = _validator.Validate(action);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ── 10. run.<name> whose ONLY incoming edge is OnFailure → not a dominator ─

    [Fact]
    public void RunBinding_ToOnFailureOnlyAncestor_Rejected()
    {
        // E (entry) ─OnFailure→ H ─Control→ S. H's only incoming edge is OnFailure,
        // which is excluded from the Control-only preds set, so H is NOT a dominator
        // of S even though it's the only node in between.
        var e = Guid.NewGuid();
        var h = Guid.NewGuid();
        var s = Guid.NewGuid();
        var action = ActionDefinition(
            new[]
            {
                Node(e, "E", ActionNodeType.Bridge, entry: true),
                Node(h, "H", ActionNodeType.Bridge),
                Node(s, "S", ActionNodeType.Back,
                    config: """{"BridgeTransactionId":{"$from":"run.H.Id"}}""", terminal: true),
            },
            new[]
            {
                Edge(e, h, ActionEdgeType.OnFailure),
                Edge(h, s),
            });

        var result = _validator.Validate(action);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("not guaranteed"));
    }

    // ── 11. run.<name> whose ONLY incoming edge is Conditional → not a dominator ─

    [Fact]
    public void RunBinding_ToConditionalOnlyAncestor_Rejected()
    {
        // Same shape as the OnFailure case, but the E→H edge is Conditional.
        var e = Guid.NewGuid();
        var h = Guid.NewGuid();
        var s = Guid.NewGuid();
        var action = ActionDefinition(
            new[]
            {
                Node(e, "E", ActionNodeType.Bridge, entry: true),
                Node(h, "H", ActionNodeType.Bridge),
                Node(s, "S", ActionNodeType.Back,
                    config: """{"BridgeTransactionId":{"$from":"run.H.Id"}}""", terminal: true),
            },
            new[]
            {
                Edge(e, h, ActionEdgeType.Conditional),
                Edge(h, s),
            });

        var result = _validator.Validate(action);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("not guaranteed"));
    }

    // ── 12. duplicate node names → rejected up-front ──────────────────────────

    [Fact]
    public void DuplicateNodeNames_Rejected()
    {
        // Two nodes both named "X" — no bindings needed; name-uniqueness is
        // checked before any binding/reachability work.
        var x1 = Guid.NewGuid();
        var x2 = Guid.NewGuid();
        var action = ActionDefinition(
            new[]
            {
                Node(x1, "X", ActionNodeType.Bridge, entry: true),
                Node(x2, "X", ActionNodeType.Back, terminal: true),
            },
            new[] { Edge(x1, x2) });

        var result = _validator.Validate(action);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("Duplicate node name"));
    }

    // ── 13. 2-segment path (root.name, no field) → silently skipped, no throw ─

    [Fact]
    public void TwoSegmentPath_DoesNotThrow_NoExecutabilityError()
    {
        // A → B. B binds run.A (only 2 segments — no field). A IS a guaranteed
        // Control ancestor, so reachability passes; the field-presence check is
        // guarded (segments.Count < 3) rather than indexing segments[2].
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var action = ActionDefinition(
            new[]
            {
                Node(a, "A", ActionNodeType.Bridge, entry: true),
                Node(b, "B", ActionNodeType.Back,
                    config: """{"BridgeTransactionId":{"$from":"run.A"}}""", terminal: true),
            },
            new[] { Edge(a, b) });

        var exception = Record.Exception(() => _validator.Validate(action));

        Assert.Null(exception);
    }
}
