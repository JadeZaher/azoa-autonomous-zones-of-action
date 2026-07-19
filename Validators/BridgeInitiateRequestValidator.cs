using FluentValidation;
using AZOA.WebAPI.Controllers;

namespace AZOA.WebAPI.Validators;

public class BridgeInitiateRequestValidator : AbstractValidator<BridgeInitiateRequest>
{
    public BridgeInitiateRequestValidator()
    {
        RuleFor(x => x.SourceChain)
            .NotEmpty().WithMessage("SourceChain is required.")
            .MaximumLength(64).WithMessage("SourceChain must not exceed 64 characters.");

        RuleFor(x => x.TargetChain)
            .NotEmpty().WithMessage("TargetChain is required.")
            .MaximumLength(64).WithMessage("TargetChain must not exceed 64 characters.");

        RuleFor(x => x)
            .Must(r => !string.Equals(r.SourceChain, r.TargetChain, StringComparison.OrdinalIgnoreCase))
            .WithMessage("SourceChain and TargetChain must be different.")
            .When(r => !string.IsNullOrEmpty(r.SourceChain) && !string.IsNullOrEmpty(r.TargetChain));

        RuleFor(x => x.TokenId)
            .NotEmpty().WithMessage("TokenId is required.")
            .MaximumLength(256).WithMessage("TokenId must not exceed 256 characters.")
            .Matches(@"^[a-zA-Z0-9\-_\.]+$").WithMessage("TokenId contains invalid characters.");

        // Chain-agnostic permissive address charset: this endpoint moves real
        // value across chains, so we must NOT reject a plausibly-valid
        // recipient. The union of real address encodings (base58, base64url,
        // hex, bech32, prefixed/checksummed forms) is covered by alnum plus
        // ': _ - + / = .'. We still block control chars / whitespace /
        // path-traversal and enforce sane length bounds. Authoritative
        // per-chain address validation is the provider's job.
        RuleFor(x => x.RecipientAddress)
            .NotEmpty().WithMessage("RecipientAddress is required.")
            .MaximumLength(128).WithMessage("RecipientAddress must not exceed 128 characters.")
            .Matches(@"^[A-Za-z0-9:_\-+/=\.]{20,128}$").WithMessage("RecipientAddress is not a valid address.");

        // The provider boundary is ulong; validate the exact wire value here.
        RuleFor(x => x.Amount)
            .NotEmpty().WithMessage("Amount is required.")
            .Must(a => ulong.TryParse(
                a,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value) && value > 0)
            .WithMessage("Amount must be a positive unsigned 64-bit integer.");
    }
}
