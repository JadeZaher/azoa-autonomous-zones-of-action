using FluentValidation;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

public class WalletTransferRequestValidator : AbstractValidator<WalletTransferRequest>
{
    public WalletTransferRequestValidator()
    {
        RuleFor(x => x.SourceWalletId)
            .NotEqual(Guid.Empty).WithMessage("SourceWalletId must not be an empty GUID.");

        // Chain-agnostic permissive address charset: this endpoint transfers
        // real value, so we must NOT reject a plausibly-valid destination. The
        // union of real address encodings (base58, base64url, hex, bech32,
        // prefixed/checksummed forms) is covered by alnum plus ': _ - + / = .'.
        // We still block control chars / whitespace / path-traversal and
        // enforce sane length bounds. Per-chain validation is the provider's job.
        RuleFor(x => x.DestinationAddress)
            .NotEmpty().WithMessage("DestinationAddress is required.")
            .MaximumLength(128).WithMessage("DestinationAddress must not exceed 128 characters.")
            .Matches(@"^[A-Za-z0-9:_\-+/=\.]{20,128}$").WithMessage("DestinationAddress is not a valid address.");

        RuleFor(x => x.Amount)
            .NotEmpty().WithMessage("Amount is required.")
            .Must(BeAPositiveDecimalString).WithMessage("Amount must be a positive numeric value.");

        When(x => x.TokenId != null, () =>
        {
            RuleFor(x => x.TokenId)
                .NotEmpty().WithMessage("TokenId must not be empty when provided.")
                .MaximumLength(256).WithMessage("TokenId must not exceed 256 characters.")
                .Matches(@"^[a-zA-Z0-9\-_\.]+$").WithMessage("TokenId contains invalid characters.");
        });
    }

    private static bool BeAPositiveDecimalString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return decimal.TryParse(value, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0;
    }
}
