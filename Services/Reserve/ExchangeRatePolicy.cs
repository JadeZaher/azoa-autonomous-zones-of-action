// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Numerics;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Reserve;

/// <summary>Operator configuration for the treasury-rate exchange — the <c>Exchange</c> section.</summary>
public sealed class ExchangeOptions
{
    public const string SectionName = "Exchange";

    /// <summary>
    /// How old a declared rate may be at the exchange it prices. Non-positive REFUSES every
    /// exchange rather than accepting any age — the fail-closed reading, never "no limit".
    /// </summary>
    public TimeSpan MaxDeclaredRateAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// The furthest ahead a hold window may be set. A window nobody alive would see is a way
    /// to take a sponsor's funds and never hand over the units.
    /// </summary>
    public TimeSpan MaxHoldWindow { get; set; } = TimeSpan.FromDays(1826);

    /// <summary>
    /// Whether an exchange must carry a declared rate. True by default: a value move whose
    /// price nobody declared cannot be audited or re-derived.
    /// </summary>
    public bool RequireDeclaredRate { get; set; } = true;

    /// <summary>Clock-skew grace before a rate dated ahead of its own exchange is refused.</summary>
    public TimeSpan FutureRateTolerance { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>Everything a rate policy is allowed to see about one exchange.</summary>
/// <param name="ChainType">Rail both legs settle on.</param>
/// <param name="Direction">Which way units move.</param>
/// <param name="ProjectAssetId">The project's asset.</param>
/// <param name="PlatformAssetId">The platform unit, as genesis recorded it.</param>
/// <param name="PlatformAmount">Platform-unit base units moving.</param>
/// <param name="ProjectUnitAmount">Project-unit base units moving.</param>
/// <param name="DeclaredRate">The consumer's own declaration, already parsed; null when none was sent.</param>
/// <param name="Now">The instant the exchange is being decided at.</param>
public sealed record ExchangeRateEvaluation(
    string ChainType,
    ExchangeDirection Direction,
    string ProjectAssetId,
    string PlatformAssetId,
    BigInteger PlatformAmount,
    BigInteger ProjectUnitAmount,
    TenantDeclaredRate? DeclaredRate,
    DateTimeOffset Now);

/// <summary>What a rate policy admitted, and under which policy version.</summary>
/// <param name="DeclaredRate">The rate that will be recorded and stamped; null when the node prices nothing.</param>
/// <param name="PolicyVersion">Identifier of the policy that admitted this exchange, carried onto the record.</param>
public sealed record ExchangeRateDecision(TenantDeclaredRate? DeclaredRate, string PolicyVersion);

/// <summary>
/// The seam that decides whether one exchange may happen at the rate its consumer declared.
/// AZOA never computes a rate (D7): a policy either admits the declaration or refuses it.
/// </summary>
public interface IExchangeRatePolicy
{
    /// <summary>Admits the exchange, or refuses it with a message safe to show a consumer.</summary>
    Task<AZOAResult<ExchangeRateDecision>> EvaluateAsync(
        ExchangeRateEvaluation evaluation, CancellationToken ct = default);
}

/// <summary>
/// The buy-back liquidity policy the SELL direction needs: how much of the platform unit is
/// paid for project units, drawn from the project's own treasury with the platform reserve as
/// the backstop, with per-project caps and a queue when the treasury is short.
/// </summary>
/// <remarks>
/// Nothing implements this yet — it lands in W8.2, deliberately BEFORE any user can sell.
/// <see cref="TreasuryRateExchangePolicy"/> refuses every sell while no implementation is
/// registered, so "sell is not available yet" is a null check in the container rather than a
/// sentence in a document.
/// </remarks>
public interface IExchangeLiquidityPolicy
{
    /// <summary>Admits one sell, or refuses it. Refusal text must already be consumer-safe.</summary>
    Task<AZOAResult<bool>> EnsureSellAllowedAsync(
        ExchangeRateEvaluation evaluation, CancellationToken ct = default);
}

/// <summary>
/// The default rate policy: the consumer's declaration is admitted when it is present, fresh,
/// in the pinned currency and about the asset actually moving; the reverse direction is
/// refused outright until a buy-back liquidity policy exists.
/// </summary>
/// <remarks>
/// The refusals here are the whole point of the type. A stale rate presented as current, a
/// rate for a different asset, and a sell with no liquidity behind it are each a way to move
/// real value on a number nobody stands behind.
/// </remarks>
public sealed class TreasuryRateExchangePolicy : IExchangeRatePolicy
{
    /// <summary>Version stamped onto every record this policy admits.</summary>
    public const string Version = "azoa.treasury-rate-exchange.v1";

    /// <summary>D15 pins the declared currency to USD; anything else is declined, never converted.</summary>
    private const string SupportedCurrencyCode = "USD";

    /// <summary>
    /// The one refusal a consumer sees for the reverse direction. Kept as a constant so the
    /// wording — and the phrase callers and tests key on — cannot drift.
    /// </summary>
    public const string SellUnavailableMessage =
        "Selling project units back is not available on this node yet. Nothing was moved.";

    private readonly ExchangeOptions _options;
    private readonly IExchangeLiquidityPolicy? _liquidity;

    /// <param name="liquidity">
    /// The W8.2 buy-back policy. Absent by default, and its absence is what refuses every
    /// sell — a sell admitted with no liquidity behind it is a promise the node cannot keep.
    /// </param>
    public TreasuryRateExchangePolicy(
        ExchangeOptions options, IExchangeLiquidityPolicy? liquidity = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _liquidity = liquidity;
    }

    /// <inheritdoc />
    public async Task<AZOAResult<ExchangeRateDecision>> EvaluateAsync(
        ExchangeRateEvaluation evaluation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        if (evaluation.PlatformAmount <= BigInteger.Zero || evaluation.ProjectUnitAmount <= BigInteger.Zero)
        {
            return Refuse("Both sides of an exchange must be a positive whole amount.");
        }

        if (string.Equals(evaluation.ProjectAssetId, evaluation.PlatformAssetId, StringComparison.Ordinal))
        {
            return Refuse(
                "An exchange must move two different kinds of unit; the two sides name the same one.");
        }

        if (evaluation.Direction == ExchangeDirection.Sell)
        {
            if (_liquidity is null)
                return Refuse(SellUnavailableMessage);

            var allowed = await _liquidity.EnsureSellAllowedAsync(evaluation, ct);
            if (allowed.IsError || !allowed.Result)
            {
                return Refuse(allowed.IsError ? allowed.Message : SellUnavailableMessage);
            }
        }

        var rateRefusal = DescribeRateRefusal(evaluation);
        if (rateRefusal is not null)
            return Refuse(rateRefusal);

        return AZOAResult<ExchangeRateDecision>.Success(
            new ExchangeRateDecision(evaluation.DeclaredRate, Version),
            "Exchange admitted at the declared rate.");
    }

    /// <summary>
    /// Why this declaration cannot price this exchange, or null when it can. Every branch is a
    /// refusal rather than a substitution: there is no last-known value and no default.
    /// </summary>
    private string? DescribeRateRefusal(ExchangeRateEvaluation evaluation)
    {
        var declared = evaluation.DeclaredRate;
        if (declared is null)
        {
            return _options.RequireDeclaredRate
                ? "This exchange must carry the rate it was priced at, and none was supplied."
                : null;
        }

        if (!string.Equals(declared.CurrencyCode, SupportedCurrencyCode, StringComparison.Ordinal))
            return $"Rates are recorded in {SupportedCurrencyCode} only.";

        // The rate must be ABOUT the units being exchanged. A rate for a different asset is a
        // different price, never a fallback for this one.
        if (!string.Equals(declared.AssetId, evaluation.ProjectAssetId, StringComparison.Ordinal))
            return "The supplied rate does not price the units this exchange moves.";

        if (!string.Equals(declared.ChainType, evaluation.ChainType, StringComparison.OrdinalIgnoreCase))
            return "The supplied rate was declared for a different settlement rail.";

        if (declared.MinorUnitsPerBaseUnit <= 0m)
            return "A declared rate of zero cannot price a sponsorship.";

        if (declared.DeclaredAt > evaluation.Now + _options.FutureRateTolerance)
            return "The supplied rate is dated after the exchange it would price.";

        var maxAge = _options.MaxDeclaredRateAge;
        if (maxAge <= TimeSpan.Zero)
            return "This node is not configured to accept declared rates, so it cannot price an exchange.";

        return evaluation.Now - declared.DeclaredAt > maxAge
            ? "The supplied rate is older than this node accepts; re-declare it and try again."
            : null;
    }

    private static AZOAResult<ExchangeRateDecision> Refuse(string message) =>
        AZOAResult<ExchangeRateDecision>.FailureWithCode(message, AzoaErrorCodes.OperationNotAllowed);
}

/// <summary>
/// Turns the wire-shaped <see cref="DeclaredExchangeRate"/> into the canonical
/// <see cref="TenantDeclaredRate"/> the stamp and the store both speak.
/// </summary>
/// <remarks>
/// Both numeric forms are parsed with invariant culture, and the whole-unit form is converted
/// through <see cref="TenantDeclaredRate.FromWholeUnitPrice"/>, which refuses a price it
/// cannot represent exactly. A rounded money constant is a fabricated one.
/// </remarks>
public static class DeclaredRateBinding
{
    /// <summary>Binds the declaration, or explains why it cannot be bound.</summary>
    public static bool TryBind(
        DeclaredExchangeRate? declared,
        string chainType,
        string assetId,
        out TenantDeclaredRate? rate,
        out string error)
    {
        rate = null;
        error = string.Empty;

        if (declared is null)
            return true;

        if (string.IsNullOrWhiteSpace(declared.Source))
        {
            error = "A declared rate must say where it came from.";
            return false;
        }

        if (declared.DeclaredAt is not { } declaredAt)
        {
            error = "A declared rate must say when it was observed.";
            return false;
        }

        var currencyCode = string.IsNullOrWhiteSpace(declared.CurrencyCode)
            ? "USD"
            : declared.CurrencyCode.Trim();

        var hasPerBaseUnit = !string.IsNullOrWhiteSpace(declared.MinorUnitsPerBaseUnit);
        var hasWholeUnit = !string.IsNullOrWhiteSpace(declared.MajorUnitsPerWholeUnit);
        if (hasPerBaseUnit == hasWholeUnit)
        {
            error =
                "A declared rate must carry exactly one of a per-base-unit rate and a whole-unit price.";
            return false;
        }

        try
        {
            if (hasPerBaseUnit)
            {
                if (!TryParseDecimal(declared.MinorUnitsPerBaseUnit, out var perBaseUnit))
                {
                    error = "The declared per-base-unit rate is not a valid decimal number.";
                    return false;
                }

                rate = new TenantDeclaredRate(
                    chainType, assetId, currencyCode, perBaseUnit, declaredAt,
                    declared.Source!, declared.Version);
                return true;
            }

            if (!TryParseDecimal(declared.MajorUnitsPerWholeUnit, out var wholeUnitPrice))
            {
                error = "The declared price is not a valid decimal number.";
                return false;
            }

            if (declared.AssetDecimals is not { } assetDecimals)
            {
                error = "A whole-unit price needs the unit's decimals before it can be converted.";
                return false;
            }

            rate = TenantDeclaredRate.FromWholeUnitPrice(
                chainType, assetId, currencyCode, wholeUnitPrice, assetDecimals,
                declared.CurrencyMinorUnitDigits, declaredAt, declared.Source!, declared.Version);
            return true;
        }
        catch (ArgumentException exception)
        {
            // TenantDeclaredRate refuses a price it cannot hold exactly, a negative rate and a
            // blank component. Those are consumer input errors, not node faults.
            error = exception.Message;
            return false;
        }
    }

    private static bool TryParseDecimal(string? text, out decimal value) =>
        decimal.TryParse(
            text,
            NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out value);
}
