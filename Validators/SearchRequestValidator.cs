using FluentValidation;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Validators;

public class SearchRequestValidator : AbstractValidator<SearchRequest>
{
    private static readonly string[] AllowedSortByFields =
        { "CreatedDate", "Name", "UpdatedDate" };

    public SearchRequestValidator()
    {
        RuleFor(x => x.Query)
            .NotEmpty().WithMessage("Query is required.")
            .MaximumLength(512).WithMessage("Query must not exceed 512 characters.");

        When(x => x.ChainId != null, () =>
        {
            RuleFor(x => x.ChainId)
                .MaximumLength(64).WithMessage("ChainId must not exceed 64 characters.");
        });

        When(x => x.AssetType != null, () =>
        {
            RuleFor(x => x.AssetType)
                .MaximumLength(64).WithMessage("AssetType must not exceed 64 characters.");
        });

        When(x => x.AvatarId.HasValue, () =>
        {
            RuleFor(x => x.AvatarId!.Value)
                .NotEqual(Guid.Empty).WithMessage("AvatarId must not be an empty GUID.");
        });

        // Cross-field range sanity: an inverted window (CreatedBefore < CreatedAfter)
        // is an impossible filter. Only enforced when BOTH bounds are supplied;
        // either bound alone is a legitimate open-ended range.
        RuleFor(x => x.CreatedBefore)
            .Must((req, _) => !(req.CreatedAfter.HasValue
                                && req.CreatedBefore.HasValue
                                && req.CreatedBefore.Value < req.CreatedAfter.Value))
            .WithMessage("CreatedBefore must be later than CreatedAfter.")
            .When(x => x.CreatedAfter.HasValue && x.CreatedBefore.HasValue);

        RuleFor(x => x.SortBy)
            .NotEmpty().WithMessage("SortBy is required.")
            .Must(s => AllowedSortByFields.Contains(s))
            .WithMessage($"SortBy must be one of: {string.Join(", ", AllowedSortByFields)}.");

        RuleFor(x => x.Page)
            .GreaterThanOrEqualTo(1).WithMessage("Page must be at least 1.");

        RuleFor(x => x.PageSize)
            .GreaterThanOrEqualTo(1).WithMessage("PageSize must be at least 1.")
            .LessThanOrEqualTo(200).WithMessage("PageSize must not exceed 200.");
    }
}
