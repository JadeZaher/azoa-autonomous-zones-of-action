using FluentValidation;
using AZOA.WebAPI.Models;

namespace AZOA.WebAPI.Validators;

public class AvatarLoginValidator : AbstractValidator<AvatarLoginModel>
{
    public AvatarLoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty();
    }
}
