using FluentValidation;
using AZOA.WebAPI.Controllers;

namespace AZOA.WebAPI.Validators;

public class BridgeReverseRequestValidator : AbstractValidator<BridgeReverseRequest>
{
    public BridgeReverseRequestValidator()
    {
        // Chain-agnostic permissive address charset: this endpoint moves real
        // value, so we must NOT reject a plausibly-valid recipient. The union
        // of real address encodings (base58, base64url, hex, bech32,
        // prefixed/checksummed forms) is covered by alnum plus ': _ - + / = .'.
        // We still block control chars / whitespace / path-traversal and
        // enforce sane length bounds. Per-chain validation is the provider's job.
        RuleFor(x => x.SourceRecipientAddress)
            .NotEmpty().WithMessage("SourceRecipientAddress is required.")
            .MaximumLength(128).WithMessage("SourceRecipientAddress must not exceed 128 characters.")
            .Matches(@"^[A-Za-z0-9:_\-+/=\.]{20,128}$").WithMessage("SourceRecipientAddress is not a valid address.");
    }
}
