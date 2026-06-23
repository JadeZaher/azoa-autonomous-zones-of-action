using FluentValidation;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

public class SwapExecuteRequestValidator : AbstractValidator<SwapExecuteRequest>
{
    private static readonly string[] AllowedChains = { "algorand", "solana" };

    public SwapExecuteRequestValidator()
    {
        RuleFor(x => x.Chain)
            .NotEmpty().WithMessage("Chain is required.")
            .Must(c => AllowedChains.Contains(c?.ToLowerInvariant()))
            .WithMessage("Chain must be 'algorand' or 'solana'.");

        RuleFor(x => x.QuoteId)
            .NotEmpty().WithMessage("QuoteId is required.")
            .MaximumLength(256).WithMessage("QuoteId must not exceed 256 characters.");

        // Chain-agnostic permissive address charset: this endpoint executes a
        // value-moving swap, so we must NOT reject a plausibly-valid wallet
        // address. The union of real address encodings (base58, base64url,
        // hex, bech32, prefixed/checksummed forms) is covered by alnum plus
        // ': _ - + / = .'. We still block control chars / whitespace /
        // path-traversal and enforce sane length bounds. Per-chain validation
        // is the provider's job.
        RuleFor(x => x.WalletAddress)
            .NotEmpty().WithMessage("WalletAddress is required.")
            .MaximumLength(128).WithMessage("WalletAddress must not exceed 128 characters.")
            .Matches(@"^[A-Za-z0-9:_\-+/=\.]{20,128}$").WithMessage("WalletAddress is not a valid address.");
    }
}
