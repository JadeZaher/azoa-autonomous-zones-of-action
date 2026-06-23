using FluentValidation;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

public class QuestTemplateCreateModelValidator : AbstractValidator<QuestTemplateCreateModel>
{
    public QuestTemplateCreateModelValidator()
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

        RuleFor(x => x.Parameters)
            .NotEmpty().WithMessage("Parameters is required.")
            .MaximumLength(65536).WithMessage("Parameters must not exceed 65536 characters.");

        RuleFor(x => x.Version)
            .NotEmpty().WithMessage("Version is required.")
            .MaximumLength(32).WithMessage("Version must not exceed 32 characters.")
            .Matches(@"^\d+\.\d+(\.\d+)?$").WithMessage("Version must follow semver format (e.g., 1.0.0).");

        RuleForEach(x => x.Tags)
            .NotEmpty().WithMessage("Tags must not contain empty strings.")
            .MaximumLength(64).WithMessage("Each tag must not exceed 64 characters.");
    }
}
