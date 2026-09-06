using FluentValidation;
using AZOA.WebAPI.Models;

namespace AZOA.WebAPI.Validators;

public class UserLoginValidator : AbstractValidator<UserLoginModel>
{
    public UserLoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty();
    }
}
