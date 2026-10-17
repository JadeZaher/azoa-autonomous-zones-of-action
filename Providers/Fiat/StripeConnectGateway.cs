// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Fiat;

/// <summary>What a person's connected payout account can actually do, re-read server-side.</summary>
public sealed class StripeConnectedAccount
{
    /// <summary>The provider's account id (<c>acct_…</c>). Server-side correlation, never shown.</summary>
    public string AccountId { get; init; } = string.Empty;

    /// <summary>
    /// The ONLY fact that decides whether money may be sent. Not "details submitted", not
    /// "charges enabled": a payout to an account that cannot receive one bounces back as a
    /// reversal, which is the expensive way to learn this.
    /// </summary>
    public bool PayoutsEnabled { get; init; }

    /// <summary>Whether the person has finished the provider's onboarding form.</summary>
    public bool DetailsSubmitted { get; init; }

    /// <summary>Whether the account may also take payments. Not used to gate a payout.</summary>
    public bool ChargesEnabled { get; init; }

    /// <summary>Provider's own reason the account is restricted; logged, never returned verbatim.</summary>
    public string? DisabledReason { get; init; }

    /// <summary>How many requirements the provider is still waiting on. A count, never the detail.</summary>
    public int OutstandingRequirementCount { get; init; }

    public string? PayoutSchedule { get; init; }
}

/// <summary>The one-time hosted onboarding link a person is sent to.</summary>
public sealed class StripeAccountLink
{
    public string Url { get; init; } = string.Empty;

    /// <summary>Links expire in minutes; a stale one is regenerated rather than reused.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>What a connected account is being asked for. Identity and routing only.</summary>
public sealed class StripeConnectedAccountRequest
{
    /// <summary>The person the account belongs to, as a compact Guid.</summary>
    public string SubjectUserId { get; init; } = string.Empty;

    /// <summary>The payout-account admission row this create is fenced by; recorded for operator reconciliation.</summary>
    public string ClaimId { get; init; } = string.Empty;

    /// <summary>Two-letter country the account is registered in.</summary>
    public string Country { get; init; } = "US";

    /// <summary>Stripe request idempotency key; a retried create returns the same account.</summary>
    public string IdempotencyKey { get; init; } = string.Empty;
}

/// <summary>Where to send a person for onboarding, and where to send them back.</summary>
public sealed class StripeAccountLinkRequest
{
    public string AccountId { get; init; } = string.Empty;

    /// <summary>Where the provider sends a person whose link has expired. Origin-allowlisted.</summary>
    public string RefreshUrl { get; init; } = string.Empty;

    /// <summary>Where the provider sends a person who finished. Origin-allowlisted.</summary>
    public string ReturnUrl { get; init; } = string.Empty;
}

/// <summary>One outbound payment to a connected account.</summary>
public sealed class StripeTransferRequest
{
    /// <summary>The node's own payout id, echoed into metadata so a reversal finds its way home.</summary>
    public string PayoutId { get; init; } = string.Empty;

    public string SubjectUserId { get; init; } = string.Empty;

    public string ConnectedAccountId { get; init; } = string.Empty;

    /// <summary>The money to send, in minor units. Derived from a registered price, never a body.</summary>
    public long AmountMinorUnits { get; init; }

    public string Currency { get; init; } = "usd";

    /// <summary>Stripe request idempotency key; a retried create returns the same transfer.</summary>
    public string IdempotencyKey { get; init; } = string.Empty;
}

/// <summary>The server-fetched facts about one outbound transfer.</summary>
public sealed class StripeTransfer
{
    public string? IntentId { get; init; }
    public string TransferId { get; init; } = string.Empty;

    /// <summary>What was sent, in minor units, as the provider holds it.</summary>
    public long AmountMinorUnits { get; init; }

    /// <summary>How much of it has been pulled back. Non-zero means a reversal happened.</summary>
    public long AmountReversedMinorUnits { get; init; }

    public string Currency { get; init; } = string.Empty;

    public string? Destination { get; init; }

    /// <summary>True once the WHOLE transfer has been reversed.</summary>
    public bool Reversed { get; init; }

    public bool Livemode { get; init; }
}

/// <summary>A connected account's bank payout, fetched in that account's scope.</summary>
public sealed class StripeBankPayout
{
    public string PayoutId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public long AmountMinorUnits { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string? IntentId { get; init; }
    public bool Livemode { get; init; }
}

/// <summary>
/// The Stripe Connect surface for accounts, onboarding, funding transfers and bank payouts. See
/// <c>Services/Fiat/OffRamp/AGENTS.md</c>.
/// </summary>
public interface IStripeConnectGateway
{
    /// <summary>Reverses a fixed amount of a funding transfer with a durable idempotency key.</summary>
    Task<AZOAResult<StripeTransferReversal>> ReverseTransferAsync(StripeCredentials credentials,
        string transferId, long amount, string payoutId, string idempotencyKey, CancellationToken ct = default);
    /// <summary>Creates an Express connected account. Idempotent on the request key.</summary>
    Task<AZOAResult<StripeConnectedAccount>> CreateConnectedAccountAsync(
        StripeCredentials credentials, StripeConnectedAccountRequest request, CancellationToken ct = default);

    /// <summary>Re-reads one connected account server-side. The authority on whether it may be paid.</summary>
    Task<AZOAResult<StripeConnectedAccount>> GetConnectedAccountAsync(
        StripeCredentials credentials, string accountId, CancellationToken ct = default);

    /// <summary>Creates a fresh hosted onboarding link. Links expire, so this is never cached.</summary>
    Task<AZOAResult<StripeAccountLink>> CreateAccountLinkAsync(
        StripeCredentials credentials, StripeAccountLinkRequest request, CancellationToken ct = default);

    /// <summary>Sends money to a connected account. Idempotent on the request key.</summary>
    Task<AZOAResult<StripeTransfer>> CreatePayoutTransferAsync(
        StripeCredentials credentials, StripeTransferRequest request, CancellationToken ct = default);

    /// <summary>Re-reads one transfer server-side. Never trust a webhook body for these facts.</summary>
    Task<AZOAResult<StripeTransfer>> GetTransferAsync(
        StripeCredentials credentials, string transferId, CancellationToken ct = default);

    /// <summary>Sets the platform-controlled account's payout schedule to manual.</summary>
    Task<AZOAResult<StripeConnectedAccount>> ConfigureManualPayoutsAsync(
        StripeCredentials credentials, string accountId, CancellationToken ct = default);

    /// <summary>Creates an idempotent bank payout in the connected account's scope.</summary>
    Task<AZOAResult<StripeBankPayout>> CreateBankPayoutAsync(
        StripeCredentials credentials, StripeTransferRequest request, CancellationToken ct = default);

    /// <summary>Fetches a bank payout from its owning connected account.</summary>
    Task<AZOAResult<StripeBankPayout>> GetBankPayoutAsync(
        StripeCredentials credentials, string accountId, string payoutId, CancellationToken ct = default);
}

/// <summary>
/// Form-encoded Stripe Connect REST client over <see cref="HttpClient"/>, ported from ArdaNova's
/// <c>StripeService.CreateConnectedAccountAsync</c> / <c>CreatePayoutTransferAsync</c>.
///
/// <para>Three things changed in the port, and each closes a hole the original had. The amount is
/// a <see cref="long"/> of minor units rather than <c>(long)(usdAmount * 100)</c> — binary
/// floating point is not allowed anywhere near money on this node. The destination account is
/// resolved by the node from the person's own durable onboarding record rather than passed in by
/// a caller, so no request can redirect a payout to somebody else's bank. And every refusal is
/// mapped to a fixed consumer-safe message before it leaves the seam, so Stripe's own text —
/// which names accounts, balances and capabilities — never reaches a person.</para>
///
/// <para>Deliberately not the Stripe SDK, for the same reason <see cref="StripeGateway"/> is not:
/// the surface is limited to the off-ramp's endpoints, and the house rule is to minimise external
/// dependencies on a value path.</para>
/// </summary>
public sealed class StripeConnectGateway : IStripeConnectGateway
{
    /// <inheritdoc/>
    public Task<AZOAResult<StripeTransferReversal>> ReverseTransferAsync(StripeCredentials credentials,
        string transferId, long amount, string payoutId, string idempotencyKey, CancellationToken ct = default)
        => SendAsync(credentials, HttpMethod.Post, "/v1/transfers/" + Uri.EscapeDataString(transferId) + "/reversals",
            new[] { new KeyValuePair<string, string>("amount", amount.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("metadata[azoa_intent_id]", payoutId) },
            idempotencyKey, "reverse a failed payout's funding", StripeTransferReversal.Parse, ct);
    /// <summary>The account type this node creates. Express carries the provider's own onboarding.</summary>
    public const string AccountType = "express";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StripeConnectGateway> _logger;

    public StripeConnectGateway(IHttpClientFactory httpClientFactory, ILogger<StripeConnectGateway> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<AZOAResult<StripeConnectedAccount>> CreateConnectedAccountAsync(
        StripeCredentials credentials, StripeConnectedAccountRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var form = new List<KeyValuePair<string, string>>
        {
            new("type", AccountType),
            new("settings[payouts][schedule][interval]", "manual"),
            new("country", string.IsNullOrWhiteSpace(request.Country) ? "US" : request.Country.Trim().ToUpperInvariant()),
            // Transfers is the only capability requested. Asking for card_payments as well would
            // put this node's users through a payments review they have no reason to pass.
            new("capabilities[transfers][requested]", "true"),
            new("metadata[azoa_user_id]", request.SubjectUserId),
        };
        if (!string.IsNullOrWhiteSpace(request.ClaimId))
            form.Add(new("metadata[azoa_payout_claim]", request.ClaimId));

        return SendAsync(
            credentials,
            HttpMethod.Post,
            "/v1/accounts",
            form,
            request.IdempotencyKey,
            "open a payout account",
            ParseConnectedAccount,
            ct);
    }

    /// <inheritdoc />
    public Task<AZOAResult<StripeConnectedAccount>> GetConnectedAccountAsync(
        StripeCredentials credentials, string accountId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return Task.FromResult(FiatPayoutErrors.RailUnavailable<StripeConnectedAccount>());

        return SendAsync(
            credentials,
            HttpMethod.Get,
            "/v1/accounts/" + Uri.EscapeDataString(accountId.Trim()),
            form: null,
            idempotencyKey: null,
            "read a payout account",
            ParseConnectedAccount,
            ct);
    }

    /// <inheritdoc />
    public Task<AZOAResult<StripeAccountLink>> CreateAccountLinkAsync(
        StripeCredentials credentials, StripeAccountLinkRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.AccountId))
            return Task.FromResult(FiatPayoutErrors.RailUnavailable<StripeAccountLink>());

        var form = new List<KeyValuePair<string, string>>
        {
            new("account", request.AccountId.Trim()),
            new("refresh_url", request.RefreshUrl),
            new("return_url", request.ReturnUrl),
            new("type", "account_onboarding"),
        };

        // No idempotency key: an account link is a short-lived credential and a person who comes
        // back tomorrow must get a NEW one. Replaying a spent link would hand them a dead page.
        return SendAsync(
            credentials,
            HttpMethod.Post,
            "/v1/account_links",
            form,
            idempotencyKey: null,
            "start payout account setup",
            ParseAccountLink,
            ct);
    }

    /// <inheritdoc />
    public Task<AZOAResult<StripeTransfer>> CreatePayoutTransferAsync(
        StripeCredentials credentials, StripeTransferRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.AmountMinorUnits <= 0)
            return Task.FromResult(AZOAResult<StripeTransfer>.FailureWithCode(
                "The amount to cash out is not a positive amount of money.", AzoaErrorCodes.InvalidRequest));

        if (string.IsNullOrWhiteSpace(request.ConnectedAccountId))
            return Task.FromResult(FiatPayoutErrors.RailUnavailable<StripeTransfer>());

        var form = new List<KeyValuePair<string, string>>
        {
            // Minor units, integer, straight through. ArdaNova computed (long)(usdAmount * 100)
            // from a double; a repeating binary fraction there is a cent that appears or vanishes.
            new("amount", request.AmountMinorUnits.ToString(CultureInfo.InvariantCulture)),
            new("currency", request.Currency),
            new("destination", request.ConnectedAccountId.Trim()),
            // azoa_intent_id is the correlation StripeEventEnvelope recovers from ANY object's
            // metadata, which is how transfer.reversed finds this payout row again. It is a
            // compact Guid because that is the only shape the envelope parses.
            new("metadata[azoa_intent_id]", request.PayoutId),
            new("metadata[azoa_user_id]", request.SubjectUserId),
            new("transfer_group", "azoa-payout-" + request.PayoutId),
        };

        return SendAsync(
            credentials,
            HttpMethod.Post,
            "/v1/transfers",
            form,
            request.IdempotencyKey,
            "send a cash-out",
            ParseTransfer,
            ct);
    }

    /// <inheritdoc />
    public Task<AZOAResult<StripeTransfer>> GetTransferAsync(
        StripeCredentials credentials, string transferId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(transferId))
            return Task.FromResult(FiatPayoutErrors.RailUnavailable<StripeTransfer>());

        return SendAsync(
            credentials,
            HttpMethod.Get,
            "/v1/transfers/" + Uri.EscapeDataString(transferId.Trim()),
            form: null,
            idempotencyKey: null,
            "read a cash-out",
            ParseTransfer,
            ct);
    }

    /// <inheritdoc />
    public Task<AZOAResult<StripeConnectedAccount>> ConfigureManualPayoutsAsync(
        StripeCredentials credentials, string accountId, CancellationToken ct = default)
        => SendAsync(credentials, HttpMethod.Post, "/v1/accounts/" + Uri.EscapeDataString(accountId),
            new[] { new KeyValuePair<string, string>("settings[payouts][schedule][interval]", "manual") },
            null, "configure bank payouts", ParseConnectedAccount, ct);

    /// <inheritdoc />
    public Task<AZOAResult<StripeBankPayout>> CreateBankPayoutAsync(
        StripeCredentials credentials, StripeTransferRequest request, CancellationToken ct = default)
        => SendAsync(credentials, HttpMethod.Post, "/v1/payouts", new[]
        {
            new KeyValuePair<string, string>("amount", request.AmountMinorUnits.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("currency", request.Currency.ToLowerInvariant()),
            new KeyValuePair<string, string>("method", "standard"),
            new KeyValuePair<string, string>("metadata[azoa_intent_id]", request.PayoutId),
        }, request.IdempotencyKey, "send money to a bank", ParseBankPayout, ct, request.ConnectedAccountId);

    /// <inheritdoc />
    public Task<AZOAResult<StripeBankPayout>> GetBankPayoutAsync(
        StripeCredentials credentials, string accountId, string payoutId, CancellationToken ct = default)
        => SendAsync(credentials, HttpMethod.Get, "/v1/payouts/" + Uri.EscapeDataString(payoutId),
            null, null, "read a bank payout", ParseBankPayout, ct, accountId);

    private async Task<AZOAResult<T>> SendAsync<T>(
        StripeCredentials credentials,
        HttpMethod method,
        string path,
        IReadOnlyCollection<KeyValuePair<string, string>>? form,
        string? idempotencyKey,
        string attemptedAction,
        Func<string, T?> parse,
        CancellationToken ct,
        string? connectedAccountId = null)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(credentials.SecretKey) || string.IsNullOrWhiteSpace(credentials.ApiBaseUrl))
            return FiatPayoutErrors.RailUnavailable<T>();

        // One opaque id ties the consumer-visible refusal to the server-side log line that carries
        // the real Stripe text; the text itself never crosses the boundary.
        var correlationId = Guid.NewGuid().ToString("N");

        try
        {
            using var message = new HttpRequestMessage(method, credentials.ApiBaseUrl.TrimEnd('/') + path);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.SecretKey);
            message.Headers.TryAddWithoutValidation("Stripe-Version", StripeGateway.ApiVersion);
            if (!string.IsNullOrWhiteSpace(connectedAccountId))
                message.Headers.TryAddWithoutValidation("Stripe-Account", connectedAccountId);
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
                message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            if (form is not null)
                message.Content = new FormUrlEncodedContent(form);

            var client = _httpClientFactory.CreateClient(StripeGateway.HttpClientName);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutSource.CancelAfter(
                credentials.Timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : credentials.Timeout);

            using var response = await client.SendAsync(message, timeoutSource.Token);
            var body = await response.Content.ReadAsStringAsync(timeoutSource.Token);

            if (!response.IsSuccessStatusCode)
            {
                using var error = JsonDocument.Parse(body);
                if ((int)response.StatusCode == 400
                    && error.RootElement.TryGetProperty("error", out var detail)
                    && ReadString(detail, "code") is "balance_insufficient" or "insufficient_funds")
                    return AZOAResult<T>.FailureWithCode(
                        "The payment provider is still making these funds available.", FiatPayoutErrors.AwaitingFundsCode);

                _logger.LogError(
                    "Stripe refused an attempt to {AttemptedAction} ({Status}). correlationId={CorrelationId}",
                    attemptedAction, (int)response.StatusCode, correlationId);

                // 429 and 5xx are the rail being busy; anything else is a request this node built
                // wrongly or an account the provider will not pay, neither of which a retry fixes.
                return (int)response.StatusCode is 429 or >= 500
                    ? FiatPayoutErrors.RailBusy<T>(correlationId)
                    : FiatPayoutErrors.RailRefused<T>(correlationId);
            }

            var parsed = parse(body);
            if (parsed is null)
            {
                _logger.LogError(
                    "Stripe returned a payout object this node could not read. correlationId={CorrelationId}",
                    correlationId);
                return FiatPayoutErrors.RailBusy<T>(correlationId);
            }

            return AZOAResult<T>.Success(parsed, "Success");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException)
        {
            _logger.LogError(
                exception,
                "The payment rail could not be reached to {AttemptedAction}. correlationId={CorrelationId}",
                attemptedAction, correlationId);
            return FiatPayoutErrors.RailBusy<T>(correlationId);
        }
    }

    /// <summary>Reads the connected-account fields this node uses. Unknown fields are ignored.</summary>
    public static StripeConnectedAccount? ParseConnectedAccount(string json)
    {
        var root = TryReadObject(json, out var document);
        if (root is null)
            return null;

        using (document)
        {
            var accountId = ReadString(root.Value, "id");
            if (string.IsNullOrWhiteSpace(accountId))
                return null;

            return new StripeConnectedAccount
            {
                AccountId = accountId!,
                PayoutSchedule = root.Value.TryGetProperty("settings", out var settings)
                    && settings.TryGetProperty("payouts", out var payouts)
                    && payouts.TryGetProperty("schedule", out var schedule)
                        ? ReadString(schedule, "interval") : null,
                PayoutsEnabled = ReadBool(root.Value, "payouts_enabled"),
                ChargesEnabled = ReadBool(root.Value, "charges_enabled"),
                DetailsSubmitted = ReadBool(root.Value, "details_submitted"),
                DisabledReason = ReadRequirementsString(root.Value, "disabled_reason"),
                OutstandingRequirementCount = CountRequirements(root.Value),
            };
        }
    }

    /// <summary>Reads the hosted onboarding link. A link with no URL is not a link.</summary>
    public static StripeAccountLink? ParseAccountLink(string json)
    {
        var root = TryReadObject(json, out var document);
        if (root is null)
            return null;

        using (document)
        {
            var url = ReadString(root.Value, "url");
            if (string.IsNullOrWhiteSpace(url))
                return null;

            return new StripeAccountLink
            {
                Url = url!,
                ExpiresAt = ReadUnixSeconds(root.Value, "expires_at"),
            };
        }
    }

    /// <summary>Reads the transfer fields this node uses, including how much came back.</summary>
    public static StripeTransfer? ParseTransfer(string json)
    {
        var root = TryReadObject(json, out var document);
        if (root is null)
            return null;

        using (document)
        {
            var transferId = ReadString(root.Value, "id");
            if (string.IsNullOrWhiteSpace(transferId))
                return null;

            return new StripeTransfer
            {
                TransferId = transferId!,
                IntentId = root.Value.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
                    ? ReadString(metadata, "azoa_intent_id") : null,
                AmountMinorUnits = ReadInt64(root.Value, "amount") ?? 0L,
                AmountReversedMinorUnits = ReadInt64(root.Value, "amount_reversed") ?? 0L,
                Currency = ReadString(root.Value, "currency") ?? string.Empty,
                Destination = ReadString(root.Value, "destination"),
                Reversed = ReadBool(root.Value, "reversed"),
                Livemode = ReadBool(root.Value, "livemode"),
            };
        }
    }

    private static JsonElement? TryReadObject(string json, out JsonDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                document = null;
                return null;
            }

            return document.RootElement;
        }
        catch (JsonException)
        {
            document?.Dispose();
            document = null;
            return null;
        }
    }

    /// <summary>
    /// How many things the provider is still waiting for. A COUNT, not the list: the list names
    /// documents and personal details that have no business in this node's logs.
    /// </summary>
    private static int CountRequirements(JsonElement root)
    {
        if (!root.TryGetProperty("requirements", out var requirements)
            || requirements.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        var count = 0;
        foreach (var name in new[] { "currently_due", "past_due" })
        {
            if (requirements.TryGetProperty(name, out var due) && due.ValueKind == JsonValueKind.Array)
                count += due.GetArrayLength();
        }

        return count;
    }

    private static string? ReadRequirementsString(JsonElement root, string name)
        => root.TryGetProperty("requirements", out var requirements)
            && requirements.ValueKind == JsonValueKind.Object
                ? ReadString(requirements, name)
                : null;

    /// <summary>Parses only the authoritative bank payout facts used by reconciliation.</summary>
    public static StripeBankPayout? ParseBankPayout(string json)
    {
        var root = TryReadObject(json, out var document);
        if (root is null)
            return null;
        using (document)
        {
            var id = ReadString(root.Value, "id");
            if (string.IsNullOrWhiteSpace(id))
                return null;
            return new StripeBankPayout
            {
                PayoutId = id,
                Status = ReadString(root.Value, "status") ?? string.Empty,
                AmountMinorUnits = ReadInt64(root.Value, "amount") ?? 0,
                Currency = ReadString(root.Value, "currency") ?? string.Empty,
                Livemode = ReadBool(root.Value, "livemode"),
                IntentId = root.Value.TryGetProperty("metadata", out var metadata)
                    ? ReadString(metadata, "azoa_intent_id") : null,
            };
        }
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static long? ReadInt64(JsonElement root, string name)
        => root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var parsed)
                ? parsed
                : null;

    private static DateTimeOffset? ReadUnixSeconds(JsonElement root, string name)
    {
        var seconds = ReadInt64(root, name);
        return seconds is null ? null : DateTimeOffset.FromUnixTimeSeconds(seconds.Value);
    }
}

/// <summary>
/// The fixed refusals the cash-out path is allowed to return, in the vocabulary of the thesis.
///
/// <para>Separate from <see cref="FiatConsumerErrors"/> because the direction matters to the
/// person reading it: "adding funds is not available" is the wrong sentence to show somebody who
/// is trying to take money OUT, and a message that describes the wrong operation reads as a bug
/// even when the refusal is correct. The only variable part is the opaque correlation id.</para>
/// </summary>
public static class FiatPayoutErrors
{
    public const string AwaitingFundsCode = "PAYOUT_AWAITING_FUNDS";
    /// <summary>Wire code for a payout rail this node cannot use at all right now.</summary>
    public const string PayoutUnavailableCode = "PAYOUT_UNAVAILABLE";

    /// <summary>Wire code for a reachable-but-busy payout rail.</summary>
    public const string PayoutBusyCode = "PAYOUT_BUSY";

    /// <summary>Wire code for a request the payout rail rejected outright.</summary>
    public const string PayoutRefusedCode = "PAYOUT_REFUSED";

    private const string UnavailableMessage =
        "Cashing out is not available on this node right now.";

    private const string BusyMessage =
        "The payment service is busy. Nothing was sent; try again shortly.";

    private const string RefusedMessage =
        "This cash-out could not be sent. Nothing left your account.";

    public static AZOAResult<T> RailUnavailable<T>() =>
        AZOAResult<T>.FailureWithCode(UnavailableMessage, PayoutUnavailableCode);

    public static AZOAResult<T> RailBusy<T>(string? correlationId = null) =>
        AZOAResult<T>.FailureWithCode(WithReference(BusyMessage, correlationId), PayoutBusyCode);

    public static AZOAResult<T> RailRefused<T>(string? correlationId = null) =>
        AZOAResult<T>.FailureWithCode(WithReference(RefusedMessage, correlationId), PayoutRefusedCode);

    private static string WithReference(string message, string? correlationId) =>
        string.IsNullOrWhiteSpace(correlationId) ? message : $"{message} Reference: {correlationId}.";
}
