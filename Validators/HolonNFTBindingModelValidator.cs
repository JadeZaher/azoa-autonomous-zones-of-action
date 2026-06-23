using FluentValidation;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

public class HolonNFTBindingModelValidator : AbstractValidator<HolonNFTBindingModel>
{
    public HolonNFTBindingModelValidator()
    {
        RuleFor(x => x.Role)
            .NotEmpty().WithMessage("Role is required.")
            .MaximumLength(64).WithMessage("Role must not exceed 64 characters.");

        When(x => x.PermissionLevel != null, () =>
        {
            RuleFor(x => x.PermissionLevel)
                .NotEmpty().WithMessage("PermissionLevel must not be empty when provided.")
                .MaximumLength(64).WithMessage("PermissionLevel must not exceed 64 characters.");
        });
    }
}
