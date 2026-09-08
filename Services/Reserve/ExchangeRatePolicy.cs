// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
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
/// <param name="TenantId">Tenant driving the exchange; names the project treasury a sell is paid from.</param>
/// <param name="ProjectReference">The tenant's own project id; the other half of the treasury reference.</param>
/// <param name="SellerUserId">The person selling. Route-derived by the caller, never body-supplied.</param>
/// <param name="RequestKey">The exchange's idempotency key — the buy-back queue's replay anchor.</param>
/// <param name="IsPreview">True while the caller is only quoting; a preview must never join a queue.</param>
/// <remarks>
/// The five trailing values are the buy-back context (W8.2). They are optional ONLY so the
/// record stays source-compatible with the call site that predates them; a liquidity policy
/// that cannot see the treasury a sell would be paid from REFUSES rather than guessing, so an
/// omitted context fails closed. See <see cref="HasProjectContext"/>.
/// </remarks>
public sealed record ExchangeRateEvaluation(
    string ChainType,
    ExchangeDirection Direction,
    string ProjectAssetId,
    string PlatformAssetId,
    BigInteger PlatformAmount,
    BigInteger ProjectUnitAmount,
    TenantDeclaredRate? DeclaredRate,
    DateTimeOffset Now,
    Guid? TenantId = null,
    string? ProjectReference = null,
    Guid? SellerUserId = null,
    string? RequestKey = null,
    bool IsPreview = false)
{
    /// <summary>True when this evaluation names a project treasury a buy-back could be paid from.</summary>
    public bool HasProjectContext =>
        TenantId is { } tenantId
        && tenantId != Guid.Empty
        && !string.IsNullOrWhiteSpace(ProjectReference)
        && !string.IsNullOrWhiteSpace(ChainType)
        && !string.IsNullOrWhiteSpace(PlatformAssetId);
}

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
/// <see cref="BuyBackLiquidityPolicy"/> implements it (W8.2), and it is registered only by
/// <see cref="BuyBackLiquidityRegistration.AddBuyBackLiquidityPolicy"/>.
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
                // The liquidity policy's own wording and code survive: "you are third in line"
                // and "this project is not buying back" are different answers, and collapsing
                // them into one message is how a queue becomes indistinguishable from a refusal.
                return AZOAResult<ExchangeRateDecision>.FailureWithCode(
                    allowed.IsError && !string.IsNullOrWhiteSpace(allowed.Message)
                        ? allowed.Message
                        : SellUnavailableMessage,
                    allowed.Code ?? AzoaErrorCodes.OperationNotAllowed);
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

// ─── W8.2: the buy-back liquidity policy ─────────────────────────────────────
// A sell is paid from the PROJECT'S OWN treasury account (W3.7), because the disclosure
// "sell requests are filled from the project's treasury" is only true if the project's
// treasury is what actually pays. The platform reserve is the backstop, and it is capped by
// a named partition on the reserve ledger rather than by a sentence. When neither can cover a
// request, the seller joins a durable queue and is told their place — never a bare refusal,
// which is the answer that teaches a person there is a market they have to understand.

/// <summary>Operator configuration for the buy-back — the <c>Exchange:BuyBack</c> section.</summary>
public sealed class BuyBackOptions
{
    public const string SectionName = "Exchange:BuyBack";

    /// <summary>
    /// Whether this node buys project units back at all. FALSE by default: a node that has not
    /// been configured with a buy-back policy must behave exactly as a node with no policy
    /// registered, so enabling the feature is always a deliberate operator act.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Hard per-request ceiling in platform base units; empty means no absolute ceiling. A
    /// request above it is refused for its SHAPE and never queued — queuing a request that can
    /// never become admissible is a promise the node cannot keep.
    /// </summary>
    public string MaxUnitsPerRequest { get; set; } = string.Empty;

    /// <summary>
    /// The most of a project's treasury balance one request may take, as a percentage. Bounds
    /// how fast a single holder can drain the funds a project still has to operate on.
    /// </summary>
    public decimal MaxRequestShareOfTreasuryPercent { get; set; } = 25m;

    /// <summary>
    /// Platform base units the treasury must still hold after a buy-back. This is the
    /// cumulative per-project cap: it bounds total drain without needing a rolling window.
    /// </summary>
    public string TreasuryFloorBaseUnits { get; set; } = "0";

    /// <summary>Whether the platform reserve may back a project's shortfall at all.</summary>
    public bool BackstopEnabled { get; set; } = true;

    /// <summary>
    /// Named partition on the reserve ledger the backstop draws against. Its declared amount is
    /// the cap: the node refuses to promise more backing than the operator declared.
    /// </summary>
    public string BackstopPartition { get; set; } = "buyback-backstop";

    /// <summary>Ceiling on one request's backstop, in platform base units; empty means the partition alone caps it.</summary>
    public string MaxBackstopUnitsPerRequest { get; set; } = string.Empty;

    /// <summary>Whether a short request joins the queue instead of being refused outright.</summary>
    public bool QueueEnabled { get; set; } = true;

    /// <summary>
    /// How long an unfilled queue entry keeps its place. A seller who never returns must stop
    /// blocking the people behind them, so a place in line expires rather than persisting.
    /// </summary>
    public TimeSpan QueueEntryTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Most open entries one project's queue may hold before further requests are refused.</summary>
    public int MaxQueueDepthPerProject { get; set; } = 250;

    /// <summary>Parses a base-unit ceiling; null when unset, and a refusal when it is unreadable.</summary>
    public static bool TryParseBaseUnits(string? text, out BigInteger? amount, out string error)
    {
        amount = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
            return true;

        if (!BigInteger.TryParse(
                text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < BigInteger.Zero)
        {
            error = "A buy-back base-unit setting must be a whole, non-negative number.";
            return false;
        }

        amount = parsed;
        return true;
    }
}

/// <summary>What the caps arithmetic concluded about one buy-back request.</summary>
public enum BuyBackCapVerdict
{
    /// <summary>The treasury can pay it now, within every cap.</summary>
    Fillable = 0,

    /// <summary>The request is larger than one request may ever be. Queuing it would never help.</summary>
    AbovePerRequestCeiling = 1,

    /// <summary>Within the caps, but the funds available now do not reach it.</summary>
    Short = 2,
}

/// <summary>The caps arithmetic's answer, in base units.</summary>
/// <param name="Verdict">What the caps concluded.</param>
/// <param name="Allowance">The most this request could be paid right now, after every cap.</param>
/// <param name="Reserved">Base units already promised to sellers ahead of this one.</param>
/// <param name="Shortfall">Base units missing before this request could be filled; zero when fillable.</param>
public sealed record BuyBackCapOutcome(
    BuyBackCapVerdict Verdict,
    BigInteger Allowance,
    BigInteger Reserved,
    BigInteger Shortfall);

/// <summary>
/// The buy-back caps, as pure arithmetic over base units. Separated from the service so the
/// policy's numbers can be reasoned about (and tested) without a container, a chain or a store.
/// </summary>
public static class BuyBackCaps
{
    /// <summary>
    /// Decides what one request may be paid. <paramref name="reserved"/> is what sellers ahead
    /// in the queue are owed: it is subtracted first, so being early in line actually means
    /// being paid first rather than being a position number with no consequence.
    /// </summary>
    public static BuyBackCapOutcome Decide(
        BigInteger requested,
        BigInteger treasuryBalance,
        BigInteger treasuryHeld,
        BigInteger floor,
        BigInteger reserved,
        BigInteger? maxUnitsPerRequest,
        decimal maxRequestShareOfTreasuryPercent)
    {
        if (maxUnitsPerRequest is { } ceiling && requested > ceiling)
        {
            return new BuyBackCapOutcome(
                BuyBackCapVerdict.AbovePerRequestCeiling, ceiling, reserved, BigInteger.Zero);
        }

        // Units already committed out of this treasury are not available to pay a seller, and
        // neither is the floor the project keeps to operate on.
        var spendable = treasuryBalance - treasuryHeld - floor;
        if (spendable < BigInteger.Zero)
            spendable = BigInteger.Zero;

        var allowance = BigInteger.Min(
            spendable, ShareOf(treasuryBalance, maxRequestShareOfTreasuryPercent));
        if (maxUnitsPerRequest is { } cap)
            allowance = BigInteger.Min(allowance, cap);

        var needed = requested + reserved;
        return needed <= allowance
            ? new BuyBackCapOutcome(BuyBackCapVerdict.Fillable, allowance, reserved, BigInteger.Zero)
            : new BuyBackCapOutcome(BuyBackCapVerdict.Short, allowance, reserved, needed - allowance);
    }

    /// <summary>
    /// A percentage of a base-unit balance, rounded DOWN. A percentage at or above 100 is the
    /// whole balance; a non-positive one pays nothing, which is the fail-closed reading of a
    /// misconfigured cap rather than "no limit".
    /// </summary>
    public static BigInteger ShareOf(BigInteger balance, decimal percent)
    {
        if (balance <= BigInteger.Zero || percent <= 0m)
            return BigInteger.Zero;
        if (percent >= 100m)
            return balance;

        // Whole-number arithmetic on the scaled percentage: casting a BigInteger balance to
        // decimal would silently lose precision on a balance decimal cannot hold.
        var basisPoints = new BigInteger(decimal.Truncate(percent * 100m));
        return balance * basisPoints / 10_000;
    }
}

/// <summary>Names one project's buy-back queue.</summary>
public sealed record BuyBackQueueScope(string ChainType, Guid TenantId, string ProjectReference)
{
    /// <summary>Canonical tenant id as stored on a queue row.</summary>
    public string TenantIdText => TenantId.ToString("N");

    /// <summary>Trimmed project id; the tenant's vocabulary, never parsed further.</summary>
    public string NormalizedProjectReference => ProjectReference?.Trim() ?? string.Empty;

    /// <summary>Trimmed chain name.</summary>
    public string NormalizedChainType => ChainType?.Trim() ?? string.Empty;
}

/// <summary>One seller's place in a project's buy-back queue.</summary>
/// <param name="Scope">The project whose funds would pay.</param>
/// <param name="PlatformAssetId">The unit the seller is owed.</param>
/// <param name="SellerUserId">The person waiting, when the caller named one.</param>
/// <param name="PlatformAmount">Platform base units this seller is owed when their turn comes.</param>
/// <param name="RequestKey">The exchange's idempotency key; one place per request, so a retry re-reads its own.</param>
/// <param name="EnqueuedAt">When the place was taken — the only thing that decides the order.</param>
public sealed record BuyBackQueueEntry(
    BuyBackQueueScope Scope,
    string PlatformAssetId,
    Guid? SellerUserId,
    BigInteger PlatformAmount,
    string RequestKey,
    DateTimeOffset EnqueuedAt);

/// <summary>
/// The durable queue a seller joins when a project's treasury is short. It is what makes "we
/// will fill this in turn" a fact the node acts on rather than a sentence in a refusal.
/// </summary>
public interface IBuyBackQueue
{
    /// <summary>Open places for one project, oldest first, with expired ones already retired.</summary>
    Task<AZOAResult<IReadOnlyList<BuyBackQueueEntry>>> ListOpenAsync(
        BuyBackQueueScope scope, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Records a place in line. Idempotent on the request key: a retry keeps its original place.</summary>
    Task<AZOAResult<BuyBackQueueEntry>> EnqueueAsync(
        BuyBackQueueEntry entry, CancellationToken ct = default);

    /// <summary>Retires one place in line — because it was filled, or because it expired.</summary>
    Task<AZOAResult<bool>> CloseAsync(
        BuyBackQueueScope scope, string requestKey, string reason, CancellationToken ct = default);
}

/// <summary>
/// Holon-backed buy-back queue. Places live as holons, exactly as declared rates do, so the
/// queue needs no schema of its own and survives a restart.
/// </summary>
public sealed class HolonBuyBackQueue : IBuyBackQueue
{
    /// <summary>Holon asset type every queue row carries. Never reused by another lane.</summary>
    public const string QueueAssetType = "AzoaBuyBackQueue";

    /// <summary>Provider name stamped on a queue row.</summary>
    public const string ProviderName = "azoa.buyback";

    private static class MetadataKeys
    {
        public const string TenantId = "tenantId";
        public const string ProjectReference = "projectReference";
        public const string PlatformAssetId = "platformAssetId";
        public const string SellerUserId = "sellerUserId";
        public const string PlatformAmount = "platformAmountBaseUnits";
        public const string RequestKey = "requestKey";
        public const string EnqueuedAt = "enqueuedAt";
        public const string ClosedReason = "closedReason";
    }

    private readonly IHolonStore _holons;
    private readonly BuyBackOptions _options;
    private readonly ILogger<HolonBuyBackQueue>? _logger;

    public HolonBuyBackQueue(
        IHolonStore holons, BuyBackOptions options, ILogger<HolonBuyBackQueue>? logger = null)
    {
        _holons = holons ?? throw new ArgumentNullException(nameof(holons));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AZOAResult<IReadOnlyList<BuyBackQueueEntry>>> ListOpenAsync(
        BuyBackQueueScope scope, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var rows = await _holons.QueryAsync(
            new HolonQueryRequest
            {
                AssetType = QueueAssetType,
                ChainId = scope.NormalizedChainType,
                ProviderName = ProviderName,
                IsActive = true,
            },
            ct);

        if (rows.IsError || rows.Result is null)
        {
            // A queue that cannot be read is not an empty queue. Reporting it as empty would
            // let a later seller step in front of every place already taken.
            return AZOAResult<IReadOnlyList<BuyBackQueueEntry>>.FailureWithCode(
                rows.Message, ConsumerSafeErrorCodes.TemporarilyUnavailable);
        }

        var open = new List<BuyBackQueueEntry>();
        var expired = new List<IHolon>();

        foreach (var row in rows.Result)
        {
            var entry = TryRead(row, scope);
            if (entry is null)
                continue;

            if (_options.QueueEntryTtl > TimeSpan.Zero && now - entry.EnqueuedAt > _options.QueueEntryTtl)
            {
                expired.Add(row);
                continue;
            }

            open.Add(entry);
        }

        foreach (var row in expired)
            await DeactivateAsync(row, "expired", ct);

        open.Sort(static (left, right) =>
        {
            var byTime = left.EnqueuedAt.CompareTo(right.EnqueuedAt);

            // A stable tie-break keeps two places taken in the same instant in a fixed order,
            // so a position number does not shuffle between two reads.
            return byTime != 0 ? byTime : string.CompareOrdinal(left.RequestKey, right.RequestKey);
        });

        return AZOAResult<IReadOnlyList<BuyBackQueueEntry>>.Success(open, "Open buy-back places listed.");
    }

    /// <inheritdoc />
    public async Task<AZOAResult<BuyBackQueueEntry>> EnqueueAsync(
        BuyBackQueueEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(entry.RequestKey))
        {
            return AZOAResult<BuyBackQueueEntry>.FailureWithCode(
                "A place in the buy-back queue needs the request it belongs to.",
                AzoaErrorCodes.InvalidRequest);
        }

        var id = RecordIdFor(entry.Scope, entry.RequestKey);
        var existing = await _holons.GetByIdAsync(id, ct);
        if (!existing.IsError && existing.Result is { IsActive: true })
        {
            var kept = TryRead(existing.Result, entry.Scope);
            if (kept is not null)
            {
                // A retry of the same request keeps its ORIGINAL place. Re-stamping it would
                // send a seller to the back of the line every time they tried again.
                return AZOAResult<BuyBackQueueEntry>.Success(kept, "Place in the queue kept.");
            }
        }

        var row = new Holon
        {
            Id = id,
            Name = NameFor(entry.Scope, entry.RequestKey),
            Description = "A seller waiting for a project's funds to cover their request.",
            ProviderName = ProviderName,
            ChainId = entry.Scope.NormalizedChainType,
            AssetType = QueueAssetType,
            TokenId = entry.PlatformAssetId,
            UserId = entry.SellerUserId,
            IsActive = true,
            ModifiedDate = DateTime.UtcNow,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MetadataKeys.TenantId] = entry.Scope.TenantIdText,
                [MetadataKeys.ProjectReference] = entry.Scope.NormalizedProjectReference,
                [MetadataKeys.PlatformAssetId] = entry.PlatformAssetId,
                [MetadataKeys.SellerUserId] = entry.SellerUserId?.ToString("N") ?? string.Empty,
                [MetadataKeys.PlatformAmount] = entry.PlatformAmount.ToString(CultureInfo.InvariantCulture),
                [MetadataKeys.RequestKey] = entry.RequestKey,
                [MetadataKeys.EnqueuedAt] = entry.EnqueuedAt.ToString("O", CultureInfo.InvariantCulture),
            },
        };

        var written = await _holons.UpsertAsync(row, ct);
        return written.IsError
            ? AZOAResult<BuyBackQueueEntry>.FailureWithCode(
                written.Message, ConsumerSafeErrorCodes.TemporarilyUnavailable)
            : AZOAResult<BuyBackQueueEntry>.Success(entry, "Place in the queue recorded.");
    }

    /// <inheritdoc />
    public async Task<AZOAResult<bool>> CloseAsync(
        BuyBackQueueScope scope, string requestKey, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (string.IsNullOrWhiteSpace(requestKey))
            return AZOAResult<bool>.Success(false, "No place in the queue to retire.");

        var existing = await _holons.GetByIdAsync(RecordIdFor(scope, requestKey), ct);
        if (existing.IsError || existing.Result is null || !existing.Result.IsActive)
            return AZOAResult<bool>.Success(false, "No open place in the queue for this request.");

        return await DeactivateAsync(existing.Result, reason, ct);
    }

    private async Task<AZOAResult<bool>> DeactivateAsync(IHolon row, string reason, CancellationToken ct)
    {
        row.IsActive = false;
        row.ModifiedDate = DateTime.UtcNow;
        row.Metadata[MetadataKeys.ClosedReason] = reason;

        var written = await _holons.UpsertAsync(row, ct);
        if (written.IsError)
        {
            _logger?.LogWarning(
                "Could not retire buy-back queue row {RowId}: {Message}", row.Id, written.Message);
            return AZOAResult<bool>.Failure(written.Message);
        }

        return AZOAResult<bool>.Success(true, "Place in the queue retired.");
    }

    /// <summary>The place a row represents, or null when it belongs to another project or cannot be read.</summary>
    private BuyBackQueueEntry? TryRead(IHolon row, BuyBackQueueScope scope)
    {
        var metadata = row.Metadata;
        if (!metadata.TryGetValue(MetadataKeys.TenantId, out var tenantText)
            || !string.Equals(tenantText, scope.TenantIdText, StringComparison.OrdinalIgnoreCase)
            || !metadata.TryGetValue(MetadataKeys.ProjectReference, out var projectReference)
            || !string.Equals(projectReference, scope.NormalizedProjectReference, StringComparison.Ordinal))
        {
            return null;
        }

        if (!metadata.TryGetValue(MetadataKeys.RequestKey, out var requestKey)
            || string.IsNullOrWhiteSpace(requestKey)
            || !metadata.TryGetValue(MetadataKeys.PlatformAmount, out var amountText)
            || !BigInteger.TryParse(
                amountText, NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
            || !metadata.TryGetValue(MetadataKeys.EnqueuedAt, out var enqueuedAtText)
            || !DateTimeOffset.TryParse(
                enqueuedAtText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var enqueuedAt))
        {
            _logger?.LogWarning(
                "Ignoring an unreadable buy-back queue row {RowId}; it holds nobody's place.", row.Id);
            return null;
        }

        metadata.TryGetValue(MetadataKeys.PlatformAssetId, out var platformAssetId);
        metadata.TryGetValue(MetadataKeys.SellerUserId, out var sellerText);

        return new BuyBackQueueEntry(
            scope,
            string.IsNullOrWhiteSpace(platformAssetId) ? row.TokenId ?? string.Empty : platformAssetId,
            Guid.TryParse(sellerText, out var sellerUserId) ? sellerUserId : row.UserId,
            amount,
            requestKey,
            enqueuedAt);
    }

    private static string NameFor(BuyBackQueueScope scope, string requestKey) =>
        $"buyback-queue:{scope.NormalizedChainType.ToUpperInvariant()}:{scope.TenantIdText}:"
        + $"{scope.NormalizedProjectReference}:{requestKey}";

    /// <summary>
    /// One deterministic id per (chain, tenant, project, request), so a retry addresses the
    /// exact place it created and cannot take a second one.
    /// </summary>
    private static Guid RecordIdFor(BuyBackQueueScope scope, string requestKey)
    {
        var digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"azoa:buyback-queue:v1:{scope.NormalizedChainType.ToUpperInvariant()}:"
            + $"{scope.TenantIdText}:{scope.NormalizedProjectReference}:{requestKey.Trim()}"));
        return new Guid(digest.AsSpan(0, 16));
    }
}

/// <summary>What a backstop top-up actually moved from the platform reserve into a treasury.</summary>
/// <param name="MovedBaseUnits">Platform base units that reached the treasury; zero when nothing moved.</param>
public sealed record BuyBackTopUp(BigInteger MovedBaseUnits);

/// <summary>One request to back a project's shortfall from the platform reserve.</summary>
public sealed record BuyBackTopUpRequest(
    BuyBackQueueScope Scope,
    string PlatformAssetId,
    string Partition,
    BigInteger ShortfallBaseUnits,
    string OperationKey);

/// <summary>
/// The seam that actually moves platform units from the reserve's backstop partition into a
/// project's treasury so a short sell can settle.
/// </summary>
/// <remarks>
/// <b>No implementation is registered on this node — this is a deliberate, fail-closed stub
/// seam.</b> A top-up is a real value movement (a reserve-signed transfer plus a partition
/// draw) and it belongs on the settlement path that already assembles and compensates such
/// moves, not inside an admission check that may run on a preview. Until one is registered,
/// <see cref="BuyBackLiquidityPolicy"/> never admits a sell the treasury cannot itself pay: it
/// reads the declared partition only to decide whether a shortfall may be PROMISED a fill in
/// the queue, and refuses to promise beyond what the operator declared. Unblocked by a
/// reserve-to-treasury transfer on the exchange settlement path.
/// </remarks>
public interface IBuyBackBackstop
{
    /// <summary>Moves up to the shortfall into the treasury; idempotent on the operation key.</summary>
    Task<AZOAResult<BuyBackTopUp>> TopUpAsync(
        BuyBackTopUpRequest request, CancellationToken ct = default);
}

/// <summary>
/// The buy-back liquidity policy: how much of the platform unit a project pays for its own
/// units, drawn from that project's treasury with the platform reserve as a capped backstop.
/// </summary>
/// <remarks>
/// Every refusal here is phrased for a person who has never heard of a market: funds, units,
/// a place in line. The machine-readable answer travels in the result's code.
/// </remarks>
public sealed class BuyBackLiquidityPolicy : IExchangeLiquidityPolicy
{
    /// <summary>Version stamped in logs so a decision can be traced to the policy that made it.</summary>
    public const string Version = "azoa.buyback-liquidity.v1";

    /// <summary>Shown when the operator has not turned the buy-back on for this node.</summary>
    public const string ClosedMessage =
        "This node is not buying units back yet. Nothing was moved.";

    /// <summary>Shown when the caller did not say which project's funds would pay.</summary>
    public const string ContextMissingMessage =
        "This request does not say which project would pay for these units, so it cannot be "
        + "filled. Nothing was moved.";

    /// <summary>Shown when a request is larger than one request may ever be.</summary>
    public const string AbovePerRequestCeilingMessage =
        "That is more than can be sold back in one request. Try a smaller amount; nothing was moved.";

    /// <summary>Shown when the project's funds are short and the seller could not be put in line.</summary>
    public const string ShortWithoutQueueMessage =
        "This project does not have the funds to buy those units back right now. Nothing was moved.";

    /// <summary>Shown when the amount to sell is not a positive whole number of units.</summary>
    public const string NotAPositiveAmountMessage =
        "The amount to sell back must be a positive whole number of units. Nothing was moved.";

    /// <summary>Shown when the project's queue is already at the depth the operator allows.</summary>
    public const string QueueFullMessage =
        "Too many people are already waiting to sell units in this project. Try again later; "
        + "nothing was moved.";

    private readonly BuyBackOptions _options;
    private readonly IProjectTreasury _treasury;
    private readonly IPlatformReserve _reserve;
    private readonly IBuyBackQueue _queue;
    private readonly IBuyBackBackstop? _backstop;
    private readonly ILogger<BuyBackLiquidityPolicy>? _logger;

    /// <param name="backstop">
    /// The reserve-to-treasury top-up. Absent on this node, and its absence is what keeps the
    /// policy from admitting a sell the project's treasury cannot actually pay.
    /// </param>
    public BuyBackLiquidityPolicy(
        BuyBackOptions options,
        IProjectTreasury treasury,
        IPlatformReserve reserve,
        IBuyBackQueue queue,
        IBuyBackBackstop? backstop = null,
        ILogger<BuyBackLiquidityPolicy>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _treasury = treasury ?? throw new ArgumentNullException(nameof(treasury));
        _reserve = reserve ?? throw new ArgumentNullException(nameof(reserve));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _backstop = backstop;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AZOAResult<bool>> EnsureSellAllowedAsync(
        ExchangeRateEvaluation evaluation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        if (evaluation.Direction != ExchangeDirection.Sell)
            return AZOAResult<bool>.Success(true, "Not a buy-back.");

        if (!_options.Enabled)
            return Refuse(ClosedMessage, AzoaErrorCodes.OperationNotAllowed);

        if (!evaluation.HasProjectContext)
        {
            // Fail closed. A liquidity policy that cannot see the treasury a sell would be paid
            // from has no basis for admitting it, and admitting it anyway is the shape of every
            // control in this codebase that turned out to be prose.
            return Refuse(ContextMissingMessage, AzoaErrorCodes.InvalidRequest);
        }

        if (evaluation.PlatformAmount <= BigInteger.Zero)
            return Refuse(NotAPositiveAmountMessage, AzoaErrorCodes.InvalidRequest);

        var capError = string.Empty;
        if (!BuyBackOptions.TryParseBaseUnits(_options.MaxUnitsPerRequest, out var maxPerRequest, out capError)
            || !BuyBackOptions.TryParseBaseUnits(_options.TreasuryFloorBaseUnits, out var floor, out capError)
            || !BuyBackOptions.TryParseBaseUnits(
                _options.MaxBackstopUnitsPerRequest, out var maxBackstop, out capError))
        {
            // An unreadable cap is not an absent cap. Refusing is the only reading that cannot
            // pay out more than the operator meant to allow.
            _logger?.LogError("The buy-back configuration is unreadable: {Error}", capError);
            return Refuse(ClosedMessage, AzoaErrorCodes.PolicyUnavailable);
        }

        var scope = new BuyBackQueueScope(
            evaluation.ChainType, evaluation.TenantId!.Value, evaluation.ProjectReference!);
        var reference = new ProjectTreasuryReference(
            scope.NormalizedChainType, scope.TenantId, scope.NormalizedProjectReference);

        var account = await _treasury.FindAsync(reference, ct);
        if (account.IsError || account.Result is null)
            return Refuse(ShortWithoutQueueMessage, ConsumerSafeErrorCodes.InsufficientFunds);

        if (account.Result.Status != ProjectTreasuryStatus.Open)
            return Refuse(ClosedMessage, AzoaErrorCodes.OperationNotAllowed);

        if (await ReadTreasuryBalanceAsync(reference, evaluation.PlatformAssetId, ct) is not { } treasuryBalance)
            return Refuse(ShortWithoutQueueMessage, ConsumerSafeErrorCodes.TemporarilyUnavailable);

        if (await ReadTreasuryHeldAsync(reference, evaluation.PlatformAssetId, ct) is not { } treasuryHeld)
            return Refuse(ShortWithoutQueueMessage, ConsumerSafeErrorCodes.TemporarilyUnavailable);

        var queue = await _queue.ListOpenAsync(scope, evaluation.Now, ct);
        if (queue.IsError || queue.Result is null)
            return Refuse(ShortWithoutQueueMessage, ConsumerSafeErrorCodes.TemporarilyUnavailable);

        var places = queue.Result;
        var mine = places.FirstOrDefault(place =>
            !string.IsNullOrWhiteSpace(evaluation.RequestKey)
            && string.Equals(place.RequestKey, evaluation.RequestKey, StringComparison.Ordinal));

        // Everyone ahead of this seller is paid first: their claim is subtracted from what this
        // request may take, which is what makes a place in line mean something.
        var myPlaceKey = mine?.RequestKey;
        var ahead = myPlaceKey is null
            ? places
            : places
                .TakeWhile(place => !string.Equals(place.RequestKey, myPlaceKey, StringComparison.Ordinal))
                .ToList();
        var reserved = ahead.Aggregate(BigInteger.Zero, (total, place) => total + place.PlatformAmount);

        var outcome = BuyBackCaps.Decide(
            evaluation.PlatformAmount,
            treasuryBalance,
            treasuryHeld,
            floor ?? BigInteger.Zero,
            reserved,
            maxPerRequest,
            _options.MaxRequestShareOfTreasuryPercent);

        if (outcome.Verdict == BuyBackCapVerdict.AbovePerRequestCeiling)
            return Refuse(AbovePerRequestCeilingMessage, AzoaErrorCodes.InvalidRequest);

        if (outcome.Verdict == BuyBackCapVerdict.Fillable)
            return await AdmitAsync(scope, evaluation, ct);

        // ── Short. The reserve backs it only as far as the operator declared. ──
        var backstopRoom = await ReadBackstopRoomAsync(evaluation.PlatformAssetId, scope, ct);
        var backedByReserve = _options.BackstopEnabled
            && backstopRoom >= outcome.Shortfall
            && (maxBackstop is not { } backstopCeiling || outcome.Shortfall <= backstopCeiling);

        if (backedByReserve && _backstop is not null && !evaluation.IsPreview)
        {
            var toppedUp = await TryTopUpAsync(scope, reference, evaluation, outcome, treasuryHeld, floor,
                maxPerRequest, reserved, ct);
            if (toppedUp)
                return await AdmitAsync(scope, evaluation, ct);
        }

        return await QueueAsync(scope, evaluation, places, mine, ahead.Count, backedByReserve, ct);
    }

    /// <summary>
    /// Asks the backstop to cover the shortfall and re-reads the treasury afterwards. The
    /// admission rests on what the treasury holds NOW, never on what a top-up said it moved.
    /// </summary>
    private async Task<bool> TryTopUpAsync(
        BuyBackQueueScope scope,
        ProjectTreasuryReference reference,
        ExchangeRateEvaluation evaluation,
        BuyBackCapOutcome outcome,
        BigInteger treasuryHeld,
        BigInteger? floor,
        BigInteger? maxPerRequest,
        BigInteger reserved,
        CancellationToken ct)
    {
        var topUp = await _backstop!.TopUpAsync(
            new BuyBackTopUpRequest(
                scope,
                evaluation.PlatformAssetId,
                ReservePartitionCodec.Normalize(_options.BackstopPartition),
                outcome.Shortfall,
                BackstopOperationKey(scope, evaluation.RequestKey)),
            ct);

        if (topUp.IsError || topUp.Result is null || topUp.Result.MovedBaseUnits < outcome.Shortfall)
            return false;

        if (await ReadTreasuryBalanceAsync(reference, evaluation.PlatformAssetId, ct) is not { } refreshed)
            return false;

        return BuyBackCaps.Decide(
            evaluation.PlatformAmount,
            refreshed,
            treasuryHeld,
            floor ?? BigInteger.Zero,
            reserved,
            maxPerRequest,
            _options.MaxRequestShareOfTreasuryPercent).Verdict == BuyBackCapVerdict.Fillable;
    }

    /// <summary>Admits the sell and retires this seller's place, if they had one.</summary>
    private async Task<AZOAResult<bool>> AdmitAsync(
        BuyBackQueueScope scope, ExchangeRateEvaluation evaluation, CancellationToken ct)
    {
        if (!evaluation.IsPreview && !string.IsNullOrWhiteSpace(evaluation.RequestKey))
            await _queue.CloseAsync(scope, evaluation.RequestKey!, "filled", ct);

        _logger?.LogInformation(
            "Buy-back admitted for project {ProjectReference} on {ChainType} under {PolicyVersion}.",
            scope.NormalizedProjectReference, scope.NormalizedChainType, Version);

        return AZOAResult<bool>.Success(true, "The project's funds cover this request.");
    }

    /// <summary>Puts the seller in line and tells them where they stand.</summary>
    private async Task<AZOAResult<bool>> QueueAsync(
        BuyBackQueueScope scope,
        ExchangeRateEvaluation evaluation,
        IReadOnlyList<BuyBackQueueEntry> places,
        BuyBackQueueEntry? mine,
        int aheadCount,
        bool backedByReserve,
        CancellationToken ct)
    {
        // A quote must never take a place in line, and a request with no key has nothing to
        // hold a place under: both get the plain answer instead of a promise.
        if (!_options.QueueEnabled || evaluation.IsPreview || string.IsNullOrWhiteSpace(evaluation.RequestKey))
            return Refuse(ShortWithoutQueueMessage, ConsumerSafeErrorCodes.InsufficientFunds);

        if (mine is null && places.Count >= _options.MaxQueueDepthPerProject)
            return Refuse(QueueFullMessage, ConsumerSafeErrorCodes.InsufficientFunds);

        // Everyone ahead of this seller, plus this seller. Counted the same way whether they
        // already had a place or are taking one now, so the number they are told never jumps.
        var position = aheadCount + 1;

        if (mine is null)
        {
            var enqueued = await _queue.EnqueueAsync(
                new BuyBackQueueEntry(
                    scope,
                    evaluation.PlatformAssetId,
                    evaluation.SellerUserId,
                    evaluation.PlatformAmount,
                    evaluation.RequestKey!,
                    evaluation.Now),
                ct);

            if (enqueued.IsError)
                return Refuse(ShortWithoutQueueMessage, ConsumerSafeErrorCodes.InsufficientFunds);
        }

        var message = backedByReserve
            ? "This project's funds are short right now. Your request is recorded and is backed "
              + $"by the platform, so it will be filled in turn — you are number {position} in "
              + "line. Nothing was moved yet."
            : "This project's funds are short right now. Your request is recorded — you are "
              + $"number {position} in line, and it will be filled as the project's funds "
              + "recover. Nothing was moved yet.";

        return AZOAResult<bool>.FailureWithCode(message, ConsumerSafeErrorCodes.InsufficientFunds);
    }

    /// <summary>The treasury's chain balance of the platform unit, or null when it cannot be read.</summary>
    private async Task<BigInteger?> ReadTreasuryBalanceAsync(
        ProjectTreasuryReference reference, string platformAssetId, CancellationToken ct)
    {
        var balance = await _treasury.GetBalanceAsync(reference, platformAssetId, ct);
        if (balance.IsError || string.IsNullOrWhiteSpace(balance.Result))
        {
            _logger?.LogWarning(
                "Could not read the treasury balance for project {ProjectReference}: {Message}",
                reference.NormalizedProjectReference, balance.Message);
            return null;
        }

        return BigInteger.TryParse(
            balance.Result!.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Platform units already committed out of this treasury. A held unit is somebody else's
    /// money already; paying it to a seller would promise it twice.
    /// </summary>
    private async Task<BigInteger?> ReadTreasuryHeldAsync(
        ProjectTreasuryReference reference, string platformAssetId, CancellationToken ct)
    {
        var ledger = await _treasury.GetLedgerAsync(reference, ct);
        if (ledger.IsError || ledger.Result is null)
            return null;

        var assets = ProjectTreasuryAssetCodec.Decode(ledger.Result.Assets, out var decodeError);
        if (assets is null)
        {
            _logger?.LogWarning(
                "Could not read the treasury counters for project {ProjectReference}: {Error}",
                reference.NormalizedProjectReference, decodeError);
            return null;
        }

        return assets.TryGetValue(ProjectTreasuryAssetCodec.Normalize(platformAssetId), out var counters)
            ? counters.Held
            : BigInteger.Zero;
    }

    /// <summary>
    /// What the operator declared the reserve may back buy-backs with, less what it has already
    /// committed. Unreadable means zero: an undeclared backstop backs nothing.
    /// </summary>
    private async Task<BigInteger> ReadBackstopRoomAsync(
        string platformAssetId, BuyBackQueueScope scope, CancellationToken ct)
    {
        if (!_options.BackstopEnabled)
            return BigInteger.Zero;

        var ledger = await _reserve.GetLedgerAsync(scope.NormalizedChainType, platformAssetId, ct);
        if (ledger.IsError || ledger.Result is null)
            return BigInteger.Zero;

        var partitions = ReservePartitionCodec.Decode(ledger.Result.Partitions, out var decodeError);
        if (partitions is null)
        {
            _logger?.LogWarning("Could not read the reserve partitions: {Error}", decodeError);
            return BigInteger.Zero;
        }

        var name = ReservePartitionCodec.Normalize(_options.BackstopPartition);
        return partitions.TryGetValue(name, out var counters) && counters.Available > BigInteger.Zero
            ? counters.Available
            : BigInteger.Zero;
    }

    /// <summary>Replay anchor for one request's top-up, so a retry tops up once.</summary>
    private static string BackstopOperationKey(BuyBackQueueScope scope, string? requestKey) =>
        $"buyback-backstop:{scope.NormalizedChainType}:{scope.TenantIdText}:"
        + $"{scope.NormalizedProjectReference}:{requestKey?.Trim()}";

    private static AZOAResult<bool> Refuse(string message, string code) =>
        AZOAResult<bool>.FailureWithCode(message, code);
}

/// <summary>Registers the buy-back liquidity policy — the one call that lets a user sell.</summary>
/// <remarks>
/// Until this runs, no <see cref="IExchangeLiquidityPolicy"/> is in the container and
/// <see cref="TreasuryRateExchangePolicy"/> refuses every sell. Running it is not by itself an
/// opening: <see cref="BuyBackOptions.Enabled"/> is false unless the operator sets it, so the
/// policy still refuses until a buy-back has actually been configured.
/// </remarks>
public static class BuyBackLiquidityRegistration
{
    /// <summary>Adds the buy-back options, its durable queue and the policy that reads them.</summary>
    public static IServiceCollection AddBuyBackLiquidityPolicy(
        IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration
            .GetSection(BuyBackOptions.SectionName)
            .Get<BuyBackOptions>() ?? new BuyBackOptions();
        services.AddSingleton(options);

        // Scoped: the holon store it writes through is scoped, exactly like the declared-rate store.
        services.AddScoped<IBuyBackQueue, HolonBuyBackQueue>();
        services.AddScoped<IExchangeLiquidityPolicy, BuyBackLiquidityPolicy>();
        return services;
    }
}
