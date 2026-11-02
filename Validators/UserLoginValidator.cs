using FluentValidation;
using AZOA.WebAPI.Models;

namespace AZOA.WebAPI.Validators;

public class UserLoginValidator : AbstractValidator<UserLoginModel>
{
    public UserLoginValidator()
    {
        RuleFor(x => x.UserId).NotEqual((Guid?)Guid.Empty).When(x => x.UserId.HasValue);
        RuleFor(x => x.Email).Must(string.IsNullOrWhiteSpace).When(x => x.UserId.HasValue)
            .WithMessage("Provide one login identity.");
        RuleFor(x => x.Email).NotEmpty().EmailAddress().When(x => !x.UserId.HasValue);
        RuleFor(x => x.Password).NotEmpty();
    }
}
