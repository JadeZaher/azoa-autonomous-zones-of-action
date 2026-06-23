using FluentValidation;
using AZOA.WebAPI.Models;

namespace AZOA.WebAPI.Validators;

public class HolonCreateValidator : AbstractValidator<HolonCreateModel>
{
    public HolonCreateValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.ProviderName).NotEmpty().MaximumLength(100);
    }
}
