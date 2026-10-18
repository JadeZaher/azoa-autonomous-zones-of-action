using FluentValidation;
using AZOA.WebAPI.Models;

namespace AZOA.WebAPI.Validators;

public class UserUpdateValidator : AbstractValidator<UserUpdateModel>
{
    public UserUpdateValidator()
    {
        RuleFor(x => x.Username!)
            .Length(UsernameRules.MinimumLength, UsernameRules.MaximumLength).WithMessage(UsernameRules.PatternMessage)
            .Matches(UsernameRules.Pattern).WithMessage(UsernameRules.PatternMessage)
            .When(x => x.Username != null);
        RuleFor(x => x.Email).EmailAddress().When(x => x.Email != null);
        RuleFor(x => x.FirstName).MaximumLength(100).When(x => x.FirstName != null);
        RuleFor(x => x.LastName).MaximumLength(100).When(x => x.LastName != null);
    }
}
