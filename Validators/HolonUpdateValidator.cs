using FluentValidation;
using AZOA.WebAPI.Models;

namespace AZOA.WebAPI.Validators;

public class HolonUpdateValidator : AbstractValidator<HolonUpdateModel>
{
    public HolonUpdateValidator()
    {
        RuleFor(x => x.Name).MaximumLength(200).When(x => x.Name != null);
        RuleFor(x => x.Description).MaximumLength(2000).When(x => x.Description != null);
        RuleFor(x => x.ProviderName).MaximumLength(100).When(x => x.ProviderName != null);
    }
}
