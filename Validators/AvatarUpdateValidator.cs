using FluentValidation;
using AZOA.WebAPI.Models;

namespace AZOA.WebAPI.Validators;

public class AvatarUpdateValidator : AbstractValidator<AvatarUpdateModel>
{
    public AvatarUpdateValidator()
    {
        RuleFor(x => x.Username).Length(3, 50).Matches("^[a-zA-Z0-9_]+$")
            .When(x => x.Username != null)
            .WithMessage("Username must contain only letters, numbers, and underscores.");
        RuleFor(x => x.Email).EmailAddress().When(x => x.Email != null);
        RuleFor(x => x.FirstName).MaximumLength(100).When(x => x.FirstName != null);
        RuleFor(x => x.LastName).MaximumLength(100).When(x => x.LastName != null);
    }
}
