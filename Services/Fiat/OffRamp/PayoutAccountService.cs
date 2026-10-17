// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Fiat;

namespace AZOA.WebAPI.Services.Fiat.OffRamp;

/// <summary>Hosted-flow identity for payout-account setup. Reuses the hosted-session seam.</summary>
public static class PayoutAccountFlow
{
    /// <summary>
    /// The hosted-session flow kind payout onboarding is claimed under. It is a hosted redirect
    /// flow with a durable admission and a provider-side id, which is exactly what that table is
    /// for — so the person's connected account lives there rather than in a second table nobody
    /// else would ever read.
    /// </summary>
    public const string FlowKind = "fiat-connect-onboarding";

    /// <summary>One payout account per person: the admission key is derived, never caller-chosen.</summary>
    public static string AdmissionKeyFor(Guid subjectUserId) => $"{subjectUserId:N}:payout-account";
}

/// <summary>
/// What a person's payout account can do right now, in the words they read on the page.
/// </summary>
/// <param name="Available">Whether this node offers cashing out at all.</param>
/// <param name="AccountOpened">Whether a payout account exists for this person yet.</param>
/// <param name="ReadyToReceive">Whether money may actually be sent to it.</param>
/// <param name="DetailsSubmitted">Whether they have finished the provider's form.</param>
/// <param name="OutstandingRequirementCount">How many things the provider is still waiting for.</param>
/// <param name="SetupUrl">The hosted setup page, when there is something left to do.</param>
/// <param name="SetupUrlExpiresAt">When that link stops working.</param>
/// <param name="StatusMessage">One sentence a person can act on.</param>
public sealed record PayoutAccountStatus(
    bool Available,
    bool AccountOpened,
    bool ReadyToReceive,
    bool DetailsSubmitted,
    int OutstandingRequirementCount,
    string? SetupUrl,
    DateTimeOffset? SetupUrlExpiresAt,
    string StatusMessage);

/// <summary>
/// The person's own payout account: opened once, at the first cash-out, and re-read from the
/// provider every time it matters. See <c>Services/Fiat/OffRamp/AGENTS.md</c> §account.
/// </summary>
public interface IPayoutAccountService
{
    /// <summary>Reads where this person's payout account stands. Never creates anything.</summary>
    Task<AZOAResult<PayoutAccountStatus>> GetStatusAsync(
        Guid subjectUserId, CancellationToken ct = default);

    /// <summary>
    /// Opens the account if it does not exist and hands back a fresh setup link. Idempotent: the
    /// durable admission means a second call resumes the same account.
    /// </summary>
    Task<AZOAResult<PayoutAccountStatus>> BeginSetupAsync(
        Guid subjectUserId, Guid? tenantId, CancellationToken ct = default);

    /// <summary>
    /// The provider account id money may be sent to, or a refusal. The ONLY way the off-ramp is
    /// allowed to learn a destination — a caller-supplied one would be a way to redirect somebody
    /// else's cash-out into your own bank.
    /// </summary>
    Task<AZOAResult<string>> RequireReadyAccountAsync(
        Guid subjectUserId, CancellationToken ct = default);
}

/// <summary>
/// Payout accounts over the hosted-session seam: one durable admission per person, whose
/// provider session id IS their connected account.
/// </summary>
/// <remarks>
/// <para>Readiness is never stored. A connected account's payout capability can be withdrawn by
/// the provider at any moment — a missing document, a failed verification — so a cached
/// "onboarded" flag would be a lie the instant that happens, and the node would learn about it
/// by sending money that bounces. Every decision here re-reads the account server-side.</para>
/// <para>The setup LINK is short-lived by design and is not persisted past the first attach: a
/// person who comes back tomorrow gets a new one rather than a dead page.</para>
/// </remarks>
public sealed class PayoutAccountService : IPayoutAccountService
{
    /// <summary>What a person is told when this node does not offer cashing out.</summary>
    public const string UnavailableMessage = "Cashing out is not available on this node yet.";

    /// <summary>What a person is told before they have set an account up.</summary>
    public const string SetupNeededMessage =
        "Set up where your money should go. It takes a few minutes and you only do it once.";

    /// <summary>What a person is told while the provider is still checking their details.</summary>
    public const string PendingMessage =
        "Your payout details are being checked. This usually finishes within a day.";

    /// <summary>What a person is told when everything is in place.</summary>
    public const string ReadyMessage = "Your account is ready to receive money.";

    /// <summary>What a person is told when the provider will not pay the account.</summary>
    public const string BlockedMessage =
        "Your payout details need attention before money can be sent.";

    /// <summary>Prefix of the claim-bound Stripe idempotency key for opening a connected account.</summary>
    public const string IdempotencyKeyPrefix = "azoa-payout-account-";

    private readonly FiatOptions _fiat;
    private readonly OffRampOptions _options;
    private readonly IStripeConnectGateway _connect;
    private readonly IHostedSessionStore _sessions;
    private readonly ILogger<PayoutAccountService> _logger;
    private readonly TimeProvider _clock;

    public PayoutAccountService(
        FiatOptions fiat,
        OffRampOptions options,
        IStripeConnectGateway connect,
        IHostedSessionStore sessions,
        ILogger<PayoutAccountService> logger,
        TimeProvider? clock = null)
    {
        _fiat = fiat ?? throw new ArgumentNullException(nameof(fiat));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<AZOAResult<PayoutAccountStatus>> GetStatusAsync(
        Guid subjectUserId, CancellationToken ct = default)
    {
        if (!IsAvailable())
            return AZOAResult<PayoutAccountStatus>.Success(Unavailable(), "Success");

        var accountId = await FindAccountIdAsync(subjectUserId, ct);
        if (accountId.IsError)
            return AZOAResult<PayoutAccountStatus>.Failure(accountId.Message);

        if (string.IsNullOrWhiteSpace(accountId.Result))
        {
            return AZOAResult<PayoutAccountStatus>.Success(
                new PayoutAccountStatus(
                    Available: true, AccountOpened: false, ReadyToReceive: false,
                    DetailsSubmitted: false, OutstandingRequirementCount: 0,
                    SetupUrl: null, SetupUrlExpiresAt: null, SetupNeededMessage),
                "Success");
        }

        var account = await ReadAccountAsync(accountId.Result!, ct);
        if (account.IsError || account.Result is null)
            return AZOAResult<PayoutAccountStatus>.FailureWithCode(account.Message, account.Code ?? FiatPayoutErrors.PayoutBusyCode);

        return AZOAResult<PayoutAccountStatus>.Success(Describe(account.Result, setupUrl: null, expiresAt: null), "Success");
    }

    /// <inheritdoc />
    public async Task<AZOAResult<PayoutAccountStatus>> BeginSetupAsync(
        Guid subjectUserId, Guid? tenantId, CancellationToken ct = default)
    {
        if (!IsAvailable())
            return AZOAResult<PayoutAccountStatus>.Success(Unavailable(), "Success");

        if (subjectUserId == Guid.Empty)
            return AZOAResult<PayoutAccountStatus>.FailureWithCode(
                "An authenticated account is required.", AzoaErrorCodes.InvalidRequest);

        // The return URLs are origin-allowlisted BEFORE anything is created, against the same
        // allowlist the checkout return page uses. An unbounded return URL on a page where
        // somebody types their bank details is the same open redirect, aimed at a worse target.
        if (!TryResolveReturnUrls(out var returnUrl, out var refreshUrl, out var reason))
        {
            _logger.LogError("Payout onboarding return URLs were refused: {Reason}", reason);
            return AZOAResult<PayoutAccountStatus>.Success(Unavailable(), "Success");
        }

        var now = _clock.GetUtcNow();
        var claimed = await _sessions.ClaimAdmissionAsync(
            new HostedSession
            {
                Id = Guid.NewGuid().ToString("N"),
                FlowKind = PayoutAccountFlow.FlowKind,
                ProviderKey = FiatRailIds.StripeProviderKey,
                AdmissionKey = PayoutAccountFlow.AdmissionKeyFor(subjectUserId),
                CreationToken = Guid.NewGuid().ToString("N"),
                CreationClaimedAt = now,
                SubjectUserId = subjectUserId.ToString("N"),
                TenantId = tenantId?.ToString("N"),
                State = HostedSessionStates.Claimed,
                StateVersion = 0,
                CreatedDate = now,
                ModifiedDate = now,
            },
            ct);

        if (claimed.IsError || claimed.Result is null)
            return FiatPayoutErrors.RailBusy<PayoutAccountStatus>();

        // The admission row is keyed on (flow, provider, admission key) alone. The key is DERIVED
        // from the subject here rather than taken from a caller, so this can only ever disagree
        // if a row was written by something else — refuse rather than hand back another person's
        // payout account.
        if (!string.Equals(claimed.Result.SubjectUserId, subjectUserId.ToString("N"), StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("A payout-account admission is claimed by a different account than its key names.");
            return FiatPayoutErrors.RailBusy<PayoutAccountStatus>();
        }

        var opened = await OpenAccountAsync(claimed.Result, subjectUserId, returnUrl!.ToString(), now, ct);
        if (opened.IsError || string.IsNullOrWhiteSpace(opened.Result))
            return AZOAResult<PayoutAccountStatus>.FailureWithCode(
                opened.Message, opened.Code ?? FiatPayoutErrors.PayoutBusyCode);
        var accountId = opened.Result;

        var link = await _connect.CreateAccountLinkAsync(
            _fiat.CredentialsFor(_fiat.ActiveEnvironment),
            new StripeAccountLinkRequest
            {
                AccountId = accountId!,
                ReturnUrl = returnUrl!.ToString(),
                RefreshUrl = refreshUrl!.ToString(),
            },
            ct);

        if (link.IsError || link.Result is null)
            return AZOAResult<PayoutAccountStatus>.FailureWithCode(
                link.Message, link.Code ?? FiatPayoutErrors.PayoutBusyCode);

        var account = await ReadAccountAsync(accountId!, ct);
        if (account.IsError || account.Result is null)
            return AZOAResult<PayoutAccountStatus>.FailureWithCode(
                account.Message, account.Code ?? FiatPayoutErrors.PayoutBusyCode);

        return AZOAResult<PayoutAccountStatus>.Success(
            Describe(account.Result, link.Result.Url, link.Result.ExpiresAt), "Success");
    }

    /// <inheritdoc />
    public async Task<AZOAResult<string>> RequireReadyAccountAsync(
        Guid subjectUserId, CancellationToken ct = default)
    {
        if (!IsAvailable())
            return FiatPayoutErrors.RailUnavailable<string>();

        var accountId = await FindAccountIdAsync(subjectUserId, ct);
        if (accountId.IsError)
            return AZOAResult<string>.FailureWithCode(accountId.Message, FiatPayoutErrors.PayoutBusyCode);

        if (string.IsNullOrWhiteSpace(accountId.Result))
            return AZOAResult<string>.FailureWithCode(SetupNeededMessage, AzoaErrorCodes.InvalidRequest);

        var account = await ReadAccountAsync(accountId.Result!, ct);
        if (account.IsError || account.Result is null)
            return AZOAResult<string>.FailureWithCode(
                account.Message, account.Code ?? FiatPayoutErrors.PayoutBusyCode);

        if (!account.Result.PayoutsEnabled)
        {
            // Logged with the provider's own reason; the person is told a sentence they can act
            // on. "capability_disabled_requirements.past_due" helps nobody outside this log line.
            _logger.LogWarning(
                "A cash-out was refused: payout account {AccountId} cannot receive money "
                + "(reason={Reason}, outstanding={Outstanding}).",
                account.Result.AccountId, account.Result.DisabledReason ?? "none given",
                account.Result.OutstandingRequirementCount);

            return AZOAResult<string>.FailureWithCode(
                account.Result.DetailsSubmitted ? PendingMessage : SetupNeededMessage,
                AzoaErrorCodes.InvalidRequest);
        }

        return AZOAResult<string>.Success(account.Result.AccountId, "Success");
    }

    /// <summary>The connected account id on this person's admission row, or blank when none.</summary>
    private async Task<AZOAResult<string?>> FindAccountIdAsync(Guid subjectUserId, CancellationToken ct)
    {
        if (subjectUserId == Guid.Empty)
            return AZOAResult<string?>.Success(null, "Success");

        var found = await _sessions.GetByAdmissionAsync(
            PayoutAccountFlow.FlowKind, FiatRailIds.StripeProviderKey,
            PayoutAccountFlow.AdmissionKeyFor(subjectUserId), ct);

        if (found.IsError)
            return AZOAResult<string?>.Failure(found.Message);
        if (found.Result is null)
            return AZOAResult<string?>.Success(null, "Success");

        return !string.Equals(found.Result.SubjectUserId, subjectUserId.ToString("N"), StringComparison.OrdinalIgnoreCase)
            ? AZOAResult<string?>.Failure("The payout account for this person could not be resolved.")
            : AZOAResult<string?>.Success(found.Result.ProviderSessionId, "Success");
    }

    /// <summary>
    /// Opens (or resumes opening) the one connected account for this claim behind the creation
    /// fence; see <c>Services/Fiat/OffRamp/AGENTS.md</c> §account.
    /// </summary>
    private async Task<AZOAResult<string>> OpenAccountAsync(
        HostedSession claimed, Guid subjectUserId, string redirectUrl, DateTimeOffset now, CancellationToken ct)
    {
        var turn = await FiatHostedCreation.BeginOrResumeAsync(_sessions, claimed, now, ct);
        switch (turn.Verdict)
        {
            case FiatCreationVerdict.Attached:
                return AZOAResult<string>.Success(turn.Session!.ProviderSessionId!, "Success");
            case FiatCreationVerdict.NeedsOperator:
                _logger.LogError(
                    "Payout-account admission {SessionId} has an unattached creation the node can no longer replay; operator reconciliation is required.",
                    turn.Session?.Id);
                return AZOAResult<string>.FailureWithCode(
                    "Your payout account setup needs a manual check before it can continue.", AzoaErrorCodes.Conflict);
            case FiatCreationVerdict.Busy:
                return FiatPayoutErrors.RailBusy<string>();
        }

        var session = turn.Session!;
        var sessionId = Guid.ParseExact(session.Id, "N");
        var created = await _connect.CreateConnectedAccountAsync(
            _fiat.CredentialsFor(_fiat.ActiveEnvironment),
            new StripeConnectedAccountRequest
            {
                SubjectUserId = subjectUserId.ToString("N"),
                ClaimId = session.Id,
                Country = _options.ConnectedAccountCountry,
                IdempotencyKey = FiatHostedCreation.IdempotencyKey(IdempotencyKeyPrefix, session),
            },
            ct);

        // The fence stays held: the next call replays the same claim-keyed create, which Stripe
        // answers with the account it already opened (or opens the first one).
        if (created.IsError || created.Result is null || string.IsNullOrWhiteSpace(created.Result.AccountId))
            return AZOAResult<string>.FailureWithCode(
                created.Message, created.Code ?? FiatPayoutErrors.PayoutBusyCode);

        var accountId = created.Result.AccountId;
        var attached = await _sessions.TryAttachProviderSessionAsync(
            sessionId,
            session.StateVersion,
            accountId,
            providerCorrelationId: null,
            // Provenance only: the onboarding link expires within minutes and every call mints a fresh one.
            redirectUrl,
            expiresAt: null,
            session.CreationToken!,
            ct);
        if (!attached.IsError && attached.Result is not null)
            return AZOAResult<string>.Success(accountId, "Success");

        // A concurrent replay of the same claim attached first; it can only carry this account.
        var reread = await _sessions.GetByIdAsync(sessionId, ct);
        if (!reread.IsError && reread.Result?.ProviderSessionId is { } winner)
        {
            if (string.Equals(winner, accountId, StringComparison.Ordinal))
                return AZOAResult<string>.Success(accountId, "Success");
            _logger.LogError(
                "Payout-account admission {SessionId} is attached to a different account than its claim-keyed create returned.",
                session.Id);
        }

        return FiatPayoutErrors.RailBusy<string>();
    }

    /// <summary>Re-reads the account with the active environment's credentials and verifies its identity.</summary>
    private async Task<AZOAResult<StripeConnectedAccount>> ReadAccountAsync(
        string accountId, CancellationToken ct)
    {
        var account = await _connect.GetConnectedAccountAsync(
            _fiat.CredentialsFor(_fiat.ActiveEnvironment), accountId, ct);

        if (account.IsError || account.Result is null)
            return account;

        if (!string.Equals(account.Result.AccountId, accountId, StringComparison.Ordinal))
        {
            _logger.LogError(
                "Payout account {AccountId} returned a different account identifier.", accountId);
            return FiatPayoutErrors.RailUnavailable<StripeConnectedAccount>();
        }

        return account;
    }

    private PayoutAccountStatus Describe(
        StripeConnectedAccount account, string? setupUrl, DateTimeOffset? expiresAt)
    {
        var message = account.PayoutsEnabled
            ? ReadyMessage
            : account.DetailsSubmitted
                ? (account.OutstandingRequirementCount > 0 ? BlockedMessage : PendingMessage)
                : SetupNeededMessage;

        return new PayoutAccountStatus(
            Available: true,
            AccountOpened: true,
            ReadyToReceive: account.PayoutsEnabled,
            DetailsSubmitted: account.DetailsSubmitted,
            OutstandingRequirementCount: account.OutstandingRequirementCount,
            SetupUrl: account.PayoutsEnabled ? null : setupUrl,
            SetupUrlExpiresAt: account.PayoutsEnabled ? null : expiresAt,
            StatusMessage: message);
    }

    private static PayoutAccountStatus Unavailable() => new(
        Available: false, AccountOpened: false, ReadyToReceive: false, DetailsSubmitted: false,
        OutstandingRequirementCount: 0, SetupUrl: null, SetupUrlExpiresAt: null, UnavailableMessage);

    /// <summary>True only when BOTH the rail and the cash-out path are configured and switched on.</summary>
    private bool IsAvailable() =>
        _options.Enabled
        && _options.MissingKeys().Count == 0
        && _fiat.MissingKeysFor(_fiat.ActiveEnvironment).Count == 0;

    private bool TryResolveReturnUrls(out Uri? returnUrl, out Uri? refreshUrl, out string reason)
    {
        refreshUrl = null;

        if (!FiatReturnUrlPolicy.TryResolveOrigin(
                _options.OnboardingReturnUrl,
                _options.OnboardingReturnUrl,
                _fiat.Checkout.AllowedReturnOrigins,
                _fiat.ActiveEnvironment,
                out returnUrl,
                out reason))
        {
            return false;
        }

        return FiatReturnUrlPolicy.TryResolveOrigin(
            _options.OnboardingRefreshUrl,
            _options.OnboardingRefreshUrl,
            _fiat.Checkout.AllowedReturnOrigins,
            _fiat.ActiveEnvironment,
            out refreshUrl,
            out reason);
    }
}
