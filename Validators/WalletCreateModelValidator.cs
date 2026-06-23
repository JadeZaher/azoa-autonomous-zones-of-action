using FluentValidation;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

public class WalletCreateModelValidator : AbstractValidator<WalletCreateModel>
{
    public WalletCreateModelValidator()
    {
        RuleFor(x => x.ChainType)
            .NotEmpty().WithMessage("ChainType is required.")
            .MaximumLength(64).WithMessage("ChainType must not exceed 64 characters.");

        RuleFor(x => x.Address)
            .NotEmpty().WithMessage("Address is required.")
            .MaximumLength(128).WithMessage("Address must not exceed 128 characters.")
            .Matches(@"^[a-zA-Z0-9]+$").WithMessage("Address must contain only alphanumeric characters.");

        When(x => x.PublicKey != null, () =>
        {
            RuleFor(x => x.PublicKey)
                .NotEmpty().WithMessage("PublicKey must not be empty when provided.")
                .MaximumLength(256).WithMessage("PublicKey must not exceed 256 characters.")
                .Matches(@"^[a-zA-Z0-9\+/=]+$").WithMessage("PublicKey contains invalid characters.");
        });

        When(x => x.Label != null, () =>
        {
            RuleFor(x => x.Label)
                .MaximumLength(128).WithMessage("Label must not exceed 128 characters.");
        });

        RuleFor(x => x.WalletType)
            .IsInEnum().WithMessage("WalletType is not a valid value.");
    }
}
