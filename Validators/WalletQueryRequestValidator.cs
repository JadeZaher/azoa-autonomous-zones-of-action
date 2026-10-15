using FluentValidation;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

public class WalletQueryRequestValidator : AbstractValidator<WalletQueryRequest>
{
    public WalletQueryRequestValidator()
    {
        When(x => x.UserId.HasValue, () =>
        {
            RuleFor(x => x.UserId!.Value)
                .NotEqual(Guid.Empty).WithMessage("UserId must not be an empty GUID.");
        });

        When(x => x.ChainType != null, () =>
        {
            RuleFor(x => x.ChainType)
                .NotEmpty().WithMessage("ChainType must not be empty when provided.")
                .MaximumLength(64).WithMessage("ChainType must not exceed 64 characters.");
        });
    }
}
