using FluentValidation;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

public class HolonPropagateRequestValidator : AbstractValidator<HolonPropagateRequest>
{
    private static readonly string[] AllowedProperties = { "IsActive" };

    public HolonPropagateRequestValidator()
    {
        RuleFor(x => x.Property)
            .NotEmpty().WithMessage("Property is required.")
            .Must(p => AllowedProperties.Contains(p))
            .WithMessage($"Property must be one of: {string.Join(", ", AllowedProperties)}.");
    }
}
