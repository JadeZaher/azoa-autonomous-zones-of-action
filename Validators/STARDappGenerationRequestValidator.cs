using FluentValidation;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

public class STARDappGenerationRequestValidator : AbstractValidator<STARDappGenerationRequest>
{
    public STARDappGenerationRequestValidator()
    {
        RuleFor(x => x.TargetChain)
            .NotEmpty().WithMessage("TargetChain is required.")
            .MaximumLength(64).WithMessage("TargetChain must not exceed 64 characters.");

        RuleForEach(x => x.BoundHolonIds)
            .NotEqual(Guid.Empty).WithMessage("Each BoundHolonId must not be an empty GUID.");
    }
}
