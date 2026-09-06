using System;
using System.Collections.Generic;
using FluentAssertions;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Actions;
using Xunit;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Tests.Actions;

/// <summary>
/// Tests for the ADDED semantic transition-legality layer
/// (smart-gates-holon-state §8.2). It runs ALONGSIDE the structural Kahn validator
/// and rejects an authored DAG whose gated edges encode an ILLEGAL phase transition
/// (e.g. DRAFT -> IN_PROGRESS skipping FUNDED), while leaving non-transition edges
/// untouched.
/// </summary>
public class ActionTransitionValidatorTests
{
    private static ActionEntity ActionWithTransitionEdge(string? condition)
    {
        var a = new ActionNode { Id = Guid.NewGuid(), Name = "from" };
        var b = new ActionNode { Id = Guid.NewGuid(), Name = "to" };
        return new ActionEntity
        {
            Id = Guid.NewGuid(),
            Nodes = new List<ActionNode> { a, b },
            Edges = new List<ActionEdge>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    SourceNodeId = a.Id,
                    TargetNodeId = b.Id,
                    EdgeType = ActionEdgeType.Conditional,
                    Condition = condition,
                }
            }
        };
    }

    [Theory]
    [InlineData("phase:DRAFT->PUBLISHED")]
    [InlineData("phase:PUBLISHED->SEEKING_SUPPORT")]
    [InlineData("phase:SEEKING_SUPPORT->FUNDED")]
    [InlineData("phase:FUNDED->IN_PROGRESS")]
    [InlineData("phase:IN_PROGRESS->COMPLETED")]
    public void LegalTransition_Passes(string condition)
    {
        var validator = new ActionTransitionValidator();
        var result = validator.Validate(ActionWithTransitionEdge(condition));

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
    }

    [Fact]
    public void IllegalJump_DraftToInProgress_Rejected()
    {
        // The headline rule: skipping the funding phases is illegal.
        var validator = new ActionTransitionValidator();
        var result = validator.Validate(ActionWithTransitionEdge("phase:DRAFT->IN_PROGRESS"));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Should().Contain("illegal phase transition").And.Contain("DRAFT").And.Contain("IN_PROGRESS");
    }

    [Fact]
    public void TransitionFromTerminalPhase_Rejected()
    {
        // COMPLETED is terminal — it has no legal outgoing transition.
        var validator = new ActionTransitionValidator();
        var result = validator.Validate(ActionWithTransitionEdge("phase:COMPLETED->DRAFT"));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("COMPLETED");
    }

    [Fact]
    public void UnknownSourcePhase_Rejected_FailClosed()
    {
        var validator = new ActionTransitionValidator();
        var result = validator.Validate(ActionWithTransitionEdge("phase:BOGUS->PUBLISHED"));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("unknown phase 'BOGUS'");
    }

    [Fact]
    public void MalformedPhaseCondition_Rejected_FailClosed()
    {
        // A "phase:"-prefixed condition that is not a clean FROM->TO is asserting a
        // transition but cannot be proven legal — it must be rejected, never skipped.
        var validator = new ActionTransitionValidator();
        var result = validator.Validate(ActionWithTransitionEdge("phase:DRAFT_ONLY_NO_ARROW"));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void NonTransitionEdge_Ignored()
    {
        // An ordinary conditional/control edge that does NOT encode a phase
        // transition is left entirely to structural validation — this layer passes it.
        var validator = new ActionTransitionValidator();

        validator.Validate(ActionWithTransitionEdge("upstream.bal.amount > 100"))
            .IsValid.Should().BeTrue();
        validator.Validate(ActionWithTransitionEdge(null))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void ArrowUnicodeVariant_Accepted()
    {
        // Both ASCII '->' and the unicode '→' separate FROM and TO.
        var validator = new ActionTransitionValidator();
        validator.Validate(ActionWithTransitionEdge("phase:DRAFT→PUBLISHED"))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void CaseInsensitivePhaseNames()
    {
        var validator = new ActionTransitionValidator();
        validator.Validate(ActionWithTransitionEdge("phase:draft->published"))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void CustomLegalMap_IsHonored()
    {
        // The legality is configurable: a different lifecycle map changes what is legal.
        var custom = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["OPEN"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CLOSED" },
            ["CLOSED"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        };
        var validator = new ActionTransitionValidator(custom);

        validator.Validate(ActionWithTransitionEdge("phase:OPEN->CLOSED")).IsValid.Should().BeTrue();
        validator.Validate(ActionWithTransitionEdge("phase:CLOSED->OPEN")).IsValid.Should().BeFalse();
        // A phase from the DEFAULT map is unknown under the custom map ⇒ rejected.
        validator.Validate(ActionWithTransitionEdge("phase:DRAFT->PUBLISHED")).IsValid.Should().BeFalse();
    }
}
