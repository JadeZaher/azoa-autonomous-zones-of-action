using FluentValidation;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

public class HolonCloneRequestValidator : AbstractValidator<HolonCloneRequest>
{
    public HolonCloneRequestValidator()
    {
        When(x => x.Name != null, () =>
        {
            RuleFor(x => x.Name)
                .NotEmpty().WithMessage("Name must not be empty when provided.")
                .MaximumLength(256).WithMessage("Name must not exceed 256 characters.");
        });

        // An ABSENT NewParentId is legitimate (clone keeps the original parent).
        // But if one is SUPPLIED it must be a real reference — Guid.Empty is malformed.
        RuleFor(x => x.NewParentId)
            .NotEqual(Guid.Empty).WithMessage("NewParentId must not be an empty GUID.")
            .When(x => x.NewParentId.HasValue);
    }
}
