using FluentValidation;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

public class QuestEdgeCreateModelValidator : AbstractValidator<QuestEdgeCreateModel>
{
    public QuestEdgeCreateModelValidator()
    {
        RuleFor(x => x.SourceNodeId)
            .GreaterThanOrEqualTo(0).WithMessage("SourceNodeId must be a non-negative index.");

        RuleFor(x => x.TargetNodeId)
            .GreaterThanOrEqualTo(0).WithMessage("TargetNodeId must be a non-negative index.");

        RuleFor(x => x)
            .Must(e => e.SourceNodeId != e.TargetNodeId)
            .WithMessage("SourceNodeId and TargetNodeId must not be the same node (self-loops not allowed).");

        RuleFor(x => x.EdgeType)
            .IsInEnum().WithMessage("EdgeType is not a valid QuestEdgeType value.");

        When(x => x.Condition != null, () =>
        {
            RuleFor(x => x.Condition)
                .MaximumLength(4096).WithMessage("Condition must not exceed 4096 characters.");
        });
    }
}
