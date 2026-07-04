using FluentValidation;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

/// <summary>
/// Validates the post-hoc edge-add body (Guid-reference variant). See spec FR-1c / AC-1b.
/// </summary>
public class QuestEdgeAddModelValidator : AbstractValidator<QuestEdgeAddModel>
{
    public QuestEdgeAddModelValidator()
    {
        RuleFor(x => x.SourceNodeId).NotEmpty().WithMessage("SourceNodeId is required.");
        RuleFor(x => x.TargetNodeId).NotEmpty().WithMessage("TargetNodeId is required.");

        RuleFor(x => x)
            .Must(e => e.SourceNodeId != e.TargetNodeId)
            .WithMessage("SourceNodeId and TargetNodeId must not be the same node (self-loops not allowed).");

        RuleFor(x => x.EdgeType)
            .IsInEnum().WithMessage("EdgeType is not a valid QuestEdgeType value.");

        // FR-1b: Conditional edges must carry non-empty Condition text.
        When(x => x.EdgeType == QuestEdgeType.Conditional, () =>
        {
            RuleFor(x => x.Condition)
                .NotEmpty().WithMessage("Condition is required for Conditional edges.")
                .MaximumLength(4096).WithMessage("Condition must not exceed 4096 characters.");
        });

        // AC-2f: OnFailure edges must not carry Condition text.
        When(x => x.EdgeType == QuestEdgeType.OnFailure, () =>
        {
            RuleFor(x => x.Condition)
                .Null().WithMessage("OnFailure edges must not carry a Condition expression.");
        });

        When(x => x.EdgeType != QuestEdgeType.Conditional
                  && x.EdgeType != QuestEdgeType.OnFailure
                  && x.Condition != null, () =>
        {
            RuleFor(x => x.Condition)
                .MaximumLength(4096).WithMessage("Condition must not exceed 4096 characters.");
        });
    }
}
