// SPDX-License-Identifier: UNLICENSED

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Services.Fiat.OffRamp;
using AZOA.WebAPI.Services.Kyb;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AZOA.WebAPI.Tests.Fiat;

/// <summary>
/// The checkout surface as a caller meets it: <see cref="FiatCheckoutController"/> (and the tenant
/// top-up controller) over the real <see cref="FiatRailProvider"/>, the real <see cref="StripeGateway"/>
/// and the in-memory hosted-session store. Only Stripe's HTTP is fake.
/// </summary>
internal sealed class FiatCheckoutHarness
{
    public FiatCheckoutClock Clock { get; } = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
    public FakeStripeCheckoutHttp Stripe { get; }
    public FiatRailProvider Rail { get; }
    public InterleavingHostedSessionStore Store { get; }
    private readonly FiatOptions _options;
    private readonly IValueRailProviderFactory _factory;

    public FiatCheckoutHarness()
    {
        _options = new FiatOptions { Enabled = true, Environment = RailEnvironmentNames.Test, ApiBaseUrl = "https://api.example" };
        _options.Test.IsEnabled = true;
        _options.Test.SecretKey = "sk_test_x";
        _options.Test.WebhookSecret = "whsec_x";
        _options.Checkout.AllowedReturnOrigins.Add("https://app.example");
        _options.Checkout.AllowedPurposes.Add(FiatCheckoutPurposes.FundsTopUp);
        _options.Checkout.AllowedPurposes.Add(TenantPayoutTopUp.Purpose);
        _options.Checkout.DefaultSuccessUrl = "https://app.example/funds/done";
        _options.Checkout.DefaultCancelUrl = "https://app.example/funds";

        Stripe = new FakeStripeCheckoutHttp();
        var httpClients = new Mock<IHttpClientFactory>();
        httpClients.Setup(factory => factory.CreateClient(StripeGateway.HttpClientName)).Returns(() => new HttpClient(Stripe, disposeHandler: false));
        var gateway = new StripeGateway(httpClients.Object, NullLogger<StripeGateway>.Instance);

        var kyc = new Mock<IRealValueKycGate>();
        kyc.Setup(gate => gate.RequireCurrentApprovalAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<bool>.Success(true));
        var kyb = new Mock<IKybGate>();
        kyb.Setup(gate => gate.RequireVerifiedTenantAsync(It.IsAny<Guid>(), It.IsAny<KybCapability>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<bool>.Success(true));
        Store = new InterleavingHostedSessionStore(new InMemoryHostedSessionStore());
        var services = new ServiceCollection()
            .AddSingleton<IHostedSessionStore>(Store)
            .AddSingleton(kyc.Object)
            .AddSingleton(kyb.Object)
            .BuildServiceProvider();

        Rail = new FiatRailProvider(_options, gateway, services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<FiatRailProvider>.Instance, Clock);
        _factory = new SingleFiatRailFactory(Rail);
    }

    /// <summary>An explicit, un-namespaced admission key — what the rail sees once the controller (or an attacker) has built one.</summary>
    public FiatCheckoutRequest Request(Guid subject, string admissionKey) => new()
    {
        SubjectUserId = subject,
        AdmissionKey = admissionKey,
        AmountMinorUnits = 1_000,
    };

    public FiatCheckoutController Controller(Guid caller) => new(_factory, _options)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = FirstParty(caller) } },
    };

    public TenantPayoutBalanceController TopUpController(Guid tenant) => new(Mock.Of<ITenantPayoutFunding>(), _factory, _options)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = FirstParty(tenant) } },
    };

    public Task<ActionResult<AZOAResult<FiatCheckoutIntent>>> StartAsync(Guid subject, string requestKey) =>
        Controller(subject).Start(subject, new FiatCheckoutStartRequest { RequestKey = requestKey, AmountMinorUnits = 1_000 });

    public Task<ActionResult<AZOAResult<FiatCheckoutVoidResult>>> VoidAsync(Guid subject, string requestKey, Guid? caller = null) =>
        Controller(caller ?? subject).VoidRequest(subject, requestKey);

    /// <summary>The durable row a first-party request key landed on, found the way the controller names it.</summary>
    public async Task<HostedSession?> RowForKeyAsync(Guid subject, string requestKey) =>
        (await Store.GetByAdmissionAsync(HostedSessionFlowKinds.FiatCheckout, FiatRailIds.StripeProviderKey,
            $"{subject:N}:self:{requestKey}")).Result;

    public async Task<HostedSession> RowAsync(Guid intentId) => (await Store.GetByIdAsync(intentId)).Result!;

    /// <summary>Moves the claim as the webhook inbox / settlement handler would, without a real delivery.</summary>
    public async Task TransitionAsync(Guid intentId, string state)
    {
        var row = await RowAsync(intentId);
        var transitioned = await Store.TryTransitionAsync(intentId, row.StateVersion, state, null, null, null);
        transitioned.Result.Should().NotBeNull();
    }

    public static (int Http, AZOAResult<T> Body) Unwrap<T>(ActionResult<AZOAResult<T>> result)
    {
        var answer = result.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        return (answer.StatusCode ?? 200, answer.Value.Should().BeOfType<AZOAResult<T>>().Subject);
    }

    private static ClaimsPrincipal FirstParty(Guid user) =>
        new(new ClaimsIdentity(new[] { new Claim("UserId", user.ToString()) }, "Test"));
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class FiatCheckoutClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public void Advance(TimeSpan by) => _now += by;
    public override DateTimeOffset GetUtcNow() => _now;
}

internal sealed class SingleFiatRailFactory(IValueRailProvider provider) : IValueRailProviderFactory
{
    public IValueRailProvider GetRailProvider(string railId, RailEnvironment environment) => provider;

    public bool TryGetRailProvider(
        string railId, RailEnvironment environment, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IValueRailProvider? result)
    {
        result = provider;
        return true;
    }

    public IReadOnlyCollection<string> RegisteredRailIds => new[] { FiatRailIds.UsdRail };
    public string DefaultRailId => FiatRailIds.UsdRail;
}

/// <summary>How the next Checkout Session expire request goes wrong, if it does.</summary>
public enum ExpireFault
{
    None,

    /// <summary>The connection fails before Stripe sees the request: nothing executed, nothing remembered.</summary>
    Unreachable,

    /// <summary>A same-key request is still running at Stripe: a 409, nothing executed.</summary>
    InFlightConflict,

    /// <summary>Stripe expires the session, then the connection drops before the answer arrives.</summary>
    ResponseLost,

    /// <summary>The payer finishes paying a moment before the expire arrives, so Stripe refuses it.</summary>
    PayerCompletesFirst,
}

/// <summary>
/// Stripe's Checkout Sessions HTTP surface: same idempotency key + same form → the same session (create),
/// the first EXECUTED answer replayed for a reused key (expire), scripted faults, and a way to move a
/// session the way a payer's actions at Stripe would.
/// </summary>
internal sealed class FakeStripeCheckoutHttp : HttpMessageHandler
{
    private const string SessionsPath = "/v1/checkout/sessions";
    private readonly Dictionary<string, (string Form, FakeStripeSession Session)> _byKey = new();
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _expireAnswers = new();
    private readonly List<string> _expireKeys = new();
    private readonly object _gate = new();

    public int SessionsCreated { get; private set; }

    /// <summary>Checkout Session GETs that reached Stripe (a scripted failure counts).</summary>
    public int SessionReads { get; private set; }

    public bool LoseNextCreateResponse { get; set; }
    public bool FailNextGet { get; set; }
    public bool NextCreateCollidesWithInFlightRequest { get; set; }
    public ExpireFault NextExpireFault { get; set; }

    /// <summary>Expire requests Stripe actually executed (a replayed key is not executed again).</summary>
    public int ExpireExecutions { get; private set; }

    /// <summary>The Idempotency-Key of every expire request that left the node, in order.</summary>
    public IReadOnlyList<string> ExpireIdempotencyKeys
    {
        get { lock (_gate) return _expireKeys.ToArray(); }
    }

    /// <summary>Moves the session Stripe holds for <paramref name="intentId"/>, as the payer's actions there would.</summary>
    public void Settle(Guid intentId, string status, string paymentStatus)
    {
        lock (_gate)
        {
            var session = SessionFor(intentId);
            session.Status = status;
            session.PaymentStatus = paymentStatus;
        }
    }

    public string UrlOf(Guid intentId)
    {
        lock (_gate)
            return SessionFor(intentId).Url;
    }

    public string StatusOf(Guid intentId)
    {
        lock (_gate)
            return SessionFor(intentId).Status;
    }

    public string SessionIdOf(Guid intentId)
    {
        lock (_gate)
            return SessionFor(intentId).Id;
    }

    /// <summary>Every session Stripe holds for an intent; empty when none was ever created.</summary>
    public IReadOnlyList<string> StatusesFor(string intentId)
    {
        lock (_gate)
            return _byKey.Values.Select(entry => entry.Session).Where(session => session.ClientReferenceId == intentId)
                .Select(session => session.Status).ToArray();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var key = request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : string.Empty;
        if (request.Method == HttpMethod.Post && path == SessionsPath)
            return Create(key, await request.Content!.ReadAsStringAsync(cancellationToken));

        if (request.Method == HttpMethod.Post && path.StartsWith(SessionsPath + "/", StringComparison.Ordinal)
            && path.EndsWith("/expire", StringComparison.Ordinal))
            return Expire(Uri.UnescapeDataString(path[(SessionsPath.Length + 1)..^"/expire".Length]), key);

        if (request.Method == HttpMethod.Get && path.StartsWith(SessionsPath + "/", StringComparison.Ordinal))
            return Read(Uri.UnescapeDataString(path[(SessionsPath.Length + 1)..]));

        return Error(HttpStatusCode.NotFound, "resource_missing");
    }

    private HttpResponseMessage Create(string idempotencyKey, string form)
    {
        lock (_gate)
        {
            if (_byKey.TryGetValue(idempotencyKey, out var existing))
                return existing.Form == form
                    ? Json(HttpStatusCode.OK, existing.Session.ToJson())
                    : Error(HttpStatusCode.BadRequest, "idempotency_error");

            SessionsCreated++;
            var fields = ParseForm(form);
            var session = new FakeStripeSession
            {
                Id = $"cs_test_{SessionsCreated}",
                Url = $"https://checkout.stripe.example/cs_test_{SessionsCreated}",
                AmountTotal = long.Parse(fields["line_items[0][price_data][unit_amount]"]),
                Currency = fields["line_items[0][price_data][currency]"],
                ExpiresAt = fields.TryGetValue("expires_at", out var expires) ? long.Parse(expires) : null,
                ClientReferenceId = fields["client_reference_id"],
            };
            _byKey[idempotencyKey] = (form, session);

            if (NextCreateCollidesWithInFlightRequest)
            {
                // Another request holding this key is still running at Stripe; it will finish
                // and own the session, but THIS request is told the key is busy.
                NextCreateCollidesWithInFlightRequest = false;
                return Error(HttpStatusCode.Conflict, StripeGateway.IdempotencyKeyInUseErrorCode);
            }

            if (LoseNextCreateResponse)
            {
                LoseNextCreateResponse = false;
                throw new HttpRequestException("The connection was reset after Stripe created the session.");
            }

            return Json(HttpStatusCode.OK, session.ToJson());
        }
    }

    private HttpResponseMessage Expire(string sessionId, string idempotencyKey)
    {
        lock (_gate)
        {
            _expireKeys.Add(idempotencyKey);
            var fault = NextExpireFault;
            NextExpireFault = ExpireFault.None;

            if (fault == ExpireFault.Unreachable)
                throw new HttpRequestException("Stripe could not be reached.");
            if (fault == ExpireFault.InFlightConflict)
                return Error(HttpStatusCode.Conflict, StripeGateway.IdempotencyKeyInUseErrorCode);

            // Stripe replays the first executed answer for a reused idempotency key.
            if (_expireAnswers.TryGetValue(idempotencyKey, out var replayed))
                return Json(replayed.Status, replayed.Body);

            var session = _byKey.Values.Select(entry => entry.Session).FirstOrDefault(candidate => candidate.Id == sessionId);
            if (session is null)
                return Error(HttpStatusCode.NotFound, "resource_missing");

            if (fault == ExpireFault.PayerCompletesFirst)
            {
                session.Status = "complete";
                session.PaymentStatus = "paid";
            }

            ExpireExecutions++;
            (HttpStatusCode Status, string Body) answer;
            if (session.Status == "open")
            {
                session.Status = "expired";
                answer = (HttpStatusCode.OK, session.ToJson());
            }
            else
            {
                // Stripe refuses to expire a session that is no longer open (invalid_request_error).
                answer = (HttpStatusCode.BadRequest, ErrorBody(code: null));
            }

            _expireAnswers[idempotencyKey] = answer;
            if (fault == ExpireFault.ResponseLost)
                throw new HttpRequestException("The connection was reset after Stripe expired the session.");

            return Json(answer.Status, answer.Body);
        }
    }

    private HttpResponseMessage Read(string sessionId)
    {
        lock (_gate)
        {
            SessionReads++;
            if (FailNextGet)
            {
                FailNextGet = false;
                throw new HttpRequestException("Stripe could not be reached.");
            }

            var found = _byKey.Values.Select(entry => entry.Session).FirstOrDefault(session => session.Id == sessionId);
            return found is null ? Error(HttpStatusCode.NotFound, "resource_missing") : Json(HttpStatusCode.OK, found.ToJson());
        }
    }

    private FakeStripeSession SessionFor(Guid intentId) =>
        _byKey.Values.Select(entry => entry.Session).Single(session => session.ClientReferenceId == intentId.ToString("N"));

    private static Dictionary<string, string> ParseForm(string form) => form
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2))
        .ToDictionary(
            parts => Uri.UnescapeDataString(parts[0].Replace('+', ' ')),
            parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode status, string code) => Json(status, ErrorBody(code));

    private static string ErrorBody(string? code) => JsonSerializer.Serialize(new
    {
        error = new { code, type = "invalid_request_error", message = "Stripe-side detail that must never reach a consumer." },
    });
}

internal sealed class FakeStripeSession
{
    public string Id { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string Status { get; set; } = "open";
    public string PaymentStatus { get; set; } = "unpaid";
    public long AmountTotal { get; init; }
    public string Currency { get; init; } = "usd";
    public long? ExpiresAt { get; init; }
    public string ClientReferenceId { get; init; } = string.Empty;

    public string ToJson() => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["id"] = Id,
        ["object"] = "checkout.session",
        ["url"] = Status == "open" ? Url : null,
        ["status"] = Status,
        ["payment_status"] = PaymentStatus,
        ["amount_total"] = AmountTotal,
        ["currency"] = Currency,
        ["livemode"] = false,
        ["expires_at"] = ExpiresAt,
        ["client_reference_id"] = ClientReferenceId,
    });
}

/// <summary>
/// The in-memory store with one-shot hooks that run another caller's request at an exact point in a
/// begin — just before its creation fence, or just before its attach — so a race is replayed
/// deterministically instead of hoped for.
/// </summary>
internal sealed class InterleavingHostedSessionStore(InMemoryHostedSessionStore inner) : IHostedSessionStore
{
    private Func<Task>? _beforeBeginCreation;
    private Func<Task>? _beforeAttach;
    private Func<Task>? _beforeTransition;

    /// <summary>Runs once, before the next creation-fence CAS.</summary>
    public Func<Task>? BeforeBeginCreation { set => _beforeBeginCreation = value; }

    /// <summary>Runs once, before the next provider-session attach CAS.</summary>
    public Func<Task>? BeforeAttach { set => _beforeAttach = value; }

    /// <summary>Runs once, before the next state-transition CAS.</summary>
    public Func<Task>? BeforeTransition { set => _beforeTransition = value; }

    public Task<AZOAResult<HostedSession>> ClaimAdmissionAsync(HostedSession candidate, CancellationToken ct = default)
        => inner.ClaimAdmissionAsync(candidate, ct);

    public Task<bool> ReleaseUnsentAdmissionAsync(Guid id, string creationToken, CancellationToken ct = default)
        => inner.ReleaseUnsentAdmissionAsync(id, creationToken, ct);

    public async Task<AZOAResult<HostedSession>> TryBeginCreationAsync(Guid id, string creationToken, DateTimeOffset now, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _beforeBeginCreation, null) is { } hook)
            await hook();
        return await inner.TryBeginCreationAsync(id, creationToken, now, ct);
    }

    public Task<IReadOnlyList<HostedSession>> ListReconciliationAsync(DateTimeOffset before, int limit, CancellationToken ct = default)
        => inner.ListReconciliationAsync(before, limit, ct);

    public Task<AZOAResult<HostedSession>> ReconcileAdmissionAsync(HostedSession expected, Guid actorId, string reason,
        HostedSessionCreation? providerSession, DateTimeOffset now, CancellationToken ct = default)
        => inner.ReconcileAdmissionAsync(expected, actorId, reason, providerSession, now, ct);

    public Task<AZOAResult<HostedSession>> GetByProviderSessionAsync(string flowKind, string providerKey, string providerSessionId, CancellationToken ct = default)
        => inner.GetByProviderSessionAsync(flowKind, providerKey, providerSessionId, ct);

    public Task<AZOAResult<HostedSession>> GetByAdmissionAsync(string flowKind, string providerKey, string admissionKey, CancellationToken ct = default)
        => inner.GetByAdmissionAsync(flowKind, providerKey, admissionKey, ct);

    public Task<AZOAResult<HostedSession>> GetByIdAsync(Guid id, CancellationToken ct = default)
        => inner.GetByIdAsync(id, ct);

    public async Task<AZOAResult<HostedSession>> TryAttachProviderSessionAsync(Guid id, long expectedStateVersion, string providerSessionId,
        string? providerCorrelationId, string redirectUrl, DateTimeOffset? expiresAt, string creationToken, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _beforeAttach, null) is { } hook)
            await hook();
        return await inner.TryAttachProviderSessionAsync(id, expectedStateVersion, providerSessionId, providerCorrelationId,
            redirectUrl, expiresAt, creationToken, ct);
    }

    public async Task<AZOAResult<HostedSession>> TryTransitionAsync(Guid id, long expectedStateVersion, string newState,
        string? authoritativeOutcome, string? outcomeDetail, string? resumeTargetId, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _beforeTransition, null) is { } hook)
            await hook();
        return await inner.TryTransitionAsync(id, expectedStateVersion, newState, authoritativeOutcome, outcomeDetail, resumeTargetId, ct);
    }

    public Task<AZOAResult<HostedSession>> TryClaimResumeAsync(Guid id, long expectedStateVersion, string token, DateTimeOffset now,
        DateTimeOffset leaseExpiresAt, CancellationToken ct = default)
        => inner.TryClaimResumeAsync(id, expectedStateVersion, token, now, leaseExpiresAt, ct);

    public Task<bool> RenewResumeLeaseAsync(Guid id, string token, DateTimeOffset now, DateTimeOffset leaseExpiresAt, CancellationToken ct = default)
        => inner.RenewResumeLeaseAsync(id, token, now, leaseExpiresAt, ct);

    public Task<AZOAResult<HostedSession>> TryFinishResumeAsync(Guid id, long expectedStateVersion, string token, DateTimeOffset now,
        string newState, string? authoritativeOutcome, string? outcomeDetail, string? resumeTargetId, CancellationToken ct = default)
        => inner.TryFinishResumeAsync(id, expectedStateVersion, token, now, newState, authoritativeOutcome, outcomeDetail, resumeTargetId, ct);

    public Task<AZOAResult<bool>> RecordCallbackAsync(Guid id, CancellationToken ct = default)
        => inner.RecordCallbackAsync(id, ct);
}
