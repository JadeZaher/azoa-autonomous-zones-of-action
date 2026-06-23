using FluentValidation;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

public class SwapQuoteRequestValidator : AbstractValidator<SwapQuoteRequest>
{
    private static readonly string[] AllowedChains = { "algorand", "solana" };

    public SwapQuoteRequestValidator()
    {
        RuleFor(x => x.Chain)
            .NotEmpty().WithMessage("Chain is required.")
            .Must(c => AllowedChains.Contains(c?.ToLowerInvariant()))
            .WithMessage("Chain must be 'algorand' or 'solana'.");

        RuleFor(x => x.TokenIn)
            .NotEmpty().WithMessage("TokenIn is required.")
            .MaximumLength(256).WithMessage("TokenIn must not exceed 256 characters.")
            .Matches(@"^[a-zA-Z0-9\-_\.]+$").WithMessage("TokenIn contains invalid characters.");

        RuleFor(x => x.TokenOut)
            .NotEmpty().WithMessage("TokenOut is required.")
            .MaximumLength(256).WithMessage("TokenOut must not exceed 256 characters.")
            .Matches(@"^[a-zA-Z0-9\-_\.]+$").WithMessage("TokenOut contains invalid characters.");

        RuleFor(x => x.AmountIn)
            .NotEmpty().WithMessage("AmountIn is required.")
            .Must(BeAPositiveDecimalString).WithMessage("AmountIn must be a positive numeric value.");

        RuleFor(x => x.SlippageBps)
            .GreaterThanOrEqualTo(0).WithMessage("SlippageBps must not be negative.")
            .LessThanOrEqualTo(10_000).WithMessage("SlippageBps must not exceed 10000 (100%).");

        When(x => x.WalletAddress != null, () =>
        {
            RuleFor(x => x.WalletAddress)
                .NotEmpty().WithMessage("WalletAddress must not be empty when provided.")
                .MaximumLength(128).WithMessage("WalletAddress must not exceed 128 characters.")
                .Matches(@"^[a-zA-Z0-9]+$").WithMessage("WalletAddress contains invalid characters.");
        });
    }

    private static bool BeAPositiveDecimalString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return decimal.TryParse(value, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0;
    }
}
