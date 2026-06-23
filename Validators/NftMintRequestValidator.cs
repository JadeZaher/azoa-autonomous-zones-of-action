using FluentValidation;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

public class NftMintRequestValidator : AbstractValidator<NftMintRequest>
{
    public NftMintRequestValidator()
    {
        RuleFor(x => x.WalletId)
            .NotEqual(Guid.Empty).WithMessage("WalletId must not be an empty GUID.");

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name is required.")
            .MaximumLength(256).WithMessage("Name must not exceed 256 characters.");

        RuleFor(x => x.Description)
            .NotEmpty().WithMessage("Description is required.")
            .MaximumLength(2048).WithMessage("Description must not exceed 2048 characters.");

        RuleFor(x => x.ChainId)
            .NotEmpty().WithMessage("ChainId is required.")
            .MaximumLength(64).WithMessage("ChainId must not exceed 64 characters.");

        When(x => x.TokenId != null, () =>
        {
            RuleFor(x => x.TokenId)
                .NotEmpty().WithMessage("TokenId must not be empty when provided.")
                .MaximumLength(256).WithMessage("TokenId must not exceed 256 characters.")
                .Matches(@"^[a-zA-Z0-9\-_\.]+$").WithMessage("TokenId contains invalid characters.");
        });

        When(x => x.ImageUri != null, () =>
        {
            RuleFor(x => x.ImageUri)
                .NotEmpty().WithMessage("ImageUri must not be empty when provided.")
                .MaximumLength(2048).WithMessage("ImageUri must not exceed 2048 characters.");
        });

        When(x => x.ExternalUri != null, () =>
        {
            RuleFor(x => x.ExternalUri)
                .NotEmpty().WithMessage("ExternalUri must not be empty when provided.")
                .MaximumLength(2048).WithMessage("ExternalUri must not exceed 2048 characters.");
        });
    }
}
