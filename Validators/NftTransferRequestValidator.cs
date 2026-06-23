using FluentValidation;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

public class NftTransferRequestValidator : AbstractValidator<NftTransferRequest>
{
    public NftTransferRequestValidator()
    {
        RuleFor(x => x.TargetAvatarId)
            .NotEqual(Guid.Empty).WithMessage("TargetAvatarId must not be an empty GUID.");

        RuleFor(x => x.WalletId)
            .NotEqual(Guid.Empty).WithMessage("WalletId must not be an empty GUID.");

        When(x => x.Memo != null, () =>
        {
            RuleFor(x => x.Memo)
                .MaximumLength(512).WithMessage("Memo must not exceed 512 characters.");
        });
    }
}
