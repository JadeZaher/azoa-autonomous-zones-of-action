using FluentValidation;
using AZOA.WebAPI.Models;

namespace AZOA.WebAPI.Validators;

public class AvatarRegisterValidator : AbstractValidator<AvatarRegisterModel>
{
    public AvatarRegisterValidator()
    {
        RuleFor(x => x.Username).NotEmpty().Length(3, 50).Matches("^[a-zA-Z0-9_]+$")
            .WithMessage("Username must contain only letters, numbers, and underscores.");
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8)
            .Matches("[A-Z]").WithMessage("Password must contain an uppercase letter.")
            .Matches("[a-z]").WithMessage("Password must contain a lowercase letter.")
            .Matches("[0-9]").WithMessage("Password must contain a digit.");
        RuleFor(x => x.FirstName).MaximumLength(100);
        RuleFor(x => x.LastName).MaximumLength(100);
    }
}
