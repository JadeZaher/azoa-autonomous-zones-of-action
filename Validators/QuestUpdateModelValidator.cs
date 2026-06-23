using FluentValidation;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

public class QuestUpdateModelValidator : AbstractValidator<QuestUpdateModel>
{
    public QuestUpdateModelValidator()
    {
        When(x => x.Name != null, () =>
        {
            RuleFor(x => x.Name)
                .NotEmpty().WithMessage("Name must not be empty when provided.")
                .MaximumLength(256).WithMessage("Name must not exceed 256 characters.");
        });

        When(x => x.Description != null, () =>
        {
            RuleFor(x => x.Description)
                .MaximumLength(2048).WithMessage("Description must not exceed 2048 characters.");
        });

        When(x => x.Status.HasValue, () =>
        {
            RuleFor(x => x.Status!.Value)
                .IsInEnum().WithMessage("Status is not a valid QuestStatus value.");
        });
    }
}
