using FluentValidation;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

public class MoveSubtreeRequestValidator : AbstractValidator<MoveSubtreeRequest>
{
    public MoveSubtreeRequestValidator()
    {
        RuleFor(x => x.NewParentId)
            .NotEqual(Guid.Empty).WithMessage("NewParentId must not be an empty GUID.");
    }
}
