// SPDX-License-Identifier: UNLICENSED

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Fiat;
using AZOA.WebAPI.Services.Fiat.OffRamp;
using AZOA.WebAPI.Services.Kyb;

// The credit-capability probe reads the reserve ledger and the on-ramp's own options.
// Aliased rather than imported wholesale so this file's type names stay unambiguous.
using IPlatformReserve = AZOA.WebAPI.Services.Reserve.IPlatformReserve;
using IPlatformTokenGenesis = AZOA.WebAPI.Services.Reserve.IPlatformTokenGenesis;
using OnRampOptions = AZOA.WebAPI.Services.Fiat.OnRamp.OnRampOptions;
using PlatformTokenDefinition = AZOA.WebAPI.Services.Reserve.PlatformTokenDefinition;
using ReservePartitionCodec = AZOA.WebAPI.Services.Reserve.ReservePartitionCodec;

namespace AZOA.WebAPI.Providers.Fiat;

/// <summary>Stable ids for the non-chain value rails this node settles on.</summary>
public static class FiatRailIds
{
    /// <summary>The US-dollar card rail: the on-ramp and (from Wave 8) the off-ramp.</summary>
    public const string UsdRail = "Fiat.USD";

    /// <summary>Hosted-session provider key for the Stripe checkout flow.</summary>
    public const string StripeProviderKey = "stripe";
}

/// <summary>Consumer-facing lifecycle words for one funding intent. Never rail vocabulary.</summary>
public static class FiatIntentStatuses
{
    /// <summary>Admission claimed; the payment page has not been created yet.</summary>
    public const string Starting = "starting";

    /// <summary>The person holds a payment page and has not finished.</summary>
    public const string AwaitingPayment = "awaiting_payment";

    /// <summary>The payment cleared and the node is applying it.</summary>
    public const string Processing = "processing";

    /// <summary>The payment cleared and this intent is closed.</summary>
    public const string Complete = "complete";

    /// <summary>The payment did not complete.</summary>
    public const string Failed = "failed";

    /// <summary>The payment page expired before the person finished.</summary>
    public const string Expired = "expired";

    /// <summary>Maps one durable hosted-session state onto the word a person sees.</summary>
    public static string FromSessionState(string? state) => state switch
    {
        HostedSessionStates.Claimed => Starting,
        HostedSessionStates.AwaitingSubject => AwaitingPayment,
        HostedSessionStates.AwaitingOutcome => Processing,
        HostedSessionStates.Resuming => Processing,
        HostedSessionStates.Completed => Complete,
        HostedSessionStates.Failed => Failed,
        HostedSessionStates.Abandoned => Expired,
        _ => Starting,
    };
}

/// <summary>How far a funding intent has got, in the vocabulary of the person who paid.</summary>
public static class FiatSettlementStates
{
    /// <summary>Nothing has cleared yet.</summary>
    public const string NotStarted = "not_started";

    /// <summary>The payment cleared; the funds have not yet been added to the account.</summary>
    public const string PaymentReceived = "payment_received";

    /// <summary>The funds are in the account.</summary>
    public const string FundsApplied = "funds_applied";

    /// <summary>The payment did not clear.</summary>
    public const string PaymentFailed = "payment_failed";
}

/// <summary>One Stripe environment's secrets and switch. Values are supplied at deploy time.</summary>
public sealed class FiatEnvironmentOptions
{
    /// <summary>Whether this node may settle in this environment at all.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>Stripe secret API key. Host-managed; never committed, never logged.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Stripe webhook signing secret. Without it every delivery fails closed.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Signing secret of the separate Connect endpoint (connected-account events); optional.</summary>
    public string ConnectWebhookSecret { get; set; } = string.Empty;
}

/// <summary>Server-side policy for what a checkout request may ask for.</summary>
public sealed class FiatCheckoutOptions
{
    /// <summary>
    /// Origins a success/cancel URL may point at, as <c>scheme://host[:port]</c>. An empty list
    /// means NO return URL can be accepted, which is why an enabled rail with an empty list is
    /// a boot failure rather than a rail that refuses every request at run time.
    /// </summary>
    public List<string> AllowedReturnOrigins { get; set; } = new();

    /// <summary>
    /// Routing values a caller may name; anything else is refused. Empty by construction: the
    /// configuration binder APPENDS to a pre-populated list, so a seeded default would survive
    /// alongside whatever the operator configured and quietly widen the allowlist.
    /// </summary>
    public List<string> AllowedPurposes { get; set; } = new();

    /// <summary>ISO-4217 currency, lower case, as Stripe expects it.</summary>
    public string Currency { get; set; } = "usd";

    /// <summary>Smallest payment this node will start, in minor units.</summary>
    public long MinimumAmountMinorUnits { get; set; } = 100;

    /// <summary>Largest payment this node will start, in minor units.</summary>
    public long MaximumAmountMinorUnits { get; set; } = 200_000;

    /// <summary>How long the hosted payment page stays usable.</summary>
    public int SessionExpiryMinutes { get; set; } = 30;

    /// <summary>Line-item label the payer sees. Deliberately not a unit or asset name.</summary>
    public string ProductName { get; set; } = "Account funds";

    /// <summary>Return URL used when the caller supplies none. Must satisfy the origin allowlist.</summary>
    public string DefaultSuccessUrl { get; set; } = string.Empty;

    /// <summary>Cancel URL used when the caller supplies none. Must satisfy the origin allowlist.</summary>
    public string DefaultCancelUrl { get; set; } = string.Empty;
}

/// <summary>Allowlisted routing values for a checkout. Adding one is a deliberate act.</summary>
public static class FiatCheckoutPurposes
{
    /// <summary>Adding spendable funds to the payer's own account. The only purpose today.</summary>
    public const string FundsTopUp = "funds-top-up";
}

/// <summary>Node configuration for the <c>Fiat.USD</c> rail. Section <c>Fiat</c>.</summary>
public sealed class FiatOptions
{
    public const string SectionName = "Fiat";

    /// <summary>Master switch. Off ⇒ the rail is registered but every environment is disabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Which environment this node settles in: <c>test</c> or <c>live</c>.</summary>
    public string Environment { get; set; } = RailEnvironmentNames.Test;

    /// <summary>Stripe REST base URL. Overridable so tests can point at a local stub.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.stripe.com";

    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>Upper bound on one Stripe request; release margins downstream assume it (OffRamp AGENTS.md §account-recovery R-2).</summary>
    public const int MaxRequestTimeoutSeconds = 120;

    /// <summary>The configured request timeout, defaulted to 30 s and clamped to 1..<see cref="MaxRequestTimeoutSeconds"/>.</summary>
    public TimeSpan EffectiveRequestTimeout =>
        TimeSpan.FromSeconds(RequestTimeoutSeconds <= 0 ? 30 : Math.Min(RequestTimeoutSeconds, MaxRequestTimeoutSeconds));

    /// <summary>Replay tolerance on the webhook signature timestamp.</summary>
    public int WebhookToleranceSeconds { get; set; } = 300;

    /// <summary>
    /// How long after a cleared card payment a chargeback can still arrive. This is the whole
    /// reason the rail declares a <see cref="SettlementProfile"/>: value issued against a card
    /// payment is not the payee's until this window closes.
    /// </summary>
    public int ChargebackWindowDays { get; set; } = 120;

    public FiatEnvironmentOptions Test { get; set; } = new();

    public FiatEnvironmentOptions Live { get; set; } = new();

    public FiatCheckoutOptions Checkout { get; set; } = new();

    /// <summary>The environment <see cref="Environment"/> names; anything unrecognised is TEST.</summary>
    /// <remarks>
    /// Defaulting an unreadable value to test cannot move real money; a typo that silently meant
    /// "live" could. A value that is neither is rejected at boot by
    /// <see cref="FiatRailStartup.Validate"/>, so this default never hides a misconfiguration.
    /// </remarks>
    public RailEnvironment ActiveEnvironment =>
        string.Equals(Environment?.Trim(), RailEnvironmentNames.Live, StringComparison.OrdinalIgnoreCase)
            ? RailEnvironment.Live
            : RailEnvironment.Test;

    /// <summary>The secrets bound to one environment.</summary>
    public FiatEnvironmentOptions ForEnvironment(RailEnvironment environment) =>
        environment.Kind == RailEnvironmentKind.Live ? Live : Test;

    /// <summary>Credentials for one environment; blank secret ⇒ the gateway refuses.</summary>
    public StripeCredentials CredentialsFor(RailEnvironment environment) => new(
        ApiBaseUrl,
        ForEnvironment(environment).SecretKey,
        EffectiveRequestTimeout);

    /// <summary>
    /// The rail's own environment → endpoint mapping, handed to the factory. A null result means
    /// that environment is switched off for this rail, which is how <c>Fiat:Enabled=false</c>,
    /// an unselected environment, and a missing secret all become the same fail-closed answer.
    /// </summary>
    public BlockchainNetworkConfig? ResolveEndpoint(RailEnvironment environment)
    {
        if (!Enabled)
            return null;

        // ONE environment is live at a time. Binding both would let a test-mode checkout be
        // settled by a live webhook (or the reverse) inside one process.
        if (environment.Kind != ActiveEnvironment.Kind)
            return null;

        var settings = ForEnvironment(environment);
        if (!settings.IsEnabled || string.IsNullOrWhiteSpace(settings.SecretKey))
            return null;

        return new BlockchainNetworkConfig
        {
            NodeUrl = ApiBaseUrl,
            TimeoutMs = (int)EffectiveRequestTimeout.TotalMilliseconds,
            RetryCount = 0,
            IsEnabled = true,
        };
    }

    /// <summary>Configuration key NAMES that are missing for one environment. Never values.</summary>
    public IReadOnlyList<string> MissingKeysFor(RailEnvironment environment)
    {
        var prefix = $"{SectionName}:{(environment.Kind == RailEnvironmentKind.Live ? "Live" : "Test")}";
        var settings = ForEnvironment(environment);
        var missing = new List<string>();

        if (!Enabled)
            missing.Add($"{SectionName}:Enabled");
        if (!settings.IsEnabled)
            missing.Add($"{prefix}:IsEnabled");
        if (string.IsNullOrWhiteSpace(settings.SecretKey))
            missing.Add($"{prefix}:SecretKey");
        if (string.IsNullOrWhiteSpace(settings.WebhookSecret))
            missing.Add($"{prefix}:WebhookSecret");
        if (Checkout.AllowedReturnOrigins.Count == 0)
            missing.Add($"{SectionName}:Checkout:AllowedReturnOrigins");

        return missing;
    }
}

/// <summary>The two names a payment rail gives its environments.</summary>
public static class RailEnvironmentNames
{
    public const string Test = "test";
    public const string Live = "live";
}

/// <summary>What a caller asks the rail for when a person wants to add funds.</summary>
public sealed class FiatCheckoutRequest
{
    /// <summary>The person paying. Resolved from the principal or the route, never the body.</summary>
    public Guid SubjectUserId { get; init; }

    /// <summary>The tenant driving this on the person's behalf, when one is.</summary>
    public Guid? TenantId { get; init; }

    /// <summary>Caller-stable key: the same key resumes one intent instead of opening a second.</summary>
    public string AdmissionKey { get; init; } = string.Empty;

    /// <summary>Allowlisted routing value.</summary>
    public string Purpose { get; init; } = FiatCheckoutPurposes.FundsTopUp;

    /// <summary>The amount of MONEY the person is paying, in minor units. Never a unit count.</summary>
    public long AmountMinorUnits { get; init; }

    /// <summary>Where to send the person when they finish. Origin-allowlisted.</summary>
    public string? SuccessUrl { get; init; }

    /// <summary>Where to send the person if they back out. Origin-allowlisted.</summary>
    public string? CancelUrl { get; init; }
}

/// <summary>Everything the return page needs to recognise the intent it came back to.</summary>
public sealed class FiatCheckoutIntent
{
    /// <summary>The node's own intent id. Safe to show; it is not the payment provider's id.</summary>
    public Guid IntentId { get; init; }

    public string AdmissionKey { get; init; } = string.Empty;

    /// <summary>One of <see cref="FiatIntentStatuses"/>.</summary>
    public string Status { get; init; } = FiatIntentStatuses.Starting;

    /// <summary>One of <see cref="FiatSettlementStates"/>.</summary>
    public string SettlementState { get; init; } = FiatSettlementStates.NotStarted;

    /// <summary>The hosted payment page, while the intent is still open.</summary>
    public string? PaymentUrl { get; init; }

    /// <summary>True when this call resumed an existing intent instead of creating one.</summary>
    public bool Resumed { get; init; }

    /// <summary>The amount that actually cleared, re-read from the provider. Null until it has.</summary>
    public long? AmountPaidMinorUnits { get; init; }

    public string? Currency { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>
/// The fiat on-ramp surface: start a payment for a person, and find that payment again.
/// Deliberately separate from <see cref="IValueRailProvider"/> — it is a hosted-redirect flow,
/// not a value movement, and no chain rail implements it.
/// </summary>
public interface IFiatOnRampRail
{
    /// <summary>Non-secret capability report; names MISSING configuration keys, never values.</summary>
    HostedSessionCapabilities GetCapabilities();

    /// <summary>
    /// Claims a durable intent, then creates the hosted payment page. The claim happens FIRST,
    /// so a crash between the two cannot leave a payment page no callback can be matched to.
    /// </summary>
    Task<AZOAResult<FiatCheckoutIntent>> BeginCheckoutAsync(
        FiatCheckoutRequest request, CancellationToken ct = default);

    /// <summary>
    /// Reads one intent for the person who owns it, as the same driver (tenant or first-party)
    /// that started it. Scoped by subject and driver, never by intent id alone.
    /// </summary>
    Task<AZOAResult<FiatCheckoutIntent>> GetIntentAsync(
        Guid subjectUserId, Guid intentId, Guid? tenantId = null, CancellationToken ct = default);
}

/// <summary>
/// The <c>Fiat.USD</c> value rail: card money in, and (from Wave 8) card money out.
///
/// <para>It is a rail, not a chain. It settles nothing on its own — a card payment is
/// <see cref="SettlementReversibility.ReversibleWithinWindow"/> for the whole chargeback tail —
/// so the only value movements it exposes are the hosted checkout it owns and the reversal
/// evidence it records. <see cref="TransferAsync"/> is refused outright rather than faked.</para>
///
/// <para>See <c>docs/FIAT-RAIL.md</c> for the ported invariants (pricing, auth, webhook
/// mapping, idempotency, errors) this rail inherits from the retired sidecar.</para>
/// </summary>
public sealed class FiatRailProvider : IValueRailProvider, IFiatOnRampRail
{
    /// <summary>Prefix of the claim-bound Stripe idempotency key for a checkout session create.</summary>
    public const string CheckoutIdempotencyKeyPrefix = "azoa-fiat-checkout-";

    /// <summary>Stripe's minimum Checkout Session lifetime (30 min) plus one minute of clock skew.</summary>
    public static readonly TimeSpan StripeMinimumCheckoutLifetime = TimeSpan.FromMinutes(31);

    /// <summary>Lifetime floor: Stripe's minimum plus a replay margin for a lost create response.</summary>
    private const int MinimumPinnedLifetimeMinutes = 35;

    /// <summary>The claim's fixed <c>expires_at</c>: creation start + configured lifetime, clamped to Stripe's 35 min..24 h.</summary>
    public static DateTimeOffset PinnedCheckoutExpiry(DateTimeOffset creationStartedAt, int configuredMinutes)
        => creationStartedAt.AddMinutes(Math.Clamp(configuredMinutes, MinimumPinnedLifetimeMinutes, 24 * 60));

    private readonly FiatOptions _options;
    private readonly IStripeGateway _gateway;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FiatRailProvider> _logger;
    private readonly TimeProvider _clock;

    private RailEnvironment _environment;
    private int _bound;

    public FiatRailProvider(
        FiatOptions options,
        IStripeGateway gateway,
        IServiceScopeFactory scopeFactory,
        ILogger<FiatRailProvider> logger,
        TimeProvider? clock = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
        _environment = options.ActiveEnvironment;
    }

    /// <inheritdoc />
    public string RailId => FiatRailIds.UsdRail;

    /// <inheritdoc />
    public RailEnvironment ActiveEnvironment => _environment;

    /// <inheritdoc />
    /// <remarks>
    /// A card payment can be pulled back for months after it clears. Expressing that as a WINDOW
    /// rather than a finality flag is what lets the hold primitive compute a release time instead
    /// of guessing one — see <c>Core/SettlementProfile.cs</c>.
    /// </remarks>
    public SettlementProfile SettlementProfile => AZOA.WebAPI.Core.SettlementProfile.ReversibleFor(
        TimeSpan.FromDays(_options.ChargebackWindowDays <= 0 ? 120 : _options.ChargebackWindowDays),
        "the card payment clears and the payment session reports complete");

    /// <inheritdoc />
    public void InitializeRail(ValueRailBinding binding)
    {
        // The factory owns this call and caches one instance per (rail, environment). A bound
        // instance that could be re-bound would let a later resolve retarget a cached provider
        // from test to live mid-flight — the same guard BaseBlockchainProvider applies.
        if (Interlocked.Exchange(ref _bound, 1) != 0)
            throw new InvalidOperationException(
                $"Value rail '{RailId}' is already bound to environment '{_environment}' and cannot be rebound.");

        _environment = binding.Environment;
    }

    // ─── Hosted checkout ───

    /// <inheritdoc />
    public HostedSessionCapabilities GetCapabilities()
    {
        var missing = _options.MissingKeysFor(_environment);
        return new HostedSessionCapabilities
        {
            ProviderKey = FiatRailIds.StripeProviderKey,
            FlowKind = HostedSessionFlowKinds.FiatCheckout,
            Available = missing.Count == 0,
            MissingConfigurationKeys = missing,
            UnavailableReason = missing.Count == 0
                ? null
                : "Adding funds is not configured on this node.",
        };
    }

    /// <inheritdoc />
    public async Task<AZOAResult<FiatCheckoutIntent>> BeginCheckoutAsync(
        FiatCheckoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var capabilities = GetCapabilities();
        if (!capabilities.Available)
            return FiatConsumerErrors.PaymentsNotConfigured<FiatCheckoutIntent>();

        if (request.SubjectUserId == Guid.Empty)
            return AZOAResult<FiatCheckoutIntent>.FailureWithCode(
                "An authenticated account is required to add funds.", AzoaErrorCodes.InvalidRequest);
        if (string.IsNullOrWhiteSpace(request.AdmissionKey))
            return AZOAResult<FiatCheckoutIntent>.FailureWithCode(
                "A stable request key is required.", AzoaErrorCodes.InvalidRequest);

        // Sidecar "auth": routing values are checked against a SERVER-SIDE allowlist, so a
        // caller cannot invent a purpose the node has no policy for.
        if (!IsAllowedPurpose(request.Purpose))
            return AZOAResult<FiatCheckoutIntent>.FailureWithCode(
                "That kind of payment is not offered here.", AzoaErrorCodes.InvalidRequest);

        var checkout = _options.Checkout;
        if (request.AmountMinorUnits < checkout.MinimumAmountMinorUnits
            || request.AmountMinorUnits > checkout.MaximumAmountMinorUnits)
        {
            return AZOAResult<FiatCheckoutIntent>.FailureWithCode(
                FormatAmountBounds(checkout), AzoaErrorCodes.InvalidRequest);
        }

        // Sidecar "auth": the return URLs are origin-allowlisted. Without this the payment page is
        // an open redirect a phisher can point back at their own copy of the site — on the one page
        // where a person has just typed their card number. The origin is checked BEFORE the durable
        // claim so a rejected URL leaves no row behind; the per-intent query parameter is appended
        // once the intent id exists.
        if (!FiatReturnUrlPolicy.TryResolveOrigin(
                request.SuccessUrl, checkout.DefaultSuccessUrl, checkout.AllowedReturnOrigins,
                _environment, out var successBase, out var successReason))
        {
            _logger.LogWarning("A checkout success URL was refused: {Reason}", successReason);
            return AZOAResult<FiatCheckoutIntent>.FailureWithCode(
                "That return address is not allowed on this node.", AzoaErrorCodes.InvalidRequest);
        }

        if (!FiatReturnUrlPolicy.TryResolveOrigin(
                request.CancelUrl, checkout.DefaultCancelUrl, checkout.AllowedReturnOrigins,
                _environment, out var cancelBase, out var cancelReason))
        {
            _logger.LogWarning("A checkout cancel URL was refused: {Reason}", cancelReason);
            return AZOAResult<FiatCheckoutIntent>.FailureWithCode(
                "That return address is not allowed on this node.", AzoaErrorCodes.InvalidRequest);
        }

        using var scope = _scopeFactory.CreateScope();

        // D46: every value action needs a current identity approval, and buying funds is a value
        // action. The check runs here — inside the rail — rather than only in the controller, so
        // a second caller of this seam cannot inherit the surface without the gate.
        var topUp = string.Equals(request.Purpose?.Trim(), TenantPayoutTopUp.Purpose, StringComparison.OrdinalIgnoreCase);
        if (topUp)
        {
            // D-8: a tenant float top-up is the tenant's own business money. It needs the tenant's KYB
            // approval for payout pricing (D-6), a server-built top-up admission key and no acting tenant.
            var kyb = scope.ServiceProvider.GetService<IKybGate>();
            if (kyb is null)
                return AZOAResult<FiatCheckoutIntent>.FailureWithCode(
                    "Business verification cannot be checked; the top-up was refused.", AzoaErrorCodes.DependencyUnavailable);
            if (request.TenantId is not null
                || !TenantPayoutTopUp.TryReadTenant(request.AdmissionKey, out var keyTenant) || keyTenant != request.SubjectUserId)
                return AZOAResult<FiatCheckoutIntent>.FailureWithCode(
                    "That kind of payment is not offered here.", AzoaErrorCodes.InvalidRequest);
            var verified = await kyb.RequireVerifiedTenantAsync(request.SubjectUserId, KybCapability.PayoutPriceSchedule, ct);
            if (verified.IsError)
                return AZOAResult<FiatCheckoutIntent>.FailureWithCode(verified.Message, AzoaErrorCodes.Forbidden);
        }
        else
        {
            if (TenantPayoutTopUp.IsTopUpAdmission(request.AdmissionKey))
                return AZOAResult<FiatCheckoutIntent>.FailureWithCode(
                    "That request key is not allowed.", AzoaErrorCodes.InvalidRequest);

            var kycGate = scope.ServiceProvider.GetService<IRealValueKycGate>();
            if (kycGate is null)
                return AZOAResult<FiatCheckoutIntent>.FailureWithCode(
                    "Identity verification cannot be checked; adding funds was refused.",
                    AzoaErrorCodes.DependencyUnavailable);

            var approval = request.TenantId is { } tenantId
                ? await kycGate.RequireCurrentApprovalAsync(request.SubjectUserId, tenantId, ct)
                : await kycGate.RequireCurrentApprovalAsync(request.SubjectUserId, ct);
            if (approval.IsError)
                return AZOAResult<FiatCheckoutIntent>.FailureWithCode(
                    "This account has to finish identity verification before adding funds.",
                    AzoaErrorCodes.Forbidden);
        }

        // OI-29: a registered IFiatSettlementHandler proves a TYPE is in the container, not that
        // this node can credit anything. The two facts that decide creditability live in data --
        // the declared on-ramp partition and the registered price -- so they are proved HERE, at
        // the counter, immediately before a real card is charged, and not only at boot. Nothing
        // durable has been written yet, so a refusal leaves no claimed intent behind.
        if (_environment.Kind == RailEnvironmentKind.Live)
        {
            // request.TenantId, not null: the probe now asks whether the price that would settle
            // THIS payment exists, which is the question the settlement handler will ask an hour
            // later. Passing null here would keep the boot-shaped answer at a per-payment counter.
            var capability = await FiatCreditCapabilityProbe.ProbeAsync(
                scope.ServiceProvider, _clock.GetUtcNow(), request.TenantId, ct);
            if (!capability.CanCredit)
            {
                _logger.LogError(
                    "A live funding checkout was refused because this node cannot credit a cleared "
                    + "payment: {Detail}. {OperatorAction}.",
                    capability.Detail, capability.OperatorAction);
                return FiatConsumerErrors.RailUnavailable<FiatCheckoutIntent>();
            }
        }

        var store = scope.ServiceProvider.GetRequiredService<IHostedSessionStore>();
        var now = _clock.GetUtcNow();

        var candidate = new HostedSession
        {
            Id = Guid.NewGuid().ToString("N"),
            FlowKind = HostedSessionFlowKinds.FiatCheckout,
            ProviderKey = FiatRailIds.StripeProviderKey,
            AdmissionKey = request.AdmissionKey.Trim(),
            CreationToken = Guid.NewGuid().ToString("N"),
            CreationClaimedAt = now,
            SubjectUserId = request.SubjectUserId.ToString("N"),
            TenantId = request.TenantId?.ToString("N"),
            State = HostedSessionStates.Claimed,
            StateVersion = 0,
            CreatedDate = now,
            ModifiedDate = now,
        };

        var claimed = await store.ClaimAdmissionAsync(candidate, ct);
        if (claimed.IsError || claimed.Result is null)
            return FiatConsumerErrors.RailBusy<FiatCheckoutIntent>();

        var session = claimed.Result;
        if (!TryParseIntentId(session.Id, out var sessionId))
        {
            // The row identifier is written by this node as a compact Guid; anything else means
            // the store handed back a shape no transition here can address. Refuse rather than
            // throw past the caller mid-checkout.
            _logger.LogError("A funding intent row carries an unusable identifier.");
            return FiatConsumerErrors.RailBusy<FiatCheckoutIntent>();
        }

        // The admission row is keyed on (flow, provider, admission key) alone, so a second person
        // reusing another person's key would otherwise resume THEIR intent. Ownership is checked
        // against the claim that already exists, not against the request that just arrived.
        if (!OwnedBy(session, request.SubjectUserId) || !DrivenBy(session, request.TenantId))
        {
            _logger.LogWarning(
                "A funding intent was requested with an admission key already claimed by another account.");
            return AZOAResult<FiatCheckoutIntent>.FailureWithCode(
                "That request key is already in use.", AzoaErrorCodes.Conflict);
        }

        if (HostedSessionStates.IsTerminal(session.State))
            return AZOAResult<FiatCheckoutIntent>.FailureWithCode(
                "This payment is already finished.", AzoaErrorCodes.Conflict);

        // A live claim that already carries a page is resumed verbatim: the whole point of the
        // admission key is that a refresh does not open a second payment.
        if (!string.IsNullOrWhiteSpace(session.ProviderSessionId)
            && !string.IsNullOrWhiteSpace(session.RedirectUrl)
            && (session.ExpiresAt is null || session.ExpiresAt > now))
        {
            return AZOAResult<FiatCheckoutIntent>.Success(
                await ProjectAsync(scope, session, resumed: true, ct), "Success");
        }

        // D1 creation fence: exactly one Stripe create per claim incarnation, keyed by its durable
        // creation token; see Providers/Fiat/AGENTS.md §creation-fence.
        var turn = await FiatHostedCreation.BeginOrResumeAsync(store, session, now, ct);
        switch (turn.Verdict)
        {
            case FiatCreationVerdict.Attached:
                return AZOAResult<FiatCheckoutIntent>.FailureWithCode(
                    "This payment page has expired. Start a new payment.", AzoaErrorCodes.Conflict);
            case FiatCreationVerdict.NeedsOperator:
                _logger.LogError(
                    "Funding intent {IntentId} has an unattached payment page the node can no longer replay; operator reconciliation is required.",
                    session.Id);
                return AZOAResult<FiatCheckoutIntent>.FailureWithCode(
                    "This payment needs a manual check before it can continue.", AzoaErrorCodes.Conflict);
            case FiatCreationVerdict.Busy:
                return FiatConsumerErrors.RailBusy<FiatCheckoutIntent>();
        }

        session = turn.Session!;
        // D8: expires_at is pinned to the claim's durable creation start so every replay of the
        // claim-keyed create sends identical parameters; see Providers/Fiat/AGENTS.md §expiry-pin.
        var expiresAt = PinnedCheckoutExpiry(session.CreationStartedAt ?? now, checkout.SessionExpiryMinutes);
        if (expiresAt - now < StripeMinimumCheckoutLifetime)
            return AZOAResult<FiatCheckoutIntent>.FailureWithCode(
                "This payment page has expired. Start a new payment.", AzoaErrorCodes.Conflict);
        var created = await _gateway.CreateCheckoutSessionAsync(
            _options.CredentialsFor(_environment),
            new StripeCheckoutSessionRequest
            {
                IntentId = session.Id,
                AdmissionKey = session.AdmissionKey,
                SubjectUserId = request.SubjectUserId.ToString("N"),
                TenantId = request.TenantId?.ToString("N"),
                Purpose = request.Purpose,
                Currency = checkout.Currency,
                AmountMinorUnits = request.AmountMinorUnits,
                ProductName = checkout.ProductName,
                SuccessUrl = FiatReturnUrlPolicy.AppendIntent(successBase, session.Id),
                CancelUrl = FiatReturnUrlPolicy.AppendIntent(cancelBase, session.Id),
                ExpiresAt = expiresAt,
                // Bound to the claim incarnation, so a replay returns Stripe's existing session.
                IdempotencyKey = FiatHostedCreation.IdempotencyKey(CheckoutIdempotencyKeyPrefix, session),
            },
            ct);

        if (created.IsError || created.Result is null || string.IsNullOrWhiteSpace(created.Result.Url))
        {
            // The claim stays NON-TERMINAL with its fence held: the next attempt replays the same
            // claim-keyed create rather than opening a second page.
            return AZOAResult<FiatCheckoutIntent>.FailureWithCode(
                created.Message, created.Code ?? FiatConsumerErrors.RailBusyCode);
        }

        // Wrong-mode credentials are a configuration fault that would otherwise only surface as
        // a webhook this node rejects, long after the person has paid.
        if (created.Result.Livemode != (_environment.Kind == RailEnvironmentKind.Live))
        {
            _logger.LogError(
                "Stripe created a session whose livemode does not match the bound rail environment {Environment}.",
                _environment);
            return FiatConsumerErrors.RailUnavailable<FiatCheckoutIntent>();
        }

        var attached = await store.TryAttachProviderSessionAsync(
            sessionId,
            session.StateVersion,
            created.Result.SessionId,
            created.Result.PaymentIntentId,
            created.Result.Url!,
            created.Result.ExpiresAt ?? expiresAt,
            session.CreationToken!,
            ct);

        if (attached.IsError || attached.Result is null)
        {
            // A concurrent replay of the same claim attached first; project it only when it carries
            // this very Stripe session.
            var reread = await store.GetByIdAsync(sessionId, ct);
            if (!reread.IsError && reread.Result is not null && !string.IsNullOrWhiteSpace(reread.Result.RedirectUrl)
                && string.Equals(reread.Result.ProviderSessionId, created.Result.SessionId, StringComparison.Ordinal))
                return AZOAResult<FiatCheckoutIntent>.Success(
                    await ProjectAsync(scope, reread.Result, resumed: true, ct), "Success");

            return FiatConsumerErrors.RailBusy<FiatCheckoutIntent>();
        }

        return AZOAResult<FiatCheckoutIntent>.Success(
            await ProjectAsync(scope, attached.Result, resumed: false, ct), "Success");
    }

    /// <inheritdoc />
    public async Task<AZOAResult<FiatCheckoutIntent>> GetIntentAsync(
        Guid subjectUserId, Guid intentId, Guid? tenantId = null, CancellationToken ct = default)
    {
        if (subjectUserId == Guid.Empty || intentId == Guid.Empty)
            return AZOAResult<FiatCheckoutIntent>.FailureWithCode(
                "A payment reference is required.", AzoaErrorCodes.InvalidRequest);

        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IHostedSessionStore>();

        var found = await store.GetByIdAsync(intentId, ct);

        // ONE refusal for "no such intent", "that is another flow's session" and "that intent
        // belongs to somebody else". Distinguishing them would turn this route into an oracle over
        // every hosted session on the node, the KYC ones included.
        if (found.IsError
            || found.Result is null
            || !string.Equals(found.Result.FlowKind, HostedSessionFlowKinds.FiatCheckout, StringComparison.Ordinal)
            || !string.Equals(found.Result.ProviderKey, FiatRailIds.StripeProviderKey, StringComparison.Ordinal)
            || !OwnedBy(found.Result, subjectUserId)
            || !DrivenBy(found.Result, tenantId))
        {
            return AZOAResult<FiatCheckoutIntent>.FailureWithCode(
                "That payment could not be found.", AzoaErrorCodes.NotFound);
        }

        return AZOAResult<FiatCheckoutIntent>.Success(
            await ProjectAsync(scope, found.Result, resumed: true, ct), "Success");
    }

    // ─── IValueRailProvider: read ───

    /// <inheritdoc />
    /// <remarks>
    /// The card rail holds no per-person balance for this node to read; a person's spendable
    /// funds live in their custodial account, not at the payment processor. Answering zero would
    /// be indistinguishable from "you have no money", so this refuses instead.
    /// </remarks>
    public Task<AZOAResult<string>> GetBalanceAsync(
        string address, string? tokenId = null, CancellationToken ct = default)
        => Task.FromResult(AZOAResult<string>.FailureWithCode(
            "This rail does not hold account balances.", AzoaErrorCodes.OperationNotAllowed));

    /// <inheritdoc />
    /// <remarks>
    /// There are no addressable destinations on the card rail today: money leaves it through a
    /// payout arranged per account (Wave 8), not by naming an address. <c>false</c> is the honest
    /// verdict — no address is valid — rather than an error, which would read as an outage.
    /// </remarks>
    public Task<AZOAResult<bool>> ValidateAddressAsync(string address, CancellationToken ct = default)
        => Task.FromResult(AZOAResult<bool>.Success(false, "This rail has no addressable destinations."));

    // ─── IValueRailProvider: move value ───

    /// <inheritdoc />
    /// <remarks>
    /// Refused, always. Money enters this rail through a hosted payment the payer authorises and
    /// leaves it through a payout — never through a node-initiated transfer between two addresses.
    /// A provider that quietly returned a fake reference here would let a caller believe a card
    /// payment had been made.
    /// </remarks>
    public Task<AZOAResult<string>> TransferAsync(
        string tokenId,
        string fromAddress,
        string toAddress,
        RailAmount amount,
        SigningContext? signingContext = null,
        CancellationToken ct = default)
    {
        // The dimension check first, because reinterpreting chain base units as cents (or the
        // reverse) moves a completely different quantity — the failure the amount type exists
        // to make impossible.
        if (!amount.Dimension.IsFiat)
            return Task.FromResult(AZOAResult<string>.FailureWithCode(
                "This rail settles money, and the amount is not denominated in money.",
                AzoaErrorCodes.InvalidRequest));

        return Task.FromResult(AZOAResult<string>.FailureWithCode(
            "This rail does not move funds between accounts.", AzoaErrorCodes.OperationNotAllowed));
    }

    // ─── IValueRailProvider: operation status ───

    /// <inheritdoc />
    /// <remarks><paramref name="txHash"/> is a payment-session id; the facts are re-read server-side.</remarks>
    public async Task<AZOAResult<Dictionary<string, object>>> GetTransactionStatusAsync(
        string txHash, CancellationToken ct = default)
    {
        var session = await _gateway.GetCheckoutSessionAsync(_options.CredentialsFor(_environment), txHash, ct);
        if (session.IsError || session.Result is null)
            return AZOAResult<Dictionary<string, object>>.FailureWithCode(
                session.Message, session.Code ?? FiatConsumerErrors.RailBusyCode);

        // Non-secret facts only: no customer, no card, no receipt URL.
        return AZOAResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
        {
            ["railId"] = RailId,
            ["environment"] = _environment.Name,
            ["sessionStatus"] = session.Result.Status,
            ["paymentStatus"] = session.Result.PaymentStatus,
            ["amountTotalMinorUnits"] = session.Result.AmountTotalMinorUnits ?? 0L,
            ["currency"] = session.Result.Currency ?? _options.Checkout.Currency,
            ["livemode"] = session.Result.Livemode,
        }, "Success");
    }

    /// <inheritdoc />
    public async Task<AZOAResult<ChainConfirmation>> GetTransactionConfirmationAsync(
        string txHash, CancellationToken ct = default)
    {
        var session = await _gateway.GetCheckoutSessionAsync(_options.CredentialsFor(_environment), txHash, ct);
        if (session.IsError || session.Result is null)
        {
            // The rail could not be read. Unknown is the conservative answer the reconciler needs:
            // a payment that cannot be observed is not a payment that failed.
            return AZOAResult<ChainConfirmation>.Success(ChainConfirmation.Unknown, session.Message);
        }

        return AZOAResult<ChainConfirmation>.Success(ClassifySession(session.Result), "Success");
    }

    /// <summary>The landed / failed / in-flight verdict for one payment session.</summary>
    public static ChainConfirmation ClassifySession(StripeCheckoutSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        // "complete" alone is not payment: a completed session whose payment_status is still
        // unpaid is an asynchronous method that has not cleared.
        if (string.Equals(session.Status, "complete", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(session.PaymentStatus, "paid", StringComparison.OrdinalIgnoreCase)
                || string.Equals(session.PaymentStatus, "no_payment_required", StringComparison.OrdinalIgnoreCase)
                    ? ChainConfirmation.Confirmed
                    : ChainConfirmation.Pending;
        }

        if (string.Equals(session.Status, "expired", StringComparison.OrdinalIgnoreCase))
            return ChainConfirmation.FailedOnChain;

        return string.Equals(session.Status, "open", StringComparison.OrdinalIgnoreCase)
            ? ChainConfirmation.Pending
            : ChainConfirmation.Unknown;
    }

    // ─── helpers ───

    private bool IsAllowedPurpose(string? purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose))
            return false;

        var allowed = _options.Checkout.AllowedPurposes;

        // An EMPTY allowlist means "nothing is allowed", not "everything is". The sidecar read an
        // empty list as unrestricted, which turns a forgotten config key into an open dimension.
        return allowed.Count != 0
            && allowed.Any(candidate => string.Equals(candidate?.Trim(), purpose.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reads the compact-Guid row identifier this node writes, without throwing.</summary>
    private static bool TryParseIntentId(string? id, out Guid intentId) =>
        Guid.TryParseExact(id, "N", out intentId) || Guid.TryParse(id, out intentId);

    private static bool OwnedBy(HostedSession session, Guid subjectUserId) =>
        string.Equals(session.SubjectUserId, subjectUserId.ToString("N"), StringComparison.OrdinalIgnoreCase);

    /// <summary>F-H8: the intent was started by <paramref name="tenantId"/> (null = the person themself).</summary>
    private static bool DrivenBy(HostedSession session, Guid? tenantId) =>
        SurrealRecordGuid.ParseOptional(session.TenantId) == tenantId;

    private static string FormatAmountBounds(FiatCheckoutOptions checkout) => string.Format(
        CultureInfo.InvariantCulture,
        "Enter an amount between {0:0.00} and {1:0.00}.",
        checkout.MinimumAmountMinorUnits / 100m,
        checkout.MaximumAmountMinorUnits / 100m);

    /// <summary>
    /// Projects one durable intent, adding the two facts only an external read can supply: what
    /// actually cleared (from the provider) and whether the funds have landed (from the inbox).
    /// </summary>
    private async Task<FiatCheckoutIntent> ProjectAsync(
        IServiceScope scope, HostedSession session, bool resumed, CancellationToken ct)
    {
        long? amountPaid = null;
        string? currency = null;
        var settlement = FiatSettlementStates.NotStarted;

        if (!string.IsNullOrWhiteSpace(session.ProviderSessionId))
        {
            var fetched = await _gateway.GetCheckoutSessionAsync(
                _options.CredentialsFor(_environment), session.ProviderSessionId!, ct);
            if (!fetched.IsError && fetched.Result is not null)
            {
                currency = fetched.Result.Currency;
                var confirmation = ClassifySession(fetched.Result);
                if (confirmation == ChainConfirmation.Confirmed)
                {
                    amountPaid = fetched.Result.AmountTotalMinorUnits;
                    settlement = FiatSettlementStates.PaymentReceived;
                }
                else if (confirmation == ChainConfirmation.FailedOnChain)
                {
                    settlement = FiatSettlementStates.PaymentFailed;
                }
            }
        }

        // "Funds applied" is asserted ONLY by an inbox row that a handler actually applied. With
        // no issuance handler registered this stays "payment received" forever, which is the
        // truth, rather than reading the intent's own terminal state as if it were money.
        if (settlement == FiatSettlementStates.PaymentReceived)
        {
            var inbox = scope.ServiceProvider.GetService<IStripeWebhookInbox>();
            if (inbox is not null)
            {
                var applied = await inbox.HasAppliedSettlementAsync(session.Id, ct);
                if (!applied.IsError && applied.Result)
                    settlement = FiatSettlementStates.FundsApplied;
            }
        }

        return new FiatCheckoutIntent
        {
            IntentId = TryParseIntentId(session.Id, out var projectedId) ? projectedId : Guid.Empty,
            AdmissionKey = session.AdmissionKey,
            Status = FiatIntentStatuses.FromSessionState(session.State),
            SettlementState = settlement,
            // The payment page is only handed back while the intent can still be paid; a terminal
            // intent that still echoed a live URL invites a second payment against one claim.
            PaymentUrl = HostedSessionStates.IsTerminal(session.State) ? null : session.RedirectUrl,
            Resumed = resumed,
            AmountPaidMinorUnits = amountPaid,
            Currency = currency,
            ExpiresAt = session.ExpiresAt,
        };
    }
}

/// <summary>
/// The origin allowlist for checkout return URLs, and the per-intent query parameter that lets a
/// return page find the intent it came back to.
/// </summary>
/// <remarks>
/// The intent is identified by the node's own intent id rather than by Stripe's
/// <c>{CHECKOUT_SESSION_ID}</c> template. A provider session id is secret-adjacent server-side
/// correlation (see <c>Services/HostedSessions/AGENTS.md</c>), and putting it in a browser URL
/// publishes it into history, referrers and analytics for no gain.
/// </remarks>
public static class FiatReturnUrlPolicy
{
    /// <summary>Query parameter the return page reads to look its intent up.</summary>
    public const string IntentQueryParameter = "intent";

    /// <summary>
    /// Resolves and origin-checks the return URL to use, or refuses. <paramref name="reason"/> is
    /// for the server log only — the caller is told one fixed message, so this cannot be used to
    /// probe the allowlist.
    /// </summary>
    public static bool TryResolveOrigin(
        string? requested,
        string? configuredDefault,
        IReadOnlyCollection<string> allowedOrigins,
        RailEnvironment environment,
        [NotNullWhen(true)] out Uri? resolved,
        out string reason)
    {
        resolved = null;
        reason = string.Empty;

        var candidate = string.IsNullOrWhiteSpace(requested) ? configuredDefault : requested;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            reason = "no return URL was supplied and none is configured";
            return false;
        }

        if (!Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var uri))
        {
            reason = "the return URL is not an absolute URL";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            // https://trusted.example@evil.example parses with Host=evil.example. Origin matching
            // already catches it; refusing outright keeps the refusal legible in the log.
            reason = "the return URL carries user info";
            return false;
        }

        // Plain http is tolerated ONLY for a loopback address outside the live environment, which
        // is how a developer runs the return page locally. Live has no exception.
        var httpsRequired = environment.Kind == RailEnvironmentKind.Live || !uri.IsLoopback;
        if (httpsRequired && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            reason = "the return URL is not https";
            return false;
        }

        if (!MatchesAllowedOrigin(uri, allowedOrigins))
        {
            reason = "the return URL's origin is not allowlisted";
            return false;
        }

        resolved = uri;
        return true;
    }

    /// <summary>Appends the per-intent query parameter the return page reads.</summary>
    public static string AppendIntent(Uri returnUrl, string intentId)
    {
        ArgumentNullException.ThrowIfNull(returnUrl);

        var builder = new UriBuilder(returnUrl);
        var pair = IntentQueryParameter + "=" + Uri.EscapeDataString(intentId);
        var existing = builder.Query.TrimStart('?');
        builder.Query = string.IsNullOrEmpty(existing) ? pair : existing + "&" + pair;
        return builder.Uri.ToString();
    }

    /// <summary>True when <paramref name="uri"/>'s scheme, host and port match an allowlist entry.</summary>
    public static bool MatchesAllowedOrigin(Uri uri, IReadOnlyCollection<string> allowedOrigins)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (allowedOrigins is null || allowedOrigins.Count == 0)
            return false;

        foreach (var entry in allowedOrigins)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            if (!Uri.TryCreate(entry.Trim(), UriKind.Absolute, out var allowed))
                continue;

            // Scheme + host + port, all three. Comparing the host alone would let http match an
            // https allowlist entry, and comparing a prefix would let evil.example.com match
            // example.com.
            if (string.Equals(allowed.Scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(allowed.Host, uri.Host, StringComparison.OrdinalIgnoreCase)
                && allowed.Port == uri.Port)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Boot-time validation for the fiat rail. Mirrors the capability-honesty guards in
/// <c>BlockchainProviderFactory</c>: a node that advertises a money path it cannot complete must
/// fail at startup, not at the moment a person's card is charged.
/// </summary>
/// <remarks>
/// Two guards, because the facts live in two places. <see cref="Validate"/> reads CONFIGURATION
/// and the container, and runs before <c>builder.Build()</c>. <see cref="GuardLiveCapabilityAsync"/>
/// reads DATA — the declared reserve partition and the registered price — and therefore runs
/// after the container is built. Registration is not capability: OI-29 was exactly the gap
/// between the two.
/// </remarks>
public static class FiatRailStartup
{
    /// <summary>
    /// Throws when the fiat rail's CONFIGURATION could not support an on-ramp. Capability is a
    /// separate proof: see <see cref="GuardLiveCapabilityAsync"/>.
    /// </summary>
    /// <param name="options">The bound <c>Fiat</c> section.</param>
    /// <param name="simulatedBlockchainMode">True when <c>Blockchain:Mode=Simulated</c>.</param>
    /// <param name="settlementHandlerRegistered">
    /// True when at least one <c>IFiatSettlementHandler</c> — a handler that turns a cleared
    /// payment into funds in the payer's account — is in the container. The LIVE environment is
    /// refused without one, because "the handler ships in the next item" is not a control.
    /// <para>This proves a TYPE is registered and NOTHING MORE. A registered handler with no
    /// declared on-ramp partition and no registered price credits nothing, which is why the live
    /// environment additionally has to pass <see cref="GuardLiveCapabilityAsync"/>.</para>
    /// </param>
    public static void Validate(
        FiatOptions options, bool simulatedBlockchainMode, bool settlementHandlerRegistered)
    {
        ArgumentNullException.ThrowIfNull(options);

        var environmentName = options.Environment?.Trim() ?? string.Empty;
        var recognised =
            string.Equals(environmentName, RailEnvironmentNames.Test, StringComparison.OrdinalIgnoreCase)
            || string.Equals(environmentName, RailEnvironmentNames.Live, StringComparison.OrdinalIgnoreCase);
        if (!recognised)
            throw new InvalidOperationException(
                $"Fiat:Environment is \"{options.Environment}\"; it must be "
                + $"\"{RailEnvironmentNames.Test}\" or \"{RailEnvironmentNames.Live}\".");

        if (!options.Enabled)
            return;

        var environment = options.ActiveEnvironment;
        var settings = options.ForEnvironment(environment);

        if (!settings.IsEnabled)
            throw new InvalidOperationException(
                $"Fiat:Enabled is true and Fiat:Environment is \"{environment.Name}\", but "
                + $"Fiat:{Capitalise(environment.Name)}:IsEnabled is false. Enable that environment "
                + "or turn the rail off.");

        if (string.IsNullOrWhiteSpace(settings.SecretKey))
            throw new InvalidOperationException(
                $"Fiat:{Capitalise(environment.Name)}:SecretKey is not set. Supply it through the host "
                + "secret store; the rail cannot create a payment without it.");

        if (string.IsNullOrWhiteSpace(settings.WebhookSecret))
            throw new InvalidOperationException(
                $"Fiat:{Capitalise(environment.Name)}:WebhookSecret is not set. Without it every inbound "
                + "payment notification fails signature verification, so a person would be charged and "
                + "nothing would ever be credited.");

        if (options.Checkout.AllowedReturnOrigins.Count == 0)
            throw new InvalidOperationException(
                "Fiat:Checkout:AllowedReturnOrigins is empty. Every checkout would be refused, and an "
                + "unbounded return URL on a payment page is an open redirect. List the origins this "
                + "node may send a payer back to.");

        if (options.Checkout.AllowedPurposes.Count == 0)
            throw new InvalidOperationException(
                "Fiat:Checkout:AllowedPurposes is empty, so no checkout could name a purpose this node "
                + "accepts. List the purposes explicitly.");

        if (options.Checkout.MinimumAmountMinorUnits <= 0
            || options.Checkout.MaximumAmountMinorUnits < options.Checkout.MinimumAmountMinorUnits)
        {
            throw new InvalidOperationException(
                "Fiat:Checkout:MinimumAmountMinorUnits must be positive and no greater than "
                + "Fiat:Checkout:MaximumAmountMinorUnits.");
        }

        foreach (var origin in options.Checkout.AllowedReturnOrigins)
        {
            if (!Uri.TryCreate(origin?.Trim(), UriKind.Absolute, out var parsed))
                throw new InvalidOperationException(
                    $"Fiat:Checkout:AllowedReturnOrigins contains \"{origin}\", which is not an absolute URL.");

            if (environment.Kind == RailEnvironmentKind.Live
                && !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Fiat:Checkout:AllowedReturnOrigins contains the non-https origin \"{origin}\" while the "
                    + "rail is bound to the live environment.");
            }
        }

        if (environment.Kind != RailEnvironmentKind.Live)
            return;

        if (simulatedBlockchainMode)
            throw new InvalidOperationException(
                "Fiat:Environment is \"live\" while Blockchain:Mode is \"Simulated\". Real cards would be "
                + "charged and the funds credited against simulated settlement. Set Blockchain:Mode=Live "
                + "or move the rail back to the test environment.");

        if (!settlementHandlerRegistered)
            throw new InvalidOperationException(
                "Fiat:Environment is \"live\" but no IFiatSettlementHandler is registered, so a cleared "
                + "card payment would be recorded and never credited to the payer. Register the on-ramp "
                + "settlement handler or move the rail back to the test environment.");
    }

    /// <summary>
    /// Refuses live boot unless this node can actually turn a cleared payment into funds.
    /// </summary>
    /// <remarks>
    /// <para>Call it AFTER <c>builder.Build()</c>, with <c>app.Services</c>. It cannot run beside
    /// <see cref="Validate"/>: the two facts it proves are rows in the database — the on-ramp
    /// partition declared on the platform unit's reserve ledger, and a current registered price for
    /// that unit — and neither store can be resolved from an unbuilt service collection.</para>
    /// <para>A disabled or test-environment rail returns without touching either store, so a dev
    /// node still boots against an empty database.</para>
    /// </remarks>
    /// <param name="services">The built root provider; a scope is created for the scoped stores.</param>
    /// <param name="options">The bound <c>Fiat</c> section.</param>
    /// <param name="asOf">Clock for the price-staleness read; defaults to the container TimeProvider.</param>
    /// <param name="ct">Cancellation for the two reads.</param>
    public static async Task GuardLiveCapabilityAsync(
        IServiceProvider services,
        FiatOptions options,
        DateTimeOffset? asOf = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled || options.ActiveEnvironment.Kind != RailEnvironmentKind.Live)
            return;

        // Both stores are scoped. Resolving them off the root provider would either throw or
        // (worse) pin a scoped store to the root for the life of the process.
        using var scope = services.GetService<IServiceScopeFactory>()?.CreateScope();
        var resolver = scope?.ServiceProvider ?? services;

        var now = asOf ?? (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        // tenantId: null on purpose. Boot has no payment and no tenant, so the honest question is
        // the node-wide one — a node with no price registered by anybody can credit nobody. The
        // per-tenant question belongs at the checkout counter, where a tenant exists to name.
        var capability = await FiatCreditCapabilityProbe.ProbeAsync(resolver, now, tenantId: null, ct);
        if (capability.CanCredit)
            return;

        throw new InvalidOperationException(
            "Fiat:Environment is \"live\" but this node cannot credit a cleared card payment: "
            + capability.Detail + ". " + capability.OperatorAction
            + ", or move the rail back to the test environment.");
    }

    private static string Capitalise(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];
}

/// <summary>Whether this node can turn a cleared payment into funds, and what is missing if not.</summary>
/// <param name="CanCredit">True only when every fact the on-ramp needs is present.</param>
/// <param name="Detail">Operator-facing reason; never surfaced to a payer.</param>
/// <param name="OperatorAction">The one thing that would fix it.</param>
public sealed record FiatCreditCapability(bool CanCredit, string Detail, string OperatorAction)
{
    /// <summary>The node can credit a cleared payment.</summary>
    public static FiatCreditCapability Able() => new(true, string.Empty, string.Empty);

    /// <summary>The node cannot credit a cleared payment, for the stated reason.</summary>
    public static FiatCreditCapability Unable(string detail, string operatorAction) =>
        new(false, detail, operatorAction);
}

/// <summary>
/// Proves the fiat on-ramp could actually credit a cleared payment, by reading the same two
/// facts the settlement handler reads at webhook time.
/// </summary>
/// <remarks>
/// <para>OI-29: the boot guard used to prove that an <c>IFiatSettlementHandler</c> TYPE was in
/// the container and to describe that as "a node cannot charge a real card while nothing is able
/// to credit it". It is not the same claim. Crediting needs two operator facts that live in data:
/// the on-ramp partition declared on the platform unit's reserve ledger
/// (<c>IPlatformReserve.DeclarePartitionsAsync</c>), and a current registered price for that unit
/// (<c>POST /api/fiat/price-schedule</c>) — OI-45. Without either, the card clears, the issuance
/// is refused, the retries burn and the lot parks <c>Stuck</c>.</para>
/// <para>Read-only and side-effect free, so it is safe on the checkout path and at boot alike.
/// Every refusal is fail-closed: an unresolvable dependency reports "cannot credit", never
/// "probably fine".</para>
/// </remarks>
public static class FiatCreditCapabilityProbe
{
    /// <summary>Configuration key naming the reserve partition on-ramp issuance draws from.</summary>
    public const string PartitionConfigurationKey = "Fiat:OnRamp:ReservePartition";

    /// <summary>Reads the declared partition and the registered price for the platform unit.</summary>
    /// <param name="services">A SCOPED provider: both stores are registered scoped.</param>
    /// <param name="asOf">The instant price staleness is measured against.</param>
    /// <param name="tenantId">
    /// The tenant whose payment this is, when there is one. Supplying it makes the price question
    /// the PER-PAYMENT one — the schedule that would actually settle this payment, resolved with
    /// the settlement handler's own precedence — instead of "does any scope on this node have a
    /// price". Boot passes null, because at boot there is no payment and no tenant to ask about.
    /// </param>
    /// <param name="ct">Cancellation for the reads.</param>
    public static async Task<FiatCreditCapability> ProbeAsync(
        IServiceProvider services,
        DateTimeOffset asOf,
        Guid? tenantId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var onRamp = services.GetService<OnRampOptions>();
        if (onRamp is null)
        {
            return FiatCreditCapability.Unable(
                "the on-ramp is not registered, so nothing turns a cleared payment into funds",
                "call OnRampRegistration.AddFiatOnRamp during composition");
        }

        // OnRampOptions is bound with .Get<T>(), so an environment variable set to "" REPLACES the
        // C# default rather than leaving it. A blank partition name therefore resolves to the
        // NARROWEST answer -- no partition at all -- rather than silently reverting to "on-ramp";
        // the reserve cannot be drawn against a blank name either way, so this is what a deploy
        // with an empty variable actually means.
        var partitionName = ReservePartitionCodec.Normalize(onRamp.ReservePartition);
        if (string.IsNullOrEmpty(partitionName))
        {
            return FiatCreditCapability.Unable(
                $"{PartitionConfigurationKey} is blank, so on-ramp issuance has no partition to draw from",
                $"set {PartitionConfigurationKey} to the partition declared on the platform unit");
        }

        var genesis = services.GetService<IPlatformTokenGenesis>();
        var reserve = services.GetService<IPlatformReserve>();
        var schedules = services.GetService<ITenantPriceScheduleStore>();
        if (genesis is null || reserve is null || schedules is null)
        {
            return FiatCreditCapability.Unable(
                "the platform unit, the reserve ledger or the price schedule is not registered",
                "register the reserve and the on-ramp before binding the rail to the live environment");
        }

        var tokenState = await genesis.GetStateAsync(ct);
        if (tokenState.IsError || tokenState.Result is null)
        {
            return FiatCreditCapability.Unable(
                $"the platform unit could not be resolved ({tokenState.Message})",
                "run platform-token genesis on this network");
        }

        var definition = tokenState.Result.Definition;
        var asset = $"{definition.ChainType}/{definition.AssetId}";

        var ledger = await reserve.GetLedgerAsync(definition.ChainType, definition.AssetId, ct);
        if (ledger.IsError || ledger.Result is null)
        {
            return FiatCreditCapability.Unable(
                $"the platform unit has no reserve ledger row ({ledger.Message})",
                $"register {asset} with the platform reserve");
        }

        // An unreadable partition blob is refused rather than read as "no partitions": treating it
        // as empty here would report the node creditable on a row nothing can actually draw from.
        var partitions = ReservePartitionCodec.Decode(ledger.Result.Partitions, out var decodeError);
        if (partitions is null)
        {
            return FiatCreditCapability.Unable(
                $"the reserve ledger partitions for {asset} could not be read ({decodeError})",
                "repair the platform unit's reserve ledger row");
        }

        if (!partitions.TryGetValue(partitionName, out var partition))
        {
            return FiatCreditCapability.Unable(
                $"the '{partitionName}' partition is not declared on {asset}, so every issuance from a "
                + "cleared payment would be refused by the reserve",
                $"declare it with POST /api/reserve/{definition.AssetId}/partitions?chain={definition.ChainType}");
        }

        // Declared-but-exhausted is the same outcome as undeclared for the next payer: the draw is
        // refused and the money is taken. Availability, not mere presence, is the capability.
        if (partition.Available.Sign <= 0)
        {
            return FiatCreditCapability.Unable(
                $"the '{partitionName}' partition on {asset} has no units left to draw",
                $"re-declare it larger with POST /api/reserve/{definition.AssetId}/partitions?chain={definition.ChainType}");
        }

        // The price question is asked at the granularity the caller actually has. At boot there is
        // no payment, so ANY scope with a usable price is the honest question: with none at all no
        // payment from anyone could ever be converted. At the counter the tenant is in hand, and
        // asking the node-wide question there ADMITS the very checkout OI-45 describes — on a node
        // where only tenant U has registered a price, a checkout for tenant T passed, the card was
        // charged and the lot parked Stuck.
        return tenantId is { } payingTenant
            ? await ProbeTenantPriceAsync(schedules, onRamp, definition, asset, payingTenant, asOf, ct)
            : await ProbeAnyPriceAsync(schedules, definition, asset, asOf, ct);
    }

    /// <summary>The boot-level price question: has ANY scope on this node registered one.</summary>
    private static async Task<FiatCreditCapability> ProbeAnyPriceAsync(
        ITenantPriceScheduleStore schedules,
        PlatformTokenDefinition definition,
        string asset,
        DateTimeOffset asOf,
        CancellationToken ct)
    {
        var registered = await schedules.ListLatestPerScopeAsync(
            definition.ChainType, definition.AssetId, ct);
        if (registered.IsError)
        {
            return FiatCreditCapability.Unable(
                $"the registered prices for {asset} could not be read ({registered.Message})",
                "restore the price-schedule store");
        }

        var priced = registered.Result?.Any(schedule => IsUsable(schedule, asOf)) ?? false;
        return priced
            ? FiatCreditCapability.Able()
            : FiatCreditCapability.Unable(
                $"no current price is registered for {asset}, so a cleared payment has nothing to "
                + "convert money into units with",
                "register one with POST /api/fiat/price-schedule");
    }

    /// <summary>
    /// The per-payment price question: is there a schedule that would actually settle THIS
    /// payment. Mirrors <c>OnRampSettlementHandler.ResolveScheduleAsync</c> exactly — the tenant's
    /// own schedule wins, and the operator's node-wide default stands in only where the operator
    /// has said it may (<c>Fiat:OnRamp:AllowNodeDefaultScheduleFallback</c>). A probe that used a
    /// wider rule than the settlement path would pass a checkout the settlement path then refuses,
    /// which is a charged card and a parked lot.
    /// </summary>
    private static async Task<FiatCreditCapability> ProbeTenantPriceAsync(
        ITenantPriceScheduleStore schedules,
        OnRampOptions onRamp,
        PlatformTokenDefinition definition,
        string asset,
        Guid tenantId,
        DateTimeOffset asOf,
        CancellationToken ct)
    {
        var scope = PriceScheduleScope.For(tenantId);

        var scoped = await schedules.GetLatestAsync(scope, definition.ChainType, definition.AssetId, ct);
        if (scoped.IsError)
        {
            return FiatCreditCapability.Unable(
                $"the registered prices for {asset} could not be read ({scoped.Message})",
                "restore the price-schedule store");
        }

        // A retired latest version means "we have stopped selling units", not "reach back to an
        // older price" — and it must not fall through to the node default either, or retiring a
        // tenant's price would silently resell at the operator's.
        if (scoped.Result is { } own)
        {
            return IsUsable(own, asOf)
                ? FiatCreditCapability.Able()
                : FiatCreditCapability.Unable(
                    $"the price this tenant registered for {asset} is retired or out of date, so a "
                    + "cleared payment has nothing to convert money into units with",
                    "register a current one with POST /api/fiat/price-schedule");
        }

        if (PriceScheduleScope.IsNode(scope) || !onRamp.AllowNodeDefaultScheduleFallback)
        {
            return FiatCreditCapability.Unable(
                $"this tenant has registered no price for {asset} and the node default may not "
                + "stand in for it, so a cleared payment has nothing to convert money into units with",
                "register one with POST /api/fiat/price-schedule, or enable "
                + "Fiat:OnRamp:AllowNodeDefaultScheduleFallback");
        }

        var fallback = await schedules.GetLatestAsync(
            PriceScheduleScope.Node, definition.ChainType, definition.AssetId, ct);
        if (fallback.IsError)
        {
            return FiatCreditCapability.Unable(
                $"the registered prices for {asset} could not be read ({fallback.Message})",
                "restore the price-schedule store");
        }

        return fallback.Result is { } node && IsUsable(node, asOf)
            ? FiatCreditCapability.Able()
            : FiatCreditCapability.Unable(
                $"no current price is registered for {asset} by this tenant or by the node, so a "
                + "cleared payment has nothing to convert money into units with",
                "register one with POST /api/fiat/price-schedule");
    }

    /// <summary>Mirrors the on-ramp read exactly: active, not future-dated, not stale.</summary>
    /// <remarks>
    /// Kept in step with <c>OnRampSettlementHandler</c> deliberately. A boot guard that accepted a
    /// price the settlement path would refuse would pass a node that still cannot credit.
    /// </remarks>
    public static bool IsUsable(RegisteredPriceSchedule? schedule, DateTimeOffset asOf) =>
        schedule is not null
        && schedule.IsActive
        && schedule.DeclaredAt <= asOf + PriceScheduleLimits.FutureDeclarationTolerance
        && asOf - schedule.DeclaredAt <= PriceScheduleLimits.MaxDeclarationAge;
}
