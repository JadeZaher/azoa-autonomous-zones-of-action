using FluentValidation;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

public class QuestCreateModelValidator : AbstractValidator<QuestCreateModel>
{
    public QuestCreateModelValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name is required.")
            .MaximumLength(256).WithMessage("Name must not exceed 256 characters.");

        When(x => x.Description != null, () =>
        {
            RuleFor(x => x.Description)
                .MaximumLength(2048).WithMessage("Description must not exceed 2048 characters.");
        });

        RuleForEach(x => x.Nodes)
            .SetValidator(new QuestNodeCreateModelValidator());

        RuleForEach(x => x.Edges)
            .SetValidator(new QuestEdgeCreateModelValidator());

        RuleFor(x => x.Nodes)
            .Must(nodes => nodes.Count(n => n.IsEntry) >= 1)
            .WithMessage("At least one node must be marked as an entry node.")
            .When(x => x.Nodes.Any());

        RuleFor(x => x.Nodes)
            .Must(nodes => nodes.Count(n => n.IsTerminal) >= 1)
            .WithMessage("At least one node must be marked as a terminal node.")
            .When(x => x.Nodes.Any());
    }
}
