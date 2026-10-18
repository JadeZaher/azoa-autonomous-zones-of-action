using FluentValidation;
using AZOA.WebAPI.Models;

namespace AZOA.WebAPI.Validators;

public class UserRegisterValidator : AbstractValidator<UserRegisterModel>
{
    public UserRegisterValidator()
    {
        RuleFor(x => x.Username).NotEmpty()
            .Length(UsernameRules.MinimumLength, UsernameRules.MaximumLength).WithMessage(UsernameRules.PatternMessage)
            .Matches(UsernameRules.Pattern).WithMessage(UsernameRules.PatternMessage)
            .Must(name => !UsernameRules.IsReservedGeneratedHandle(name)).WithMessage(UsernameRules.ReservedPrefixMessage);
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8)
            .Matches("[A-Z]").WithMessage("Password must contain an uppercase letter.")
            .Matches("[a-z]").WithMessage("Password must contain a lowercase letter.")
            .Matches("[0-9]").WithMessage("Password must contain a digit.");
        RuleFor(x => x.FirstName).MaximumLength(100);
        RuleFor(x => x.LastName).MaximumLength(100);
    }
}
