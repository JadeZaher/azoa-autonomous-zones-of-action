using FluentValidation;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

public class QuestNodeTemplateCreateModelValidator : AbstractValidator<QuestNodeTemplateCreateModel>
{
    public QuestNodeTemplateCreateModelValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name is required.")
            .MaximumLength(256).WithMessage("Name must not exceed 256 characters.");

        RuleFor(x => x.NodeType)
            .IsInEnum().WithMessage("NodeType is not a valid QuestNodeType value.");

        When(x => x.Description != null, () =>
        {
            RuleFor(x => x.Description)
                .MaximumLength(2048).WithMessage("Description must not exceed 2048 characters.");
        });

        RuleFor(x => x.DefaultConfig)
            .NotEmpty().WithMessage("DefaultConfig is required.")
            .MaximumLength(65536).WithMessage("DefaultConfig must not exceed 65536 characters.");

        RuleFor(x => x.ConfigSchema)
            .NotEmpty().WithMessage("ConfigSchema is required.")
            .MaximumLength(65536).WithMessage("ConfigSchema must not exceed 65536 characters.");

        RuleFor(x => x.InputSchema)
            .NotEmpty().WithMessage("InputSchema is required.")
            .MaximumLength(65536).WithMessage("InputSchema must not exceed 65536 characters.");

        RuleFor(x => x.OutputSchema)
            .NotEmpty().WithMessage("OutputSchema is required.")
            .MaximumLength(65536).WithMessage("OutputSchema must not exceed 65536 characters.");

        RuleFor(x => x.Version)
            .NotEmpty().WithMessage("Version is required.")
            .MaximumLength(32).WithMessage("Version must not exceed 32 characters.")
            .Matches(@"^\d+\.\d+(\.\d+)?$").WithMessage("Version must follow semver format (e.g., 1.0.0).");

        RuleForEach(x => x.Tags)
            .NotEmpty().WithMessage("Tags must not contain empty strings.")
            .MaximumLength(64).WithMessage("Each tag must not exceed 64 characters.");
    }
}
