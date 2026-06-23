using FluentValidation;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

public class WalletNFTBindingUpdateModelValidator : AbstractValidator<WalletNFTBindingUpdateModel>
{
    public WalletNFTBindingUpdateModelValidator()
    {
        When(x => x.BindingType != null, () =>
        {
            RuleFor(x => x.BindingType)
                .NotEmpty().WithMessage("BindingType must not be empty when provided.")
                .MaximumLength(64).WithMessage("BindingType must not exceed 64 characters.");
        });

        When(x => x.AccessLevel != null, () =>
        {
            RuleFor(x => x.AccessLevel)
                .NotEmpty().WithMessage("AccessLevel must not be empty when provided.")
                .MaximumLength(64).WithMessage("AccessLevel must not exceed 64 characters.");
        });
    }
}
