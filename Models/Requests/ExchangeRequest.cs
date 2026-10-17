// SPDX-License-Identifier: UNLICENSED

using System.Text.Json.Serialization;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;

namespace AZOA.WebAPI.Models.Requests;

/// <summary>Which way units move in a treasury-rate exchange (W5.1 / D50).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExchangeDirection
{
    /// <summary>The sponsor spends the platform unit and receives project units.</summary>
    Buy = 0,

    /// <summary>
    /// A holder gives project units back and receives the platform unit. Refused until a
    /// buy-back liquidity policy is registered (W8.2) — see
    /// <c>Services/Reserve/ExchangeRatePolicy.cs</c>.
    /// </summary>
    Sell = 1,
}

/// <summary>
/// The rate the CONSUMER decided, relayed verbatim. AZOA computes no exchange rate of its
/// own (D7/D16): it records the one its consumer declared, with that consumer's own source,
/// version and observation time, or it records nothing.
/// </summary>
/// <remarks>
/// Exactly one of <see cref="MajorUnitsPerWholeUnit"/> and <see cref="MinorUnitsPerBaseUnit"/>
/// must be supplied. Both are carried as strings so JSON binding cannot round a money
/// constant through a binary floating-point conversion; they are parsed with invariant
/// culture, and a price that cannot be represented exactly is REFUSED, never rounded.
/// </remarks>
public sealed class DeclaredExchangeRate
{
    /// <summary>ISO-4217 code the rate converts into. USD only today (D15).</summary>
    public string? CurrencyCode { get; set; } = "USD";

    /// <summary>Price of ONE WHOLE project unit in the currency's major unit (dollars). Needs <see cref="AssetDecimals"/>.</summary>
    public string? MajorUnitsPerWholeUnit { get; set; }

    /// <summary>Minor units of <see cref="CurrencyCode"/> per ONE BASE UNIT of the project asset.</summary>
    public string? MinorUnitsPerBaseUnit { get; set; }

    /// <summary>The project asset's decimals, needed to convert <see cref="MajorUnitsPerWholeUnit"/>.</summary>
    public int? AssetDecimals { get; set; }

    /// <summary>Digits in the currency's minor unit — 2 for USD cents.</summary>
    public int CurrencyMinorUnitDigits { get; set; } = 2;

    /// <summary>When the consumer observed this rate — not when AZOA read or applied it.</summary>
    public DateTimeOffset? DeclaredAt { get; set; }

    /// <summary>The consumer's own identifier for where the rate came from ("ardanova:treasury-rate").</summary>
    public string? Source { get; set; }

    /// <summary>The consumer's version for the rate schedule it belongs to, when it versions one.</summary>
    public string? Version { get; set; }
}

/// <summary>
/// One treasury-rate exchange, with every amount already decided by the consumer. AZOA
/// executes the move; it does not price it.
/// </summary>
/// <remarks>
/// The person the exchange is for is NEVER on this body — it is the route value, so no
/// field can redirect a sponsorship to a different account (the IDOR rule this codebase
/// applies to every owned-resource route).
/// </remarks>
public sealed class ExchangeRequest
{
    /// <summary>The rail both legs settle on. They are one atomic group, so there is exactly one.</summary>
    public string ChainType { get; set; } = string.Empty;

    /// <summary>Which way units move.</summary>
    public ExchangeDirection Direction { get; set; } = ExchangeDirection.Buy;

    /// <summary>The consumer's own project id. Names the project treasury the funds land in (W3.7).</summary>
    public string ProjectReference { get; set; } = string.Empty;

    /// <summary>The project's asset — the units the sponsor receives.</summary>
    public string ProjectAssetId { get; set; } = string.Empty;

    /// <summary>Platform-unit amount, in base units, as a whole decimal string. The funds leg.</summary>
    public string PlatformAmount { get; set; } = string.Empty;

    /// <summary>Project-unit amount, in base units, as a whole decimal string. The units leg.</summary>
    public string ProjectUnitAmount { get; set; } = string.Empty;

    /// <summary>
    /// Named partition on the reserve ledger the project units are drawn from (W3.1). Required:
    /// without it one purpose could quietly draw down another purpose's earmark.
    /// </summary>
    public string ReservePartition { get; set; } = string.Empty;

    /// <summary>The rate this exchange was priced at. Required unless the policy says otherwise.</summary>
    public DeclaredExchangeRate? DeclaredRate { get; set; }

    /// <summary>
    /// The hold window. When present the project units are COMMITTED to a hold for the
    /// sponsor and become theirs at this instant; when absent they move straight to them.
    /// </summary>
    public DateTimeOffset? ReleaseNotBefore { get; set; }

    /// <summary>Optional consumer-supplied hold id; derived from the exchange when absent.</summary>
    public string? HoldReference { get; set; }

    /// <summary>The consumer's own reason, carried onto the durable record.</summary>
    public string? Purpose { get; set; }

    /// <summary>
    /// Runs every gate and validation and then STOPS: no idempotency claim, no partition
    /// draw, no movement, and an empty operation id as the machine-checkable proof.
    /// </summary>
    public bool PreviewOnly { get; set; }

    /// <summary>Requested automatic release of a held purchase: deadline and independent cost caps. A preview returns its disclosure.</summary>
    public ExchangeReleaseMandateRequest? ReleaseMandate { get; set; }

    /// <summary>Explicit acceptance of the exact release disclosure a preview returned; required for automatic release.</summary>
    public ExchangeReleaseAcceptance? ReleaseAcceptance { get; set; }
}

/// <summary>Owner-chosen automatic-release deadline and separately capped native cost categories, in base units.</summary>
public sealed class ExchangeReleaseMandateRequest
{
    /// <summary>Instant after which the release permission lapses; must follow the hold window.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Most the reserve may spend on the release's network fee.</summary>
    public string MaximumNetworkFeeBaseUnits { get; set; } = "0";

    /// <summary>Most the reserve may lock as account minimum balance for the release.</summary>
    public string MaximumLockedBalanceBaseUnits { get; set; } = "0";

    /// <summary>Most the reserve may spend on nonrefundable protocol costs for the release.</summary>
    public string MaximumNonrefundableCostBaseUnits { get; set; } = "0";
}

/// <summary>Echo of the server-authored release disclosure the caller explicitly accepts.</summary>
public sealed class ExchangeReleaseAcceptance
{
    /// <summary>Caller's stable identity for this acceptance; a revised disclosure needs a new one.</summary>
    public Guid RequestId { get; set; }

    /// <summary>The exact disclosure returned by the preview.</summary>
    public ExchangeReleaseDisclosure? Disclosure { get; set; }
}

/// <summary>Immutable request-derived and admitted-rate terms bound into an exchange source.</summary>
public sealed record ExchangeSourceTerms(int Version, string RequestJson, string PolicyVersion, string? Rate,
    string? RateCurrencyCode, string? RateSource, string? RateVersion, DateTimeOffset? RateAsOf, string? ValueMinorUnits);

/// <summary>
/// Claims-derived caller identity for an exchange. Constructed by the controller from the
/// authenticated principal and the route; it is never bound from an HTTP body.
/// </summary>
public sealed class ExchangeCallerContext
{
    /// <summary>The authenticated API-key record id. Partitions the idempotency key.</summary>
    public Guid ApiKeyId { get; init; }

    /// <summary>The authenticated user that owns the API key.</summary>
    public Guid CallerUserId { get; init; }

    /// <summary>The tenant this exchange is driven by, resolved from the principal alone.</summary>
    public Guid ActingTenantId { get; init; }

    /// <summary>The person the exchange is for. Route-supplied, never body-supplied.</summary>
    public Guid TargetUserId { get; init; }

    /// <summary>The required client-supplied idempotency key.</summary>
    public string ClientIdempotencyKey { get; init; } = string.Empty;

    /// <summary>The authenticated API-key credential captured at the edge; required for any effect.</summary>
    public SigningCredentialWitness? Credential { get; init; }
}

/// <summary>
/// Claims-derived caller context for an exchange receipt read. Constructed by the
/// controller; never bound from a body.
/// </summary>
public sealed class ExchangeReceiptQuery
{
    /// <summary>The authenticated API-key record id the original exchange was claimed under.</summary>
    public Guid ApiKeyId { get; init; }

    /// <summary>The authenticated user that owns the API key.</summary>
    public Guid CallerUserId { get; init; }

    /// <summary>The client idempotency key the exchange was submitted with.</summary>
    public string ClientIdempotencyKey { get; init; } = string.Empty;
}
