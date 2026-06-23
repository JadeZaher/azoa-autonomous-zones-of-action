using FluentValidation;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

public class AvatarNFTMintModelValidator : AbstractValidator<AvatarNFTMintModel>
{
    public AvatarNFTMintModelValidator()
    {
        RuleFor(x => x.ChainType)
            .NotEmpty().WithMessage("ChainType is required.")
            .MaximumLength(64).WithMessage("ChainType must not exceed 64 characters.");

        RuleFor(x => x.NFTContractAddress)
            .NotEmpty().WithMessage("NFTContractAddress is required.")
            .MaximumLength(128).WithMessage("NFTContractAddress must not exceed 128 characters.")
            .Matches(@"^[a-zA-Z0-9]+$").WithMessage("NFTContractAddress must contain only alphanumeric characters.");

        RuleFor(x => x.TokenStandard)
            .NotEmpty().WithMessage("TokenStandard is required.")
            .MaximumLength(32).WithMessage("TokenStandard must not exceed 32 characters.");

        RuleFor(x => x.MetadataURI)
            .NotEmpty().WithMessage("MetadataURI is required.")
            .MaximumLength(2048).WithMessage("MetadataURI must not exceed 2048 characters.");

        When(x => x.ImageURI != null, () =>
        {
            RuleFor(x => x.ImageURI)
                .NotEmpty().WithMessage("ImageURI must not be empty when provided.")
                .MaximumLength(2048).WithMessage("ImageURI must not exceed 2048 characters.");
        });

        When(x => x.Name != null, () =>
        {
            RuleFor(x => x.Name)
                .MaximumLength(256).WithMessage("Name must not exceed 256 characters.");
        });

        When(x => x.Description != null, () =>
        {
            RuleFor(x => x.Description)
                .MaximumLength(2048).WithMessage("Description must not exceed 2048 characters.");
        });

        RuleFor(x => x.RoyaltyPercentage)
            .GreaterThanOrEqualTo(0).WithMessage("RoyaltyPercentage must not be negative.")
            .LessThanOrEqualTo(100).WithMessage("RoyaltyPercentage must not exceed 100.");

        When(x => x.RoyaltyRecipient != null, () =>
        {
            RuleFor(x => x.RoyaltyRecipient)
                .NotEmpty().WithMessage("RoyaltyRecipient must not be empty when provided.")
                .MaximumLength(128).WithMessage("RoyaltyRecipient must not exceed 128 characters.")
                .Matches(@"^[a-zA-Z0-9]+$").WithMessage("RoyaltyRecipient must contain only alphanumeric characters.");
        });
    }
}
