using FluentValidation;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

public class AZOARequestValidator : AbstractValidator<AZOARequest>
{
    public AZOARequestValidator()
    {
        RuleFor(x => x.ProviderType)
            .IsInEnum().WithMessage("ProviderType is not a valid value.");

        RuleFor(x => x.AutoLoadBalanceMode)
            .IsInEnum().WithMessage("AutoLoadBalanceMode is not a valid value.");

        RuleForEach(x => x.CustomProviderKeys)
            .NotEmpty().WithMessage("Each CustomProviderKey must not be empty.")
            .MaximumLength(128).WithMessage("Each CustomProviderKey must not exceed 128 characters.");
    }
}
