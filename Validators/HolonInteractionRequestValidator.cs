using FluentValidation;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

public class HolonInteractionRequestValidator : AbstractValidator<HolonInteractionRequest>
{
    public HolonInteractionRequestValidator()
    {
        RuleForEach(x => x.AddPeerHolonIds)
            .NotEqual(Guid.Empty).WithMessage("Each AddPeerHolonId must not be an empty GUID.");

        RuleForEach(x => x.RemovePeerHolonIds)
            .NotEqual(Guid.Empty).WithMessage("Each RemovePeerHolonId must not be an empty GUID.");

        When(x => x.NewParentHolonId.HasValue, () =>
        {
            RuleFor(x => x.NewParentHolonId!.Value)
                .NotEqual(Guid.Empty).WithMessage("NewParentHolonId must not be an empty GUID.");
        });

        When(x => x.RemoveMetadataKeys != null, () =>
        {
            RuleForEach(x => x.RemoveMetadataKeys)
                .NotEmpty().WithMessage("Metadata key must not be empty.")
                .MaximumLength(256).WithMessage("Metadata key must not exceed 256 characters.");
        });
    }
}
