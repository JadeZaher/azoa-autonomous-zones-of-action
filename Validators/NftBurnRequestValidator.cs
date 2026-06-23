using FluentValidation;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

public class NftBurnRequestValidator : AbstractValidator<NftBurnRequest>
{
    public NftBurnRequestValidator()
    {
        RuleFor(x => x.WalletId)
            .NotEqual(Guid.Empty).WithMessage("WalletId must not be an empty GUID.");
    }
}
