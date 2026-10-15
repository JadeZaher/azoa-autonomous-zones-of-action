// SPDX-License-Identifier: UNLICENSED

using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;   // SurrealRecordParam (record<> binding, never a bare hex id)
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Fiat;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;   // SurrealTransientConflict (retryable 3.x txn conflicts)
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.Fiat;

/// <summary>Durable states of one inbox row. Mirrors the POCO allow-list.</summary>
public static class FiatEventStatuses
{
    /// <summary>Signature-verified and stored; no handler has finished with it.</summary>
    public const string Received = "Received";

    /// <summary>A handler completed the work this event describes.</summary>
    public const string Applied = "Applied";

    /// <summary>No registered handler claims this event type.</summary>
    public const string Ignored = "Ignored";

    /// <summary>A handler failed. The delivery is retried by the provider.</summary>
    public const string Failed = "Failed";

    /// <summary>
    /// The statuses of a delivery no handler has finished with — the operator read's filter.
    /// </summary>
    /// <remarks>Exposed read-only: a shared mutable array would let one caller silently widen or
    /// narrow what the board shows for every other caller.</remarks>
    public static readonly IReadOnlyList<string> NeedingOperatorStatuses = new[] { Received, Failed };

    /// <summary>
    /// True when this node already answered the provider "stop retrying" for a delivery it never
    /// finished — so only a person will move it.
    /// </summary>
    /// <remarks>
    /// A row is written <see cref="Received"/> at admission with no attempts. The only thing that
    /// settles a row back to <see cref="Received"/> is
    /// <see cref="FiatEventDispositions.ToRowStatus"/> mapping
    /// <see cref="FiatEventOutcome.AcceptedPending"/>, and that path answers 202 while
    /// <c>RecordOutcomeAsync</c> counts the attempt. Received with at least one attempt therefore
    /// means the provider was told to stop; Received with none means the delivery never reached
    /// the outcome write and the provider's own retry still owns it.
    /// </remarks>
    public static bool ProviderWasToldToStopRetrying(string? status, long attemptCount) =>
        string.Equals(status, Received, StringComparison.Ordinal) && attemptCount > 0;
}

/// <summary>The four dispositions a webhook delivery can reach. Drives the HTTP status.</summary>
/// <remarks>
/// Ported from the retired sidecar's webhook-mapping section. The distinction that matters is
/// between <see cref="AcceptedPending"/> and <see cref="Failed"/>: an issuance that has been
/// submitted but not yet confirmed is NOT a failure, and returning one would make the provider
/// retry until it gave up — leaving a person charged with nothing credited.
/// </remarks>
public enum FiatEventOutcome
{
    /// <summary>No handler claims this event type. Nothing to do.</summary>
    Ignored = 0,

    /// <summary>A handler finished the work.</summary>
    Applied = 1,

    /// <summary>Work is under way and this node's own reconciliation owns it. Do not retry.</summary>
    AcceptedPending = 2,

    /// <summary>Genuine failure. The provider should retry with its own backoff.</summary>
    Failed = 3,
}

/// <summary>The verified, correlated facts of one delivery, handed to every handler.</summary>
/// <param name="Row">The durable inbox row this delivery was admitted as.</param>
/// <param name="EventType">Provider event discriminator, e.g. <c>checkout.session.completed</c>.</param>
/// <param name="CheckoutSessionId">Payment-session id, when the event names one.</param>
/// <param name="PaymentIntentId">Payment id, when the event names one.</param>
/// <param name="ChargeId">Charge id, when the event names one.</param>
/// <param name="IntentId">The node's own funding-intent id, recovered from provider correlation.</param>
/// <param name="SubjectUserId">The payer, recovered from provider correlation.</param>
/// <param name="Environment">The rail environment this delivery was accepted in.</param>
public sealed record FiatEventContext(
    FiatEvent Row,
    string EventType,
    string? CheckoutSessionId,
    string? PaymentIntentId,
    string? ChargeId,
    Guid? IntentId,
    Guid? SubjectUserId,
    RailEnvironment Environment);

/// <summary>Something that does work when a fiat event arrives.</summary>
/// <remarks>
/// A handler must be idempotent: the provider redelivers, and the inbox de-duplicates only
/// identical provider event ids, not the effects two distinct events can describe.
/// </remarks>
public interface IFiatEventHandler
{
    /// <summary>Stable name for logs and operator views.</summary>
    string Name { get; }

    /// <summary>True when this handler wants the event type.</summary>
    bool Handles(string eventType);

    /// <summary>Does the work and reports what happened.</summary>
    Task<FiatEventOutcome> ApplyAsync(FiatEventContext context, CancellationToken ct = default);
}

/// <summary>
/// A handler that turns a CLEARED PAYMENT into funds in the payer's account.
///
/// <para>This marker is load-bearing, not decorative: <c>FiatRailStartup.Validate</c> refuses to
/// boot the LIVE environment unless one is registered. Bookkeeping handlers — the intent
/// lifecycle, reversal recording — implement <see cref="IFiatEventHandler"/> only, so their
/// presence can never be mistaken for "the money path is wired".</para>
/// </summary>
public interface IFiatSettlementHandler : IFiatEventHandler
{
}

/// <summary>How several handlers' verdicts become one, and one verdict becomes one status code.</summary>
public static class FiatEventDispositions
{
    /// <summary>
    /// The worst outcome wins: Failed &gt; AcceptedPending &gt; Applied &gt; Ignored. A delivery
    /// where the intent bookkeeping succeeded and the issuance failed must still be retried.
    /// </summary>
    public static FiatEventOutcome Aggregate(IEnumerable<FiatEventOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);

        var worst = FiatEventOutcome.Ignored;
        var any = false;
        foreach (var outcome in outcomes)
        {
            any = true;
            if (Rank(outcome) > Rank(worst))
                worst = outcome;
        }

        return any ? worst : FiatEventOutcome.Ignored;
    }

    /// <summary>
    /// The HTTP status the provider is answered with (the ported webhook-mapping table).
    /// <list type="bullet">
    ///   <item><c>200</c> — applied.</item>
    ///   <item><c>202</c> — accepted: nothing to do, or work under way that this node will
    ///     finish on its own. The provider must STOP retrying.</item>
    ///   <item><c>500</c> — genuine failure; the provider retries with bounded backoff.</item>
    /// </list>
    /// A malformed or unsigned delivery never reaches here: the controller answers 400, which the
    /// provider does not retry.
    /// </summary>
    public static int ToStatusCode(FiatEventOutcome outcome) => outcome switch
    {
        FiatEventOutcome.Applied => StatusCodes.Status200OK,
        FiatEventOutcome.Ignored => StatusCodes.Status202Accepted,
        FiatEventOutcome.AcceptedPending => StatusCodes.Status202Accepted,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>The durable inbox status one outcome settles the row at.</summary>
    public static string ToRowStatus(FiatEventOutcome outcome) => outcome switch
    {
        FiatEventOutcome.Applied => FiatEventStatuses.Applied,
        FiatEventOutcome.Ignored => FiatEventStatuses.Ignored,
        // Still in flight: the row stays Received so a sweep or a redelivery picks it up. Marking
        // it Applied here is exactly the lie that makes a stuck issuance invisible.
        FiatEventOutcome.AcceptedPending => FiatEventStatuses.Received,
        _ => FiatEventStatuses.Failed,
    };

    private static int Rank(FiatEventOutcome outcome) => outcome switch
    {
        FiatEventOutcome.Ignored => 0,
        FiatEventOutcome.Applied => 1,
        FiatEventOutcome.AcceptedPending => 2,
        _ => 3,
    };
}

/// <summary>The result of offering one delivery to the inbox.</summary>
/// <param name="Row">The stored row — the one just written, or the one already there.</param>
/// <param name="Admitted">False when this provider event id had already been stored.</param>
public sealed record FiatEventAdmission(FiatEvent Row, bool Admitted);

/// <summary>Durable, idempotent inbox for verified fiat webhook deliveries.</summary>
public interface IStripeWebhookInbox
{
    /// <summary>
    /// Stores one verified delivery, or returns the row that is already there. The provider event
    /// id is the dedup key, so a redelivery never runs a handler twice for the same event.
    /// </summary>
    Task<AZOAResult<FiatEventAdmission>> AdmitAsync(FiatEvent candidate, CancellationToken ct = default);

    /// <summary>Settles one row at a terminal-ish status and records the attempt.</summary>
    Task<AZOAResult<FiatEvent>> RecordOutcomeAsync(
        string recordId, string status, string? error, CancellationToken ct = default);

    /// <summary>
    /// True when a settlement event for <paramref name="intentId"/> has actually been APPLIED.
    /// The intent read uses this rather than the intent's own state, so "your funds are in" is
    /// never asserted by bookkeeping alone.
    /// </summary>
    Task<AZOAResult<bool>> HasAppliedSettlementAsync(string intentId, CancellationToken ct = default);

    /// <summary>
    /// Deliveries no handler has finished — <c>Received</c> or <c>Failed</c> — newest first. The
    /// operator surface over the inbox.
    /// </summary>
    /// <remarks>
    /// <see cref="FiatEventDispositions.ToRowStatus"/> parks an in-flight issuance at
    /// <c>Received</c> "so a sweep or a redelivery picks it up" while
    /// <see cref="FiatEventDispositions.ToStatusCode"/> answers 202, which tells the provider to
    /// stop retrying. Without this read those two sentences add up to a delivery nobody will ever
    /// look at again.
    /// </remarks>
    Task<AZOAResult<IReadOnlyList<FiatEvent>>> ListNeedingOperatorAsync(
        int limit, CancellationToken ct = default);
}

/// <summary>
/// SurrealDB-backed webhook inbox. Colocated with the seam because
/// <c>Providers/Stores/Surreal/</c> is outside this item's file boundary; the interface is
/// <see cref="IStripeWebhookInbox"/>, so moving it later is a file move.
/// </summary>
public sealed class StripeWebhookInbox : IStripeWebhookInbox
{
    private const string Table = FiatEvent.SchemaNameConst;

    /// <summary>Cap on the stored failure text; the full detail lives in the server log.</summary>
    private const int MaxErrorLength = 1024;

    private const string UpdateClearErrorStatement =
        "UPDATE type::record($_t, $_id) SET status = $_status, last_error = NONE, "
        + "attempt_count = attempt_count + 1 RETURN AFTER";

    private const string UpdateClearErrorAppliedStatement =
        "UPDATE type::record($_t, $_id) SET status = $_status, last_error = NONE, "
        + "attempt_count = attempt_count + 1, applied_at = $_applied RETURN AFTER";

    private const string UpdateWithErrorStatement =
        "UPDATE type::record($_t, $_id) SET status = $_status, last_error = $_error, "
        + "attempt_count = attempt_count + 1 RETURN AFTER";

    private const string UpdateWithErrorAppliedStatement =
        "UPDATE type::record($_t, $_id) SET status = $_status, last_error = $_error, "
        + "attempt_count = attempt_count + 1, applied_at = $_applied RETURN AFTER";

    private const string NeedingOperatorStatement =
        "SELECT * FROM fiat_event WHERE status INSIDE $_statuses ORDER BY received_at DESC LIMIT $_limit";

    private readonly ISurrealExecutor _executor;

    /// <summary>Trims stored failure text so one long provider message cannot bloat a row.</summary>
    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    public StripeWebhookInbox(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <inheritdoc />
    public async Task<AZOAResult<FiatEventAdmission>> AdmitAsync(
        FiatEvent candidate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        try
        {
            var create = SurrealQuery
                .Of("CREATE type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t", Table)
                .WithParam("_id", candidate.Id)
                .WithParam("_body", candidate);

            var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var executed = await _executor.ExecuteAsync(create, ct);
                executed.EnsureAllOk();
                return executed;
            }, ct);

            var created = response.GetValues<FiatEvent>(0).SingleOrDefault() ?? candidate;
            return AZOAResult<FiatEventAdmission>.Success(
                new FiatEventAdmission(created, Admitted: true), "Recorded.");
        }
        catch (Exception ex)
        {
            // A CREATE that lost to an existing row is the redelivery case, not a fault — but only
            // when the row really is there now. Re-read before concluding that, so a transport
            // failure is never reported as "already seen" (which would silently drop the event).
            var existing = await GetAsync(candidate.Id, ct);
            if (!existing.IsError && existing.Result is not null)
                return AZOAResult<FiatEventAdmission>.Success(
                    new FiatEventAdmission(existing.Result, Admitted: false), "Already recorded.");

            return new AZOAResult<FiatEventAdmission>()
                .CaptureException(ex, $"Fiat event inbox write failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<FiatEvent>> RecordOutcomeAsync(
        string recordId, string status, string? error, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recordId))
            return AZOAResult<FiatEvent>.Failure("A fiat event id is required.");

        try
        {
            // Four LITERAL statements rather than one assembled string. Two reasons, both hard
            // requirements: the optional columns must be cleared with a literal NONE (a SCHEMAFULL
            // option<string> / option<datetime> refuses an explicit NULL, so a null-valued
            // parameter would fail the whole statement instead of clearing the field), and the
            // SRDB0001 analyzer refuses interpolated SurrealQL outside the safe layer.
            var hasError = !string.IsNullOrWhiteSpace(error);
            var isApplied = string.Equals(status, FiatEventStatuses.Applied, StringComparison.Ordinal);

            var statement = (hasError, isApplied) switch
            {
                (false, false) => UpdateClearErrorStatement,
                (false, true) => UpdateClearErrorAppliedStatement,
                (true, false) => UpdateWithErrorStatement,
                (true, true) => UpdateWithErrorAppliedStatement,
            };

            var update = SurrealQuery
                .Of(statement)
                .WithParam("_t", Table)
                .WithParam("_id", recordId)
                .WithParam("_status", status);

            if (hasError)
                update = update.WithParam("_error", Truncate(error!, MaxErrorLength));
            if (isApplied)
                update = update.WithParam("_applied", DateTimeOffset.UtcNow);

            var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var executed = await _executor.ExecuteAsync(update, ct);
                executed.EnsureAllOk();
                return executed;
            }, ct);

            var persisted = response.GetValues<FiatEvent>(0).SingleOrDefault();
            return persisted is null
                ? AZOAResult<FiatEvent>.Failure($"Fiat event '{recordId}' was not found.")
                : AZOAResult<FiatEvent>.Success(persisted, "Saved.");
        }
        catch (Exception ex)
        {
            return new AZOAResult<FiatEvent>()
                .CaptureException(ex, $"Fiat event outcome write failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<bool>> HasAppliedSettlementAsync(
        string intentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(intentId))
            return AZOAResult<bool>.Success(false, "Success");

        try
        {
            // intent_id is a record<hosted_session> column, so the parameter has to be bound as a
            // native record id — a bare string compares equal to nothing and would answer "no
            // funds applied" with no error at all.
            var query = SurrealQuery
                .Of("SELECT * FROM fiat_event WHERE intent_id = $_intent AND status = $_status LIMIT 1")
                .WithParam("_intent", SurrealRecordParam.Of(HostedSession.SchemaNameConst, intentId))
                .WithParam("_status", FiatEventStatuses.Applied);

            var rows = await _executor.QueryAsync<FiatEvent>(query, ct);
            return AZOAResult<bool>.Success(rows?.Any() == true, "Success");
        }
        catch (Exception ex)
        {
            return new AZOAResult<bool>()
                .CaptureException(ex, $"Fiat event read failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<IReadOnlyList<FiatEvent>>> ListNeedingOperatorAsync(
        int limit, CancellationToken ct = default)
    {
        try
        {
            var query = SurrealQuery
                .Of(NeedingOperatorStatement)
                .WithParam("_statuses", FiatEventStatuses.NeedingOperatorStatuses.ToArray())
                .WithParam("_limit", Math.Clamp(limit, 1, 500));

            var rows = await _executor.QueryAsync<FiatEvent>(query, ct);
            return AZOAResult<IReadOnlyList<FiatEvent>>.Success(
                rows?.ToList() ?? new List<FiatEvent>(), "Success");
        }
        catch (Exception ex)
        {
            // An empty board and an unavailable board are different answers. Saying "nothing is
            // outstanding" because a read faulted is how a charged-and-uncredited person stays
            // invisible.
            return new AZOAResult<IReadOnlyList<FiatEvent>>()
                .CaptureException(ex, $"Fiat event read failed: {ex.Message}");
        }
    }

    private async Task<AZOAResult<FiatEvent?>> GetAsync(string recordId, CancellationToken ct)
    {
        try
        {
            var query = SurrealQuery<FiatEvent>.Key(recordId);
            var row = await _executor.QuerySingleAsync<FiatEvent>(query, ct);
            return new AZOAResult<FiatEvent?> { Result = row, Message = row is null ? "Not found." : "Success" };
        }
        catch (Exception ex)
        {
            return new AZOAResult<FiatEvent?>().CaptureException(ex, $"Fiat event read failed: {ex.Message}");
        }
    }
}

/// <summary>The fields this node reads out of a Stripe event envelope. Amounts are NOT among them.</summary>
/// <remarks>
/// Deliberately no amount, currency or customer field. The only amount that may be converted into
/// funds is the one re-read from the provider server-side, so a body that carried an amount would
/// re-open the money-printer the retired sidecar closed. What is kept is identity and correlation.
/// </remarks>
public sealed class StripeEventEnvelope
{
    public string EventId { get; init; } = string.Empty;

    public string EventType { get; init; } = string.Empty;

    public string? ApiVersion { get; init; }

    public bool Livemode { get; init; }

    public DateTimeOffset? OccurredAt { get; init; }

    /// <summary>Id of the object the event is about (a session, a payment, a charge).</summary>
    public string? ObjectId { get; init; }

    /// <summary>Discriminator of that object, e.g. <c>checkout.session</c> or <c>charge</c>.</summary>
    public string? ObjectType { get; init; }

    public string? CheckoutSessionId { get; init; }

    public string? PaymentIntentId { get; init; }

    public string? ChargeId { get; init; }

    /// <summary>The node's own intent id, from <c>client_reference_id</c> or metadata.</summary>
    public Guid? IntentId { get; init; }

    /// <summary>The payer, from metadata.</summary>
    public Guid? SubjectUserId { get; init; }

    /// <summary>Parses the envelope. Returns null for anything this node cannot correlate at all.</summary>
    public static StripeEventEnvelope? TryParse(string rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
            return null;

        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var eventId = ReadString(root, "id");
            var eventType = ReadString(root, "type");
            if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(eventType))
                return null;

            JsonElement dataObject = default;
            var hasObject = root.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("object", out dataObject)
                && dataObject.ValueKind == JsonValueKind.Object;

            var objectId = hasObject ? ReadString(dataObject, "id") : null;
            var objectType = hasObject ? ReadString(dataObject, "object") : null;
            var isSession = string.Equals(objectType, "checkout.session", StringComparison.Ordinal);
            var isCharge = string.Equals(objectType, "charge", StringComparison.Ordinal);
            var isPaymentIntent = string.Equals(objectType, "payment_intent", StringComparison.Ordinal);

            var paymentIntentId = hasObject
                ? (isPaymentIntent ? objectId : ReadString(dataObject, "payment_intent"))
                : null;

            Guid? intentId = null;
            Guid? subjectUserId = null;
            if (hasObject)
            {
                // client_reference_id is the checkout-session carrier; metadata is the carrier on
                // every other object, because the create call stamps it on the payment as well.
                intentId = ParseCompactGuid(ReadString(dataObject, "client_reference_id"))
                    ?? ParseCompactGuid(ReadMetadata(dataObject, "azoa_intent_id"));
                subjectUserId = ParseCompactGuid(ReadMetadata(dataObject, "azoa_user_id"));
            }

            return new StripeEventEnvelope
            {
                EventId = eventId!,
                EventType = eventType!,
                ApiVersion = ReadString(root, "api_version"),
                Livemode = root.TryGetProperty("livemode", out var livemode)
                    && livemode.ValueKind == JsonValueKind.True,
                OccurredAt = ReadUnixSeconds(root, "created"),
                ObjectId = objectId,
                ObjectType = objectType,
                CheckoutSessionId = isSession ? objectId : null,
                PaymentIntentId = paymentIntentId,
                ChargeId = isCharge ? objectId : null,
                IntentId = intentId,
                SubjectUserId = subjectUserId,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadMetadata(JsonElement element, string name)
        => element.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            ? ReadString(metadata, name)
            : null;

    private static DateTimeOffset? ReadUnixSeconds(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;

    private static Guid? ParseCompactGuid(string? value)
        => Guid.TryParseExact(value, "N", out var parsed) ? parsed : null;
}

/// <summary>Builds the durable inbox row for one verified delivery.</summary>
public static class FiatEventFactory
{
    /// <summary>Deterministic record id for one provider event, so the CREATE itself de-duplicates.</summary>
    public static string RecordIdFor(string provider, string providerEventId)
    {
        var canonical = $"{provider.Trim().ToLowerInvariant()}|{providerEventId.Trim()}";
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    /// <summary>The row for one envelope. The raw body is DIGESTED, never stored.</summary>
    /// <remarks>
    /// Storing the body would put card-adjacent personal data in the node's own database for no
    /// gain: every fact a handler acts on is re-read from the provider. The digest is enough to
    /// prove afterwards which bytes were accepted.
    /// </remarks>
    public static FiatEvent Create(
        StripeEventEnvelope envelope, string rawBody, RailEnvironment environment, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(rawBody);

        return new FiatEvent
        {
            Id = RecordIdFor(FiatRailIds.StripeProviderKey, envelope.EventId),
            Provider = FiatRailIds.StripeProviderKey,
            ProviderEventId = envelope.EventId,
            EventType = envelope.EventType,
            ApiVersion = envelope.ApiVersion,
            Environment = environment.Kind == RailEnvironmentKind.Live
                ? RailEnvironmentNames.Live
                : RailEnvironmentNames.Test,
            Livemode = envelope.Livemode,
            Status = FiatEventStatuses.Received,
            IntentId = envelope.IntentId?.ToString("N"),
            SubjectUserId = envelope.SubjectUserId?.ToString("N"),
            CheckoutSessionId = envelope.CheckoutSessionId,
            PaymentIntentId = envelope.PaymentIntentId,
            ChargeId = envelope.ChargeId,
            PayloadDigest = Convert
                .ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawBody)))
                .ToLowerInvariant(),
            AttemptCount = 0,
            OccurredAt = envelope.OccurredAt,
            ReceivedAt = now,
        };
    }
}

/// <summary>
/// Moves the durable funding intent through its lifecycle when the provider reports on the
/// payment session. Bookkeeping only — it credits nothing, which is why it implements
/// <see cref="IFiatEventHandler"/> and NOT <see cref="IFiatSettlementHandler"/>.
/// </summary>
public sealed class FiatCheckoutIntentEventHandler : IFiatEventHandler
{
    private readonly IHostedSessionStore _store;
    private readonly IStripeGateway _gateway;
    private readonly FiatOptions _options;
    private readonly ILogger<FiatCheckoutIntentEventHandler> _logger;

    public FiatCheckoutIntentEventHandler(
        IHostedSessionStore store,
        IStripeGateway gateway,
        FiatOptions options,
        ILogger<FiatCheckoutIntentEventHandler> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Event types that change a funding intent's state.</summary>
    /// <remarks>Exposed as read-only: a shared mutable array would let one caller silently
    /// widen or narrow what this handler claims for every other caller.</remarks>
    public static readonly IReadOnlyList<string> HandledEventTypes = new[]
    {
        "checkout.session.completed",
        "checkout.session.async_payment_succeeded",
        "checkout.session.async_payment_failed",
        "checkout.session.expired",
    };

    /// <inheritdoc />
    public string Name => "fiat-checkout-intent";

    /// <inheritdoc />
    public bool Handles(string eventType)
        => HandledEventTypes.Contains(eventType, StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<FiatEventOutcome> ApplyAsync(FiatEventContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.IntentId is not { } intentId)
        {
            // A session this node did not create. Nothing to move.
            _logger.LogWarning(
                "A {EventType} delivery carried no funding-intent correlation and was ignored.",
                context.EventType);
            return FiatEventOutcome.Ignored;
        }

        var read = await _store.GetByIdAsync(intentId, ct);
        if (read.IsError)
            return FiatEventOutcome.Failed;
        if (read.Result is null
            || !string.Equals(read.Result.FlowKind, HostedSessionFlowKinds.FiatCheckout, StringComparison.Ordinal))
        {
            return FiatEventOutcome.Ignored;
        }

        var session = read.Result;
        if (HostedSessionStates.IsTerminal(session.State))
            return FiatEventOutcome.Applied;   // a redelivery after the single terminal write

        if (string.Equals(context.EventType, "checkout.session.expired", StringComparison.Ordinal))
            return await TransitionAsync(intentId, session, HostedSessionStates.Abandoned, "EXPIRED", ct);

        if (string.Equals(context.EventType, "checkout.session.async_payment_failed", StringComparison.Ordinal))
            return await TransitionAsync(intentId, session, HostedSessionStates.Failed, "PAYMENT_FAILED", ct);

        // The body is an untrusted TRIGGER. What actually happened is re-read from the provider,
        // exactly as the KYC hosted flow re-fetches its verdict.
        if (string.IsNullOrWhiteSpace(context.CheckoutSessionId))
            return FiatEventOutcome.Ignored;

        var fetched = await _gateway.GetCheckoutSessionAsync(
            _options.CredentialsFor(context.Environment), context.CheckoutSessionId!, ct);
        if (fetched.IsError || fetched.Result is null)
            return FiatEventOutcome.Failed;

        return FiatRailProvider.ClassifySession(fetched.Result) switch
        {
            ChainConfirmation.Confirmed =>
                await TransitionAsync(intentId, session, HostedSessionStates.Completed, "PAID", ct),
            ChainConfirmation.FailedOnChain =>
                await TransitionAsync(intentId, session, HostedSessionStates.Failed, "PAYMENT_FAILED", ct),
            // Still clearing (an asynchronous payment method). Parking it non-terminally is the
            // whole reason AcceptedPending exists: the provider must not retry, and the intent
            // must not terminalise on a payment that has not cleared.
            _ => await ParkAsync(intentId, session, ct),
        };
    }

    private async Task<FiatEventOutcome> TransitionAsync(
        Guid intentId, HostedSession session, string newState, string outcome, CancellationToken ct)
    {
        var transitioned = await _store.TryTransitionAsync(
            intentId,
            session.StateVersion,
            newState,
            outcome,
            outcomeDetail: null,
            resumeTargetId: null,
            ct);

        if (transitioned.IsError)
            return FiatEventOutcome.Failed;

        // Losing the compare-and-swap is the EXPECTED outcome for every redelivery after the
        // first, not an error: some other delivery already made this exact transition.
        return FiatEventOutcome.Applied;
    }

    private async Task<FiatEventOutcome> ParkAsync(Guid intentId, HostedSession session, CancellationToken ct)
    {
        if (string.Equals(session.State, HostedSessionStates.AwaitingOutcome, StringComparison.Ordinal))
            return FiatEventOutcome.AcceptedPending;

        var parked = await _store.TryTransitionAsync(
            intentId,
            session.StateVersion,
            HostedSessionStates.AwaitingOutcome,
            authoritativeOutcome: null,
            outcomeDetail: null,
            resumeTargetId: null,
            ct);

        return parked.IsError ? FiatEventOutcome.Failed : FiatEventOutcome.AcceptedPending;
    }
}
