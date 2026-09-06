using FluentValidation;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

/// <summary>
/// Validates the post-hoc edge-add body (Guid-reference variant). See spec FR-1c / AC-1b.
/// </summary>
public class ActionEdgeAddModelValidator : AbstractValidator<ActionEdgeAddModel>
{
    public ActionEdgeAddModelValidator()
    {
        RuleFor(x => x.SourceNodeId).NotEmpty().WithMessage("SourceNodeId is required.");
        RuleFor(x => x.TargetNodeId).NotEmpty().WithMessage("TargetNodeId is required.");

        RuleFor(x => x)
            .Must(e => e.SourceNodeId != e.TargetNodeId)
            .WithMessage("SourceNodeId and TargetNodeId must not be the same node (self-loops not allowed).");

        RuleFor(x => x.EdgeType)
            .IsInEnum().WithMessage("EdgeType is not a valid ActionEdgeType value.");

        // FR-1b: Conditional edges must carry non-empty Condition text.
        When(x => x.EdgeType == ActionEdgeType.Conditional, () =>
        {
            RuleFor(x => x.Condition)
                .NotEmpty().WithMessage("Condition is required for Conditional edges.")
                .MaximumLength(4096).WithMessage("Condition must not exceed 4096 characters.");
        });

        // AC-2f: OnFailure edges must not carry Condition text.
        When(x => x.EdgeType == ActionEdgeType.OnFailure, () =>
        {
            RuleFor(x => x.Condition)
                .Null().WithMessage("OnFailure edges must not carry a Condition expression.");
        });

        When(x => x.EdgeType != ActionEdgeType.Conditional
                  && x.EdgeType != ActionEdgeType.OnFailure
                  && x.Condition != null, () =>
        {
            RuleFor(x => x.Condition)
                .MaximumLength(4096).WithMessage("Condition must not exceed 4096 characters.");
        });
    }
}
