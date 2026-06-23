using FluentValidation;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

public class QuestNodeCreateModelValidator : AbstractValidator<QuestNodeCreateModel>
{
    public QuestNodeCreateModelValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Node Name is required.")
            .MaximumLength(256).WithMessage("Node Name must not exceed 256 characters.");

        RuleFor(x => x.NodeType)
            .IsInEnum().WithMessage("NodeType is not a valid QuestNodeType value.");

        RuleFor(x => x.Config)
            .NotEmpty().WithMessage("Config is required.")
            .MaximumLength(65536).WithMessage("Config must not exceed 65536 characters.");

        When(x => x.NodeTemplateId.HasValue, () =>
        {
            RuleFor(x => x.NodeTemplateId!.Value)
                .NotEqual(Guid.Empty).WithMessage("NodeTemplateId must not be an empty GUID.");
        });
    }
}
