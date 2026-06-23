using FluentValidation;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

public class HolonQueryRequestValidator : AbstractValidator<HolonQueryRequest>
{
    public HolonQueryRequestValidator()
    {
        When(x => x.Name != null, () =>
        {
            RuleFor(x => x.Name)
                .MaximumLength(256).WithMessage("Name must not exceed 256 characters.");
        });

        When(x => x.AvatarId.HasValue, () =>
        {
            RuleFor(x => x.AvatarId!.Value)
                .NotEqual(Guid.Empty).WithMessage("AvatarId must not be an empty GUID.");
        });

        When(x => x.ProviderName != null, () =>
        {
            RuleFor(x => x.ProviderName)
                .MaximumLength(128).WithMessage("ProviderName must not exceed 128 characters.");
        });

        When(x => x.ChainId != null, () =>
        {
            RuleFor(x => x.ChainId)
                .MaximumLength(64).WithMessage("ChainId must not exceed 64 characters.");
        });

        When(x => x.AssetType != null, () =>
        {
            RuleFor(x => x.AssetType)
                .MaximumLength(64).WithMessage("AssetType must not exceed 64 characters.");
        });

        When(x => x.ParentHolonId.HasValue, () =>
        {
            RuleFor(x => x.ParentHolonId!.Value)
                .NotEqual(Guid.Empty).WithMessage("ParentHolonId must not be an empty GUID.");
        });
    }
}
