// SPDX-License-Identifier: UNLICENSED

using System.Security.Cryptography;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Auth;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Kyc;

namespace AZOA.WebAPI.Services.Auth;

/// <summary>
/// Resolves a verified federated identity to an AZOA user (Phase 1B, D17/D18).
/// </summary>
/// <remarks>
/// <para><b>Rule 1 — no email auto-linking, enforced structurally.</b> There is not one
/// email lookup in this file, and <see cref="IUserExternalLoginStore"/> exposes no
/// method that could perform one. An account is resolved ONLY by
/// <c>(tenant_scope, provider, subject)</c>. When no binding exists, a BRAND NEW user
/// is created with a synthetic scope-unique address — the provider's email is recorded
/// as an audit snapshot and never consulted to find an existing account. This is what
/// closes account pre-hijacking: an attacker who registers a password account on the
/// victim's email gains nothing, because the victim's later Google sign-in never looks
/// at email and so can never be merged into the attacker's account. Attaching a
/// provider to an existing user happens ONLY through <see cref="LinkToUserAsync"/>,
/// which requires an already-authenticated session and an explicit user action.</para>
///
/// <para><b>Rule 2 — OAuth is not KYC.</b> Nothing here writes <c>kyc_submission</c> or
/// <c>kyc_identity</c>, sets <c>IsVerified</c>, or touches <c>IValueAccessService</c>.
/// A federated login proves control of a provider account; the value gate asks whether
/// a PERSON was verified, which is a different question with a different evidence
/// table.</para>
///
/// <para><b>Rule 3 — per-tenant binding.</b> The uniqueness scope comes from
/// <see cref="KycPersonKeyDeriver.ScopeValue"/> — the SAME static <c>kyc_identity</c>
/// uses. That is deliberate: its own remarks warn that a second private copy of the
/// scope rule is how two tables end up in different scopes.</para>
/// </remarks>
public sealed class ExternalLoginService : IExternalLoginService
{
    /// <summary>Domain for synthetic addresses; mirrors TenantManager's onboard.azoa.local.</summary>
    private const string SyntheticEmailDomain = "oauth.azoa.local";

    private readonly IUserStore _userStore;
    private readonly IUserExternalLoginStore _loginStore;
    private readonly ILogger<ExternalLoginService> _logger;
    private readonly TimeProvider _clock;
    private readonly IConfiguration? _configuration;
    private readonly IHostEnvironment? _environment;

    public ExternalLoginService(
        IUserStore userStore,
        IUserExternalLoginStore loginStore,
        ILogger<ExternalLoginService> logger,
        TimeProvider? clock = null,
        IConfiguration? configuration = null,
        IHostEnvironment? environment = null)
    {
        _userStore = userStore ?? throw new ArgumentNullException(nameof(userStore));
        _loginStore = loginStore ?? throw new ArgumentNullException(nameof(loginStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
        _configuration = configuration;
        _environment = environment;
    }

    /// <inheritdoc />
    public async Task<AZOAResult<IUser>> ResolveOrCreateAsync(
        ExternalIdentity identity,
        Guid? tenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.Subject))
        {
            // A provider that asserted no stable subject cannot be trusted to identify
            // anyone. Fail closed rather than inventing a key from the email.
            return AZOAResult<IUser>.FailureWithCode(
                "The identity provider did not return a stable account identifier.",
                AzoaErrorCodes.InvalidRequest);
        }

        var scope = KycPersonKeyDeriver.ScopeValue(tenantId);

        var existing = await _loginStore.GetBySubjectAsync(scope, identity.Provider, identity.Subject, ct);
        if (existing.IsError)
            return AZOAResult<IUser>.FailureWithCode(existing.Message, AzoaErrorCodes.DependencyUnavailable);

        if (existing.Result is not null)
        {
            var boundResult = await LoadBoundUserAsync(existing.Result, ct);
            if (boundResult.IsError || boundResult.Result is null)
                return boundResult;

            await _loginStore.TouchLastLoginAsync(existing.Result.Id, _clock.GetUtcNow(), ct);
            return AZOAResult<IUser>.Success(boundResult.Result, "Signed in.");
        }

        return await CreateUserForIdentityAsync(identity, tenantId, scope, ct);
    }

    /// <inheritdoc />
    public async Task<AZOAResult<UserExternalLogin>> LinkToUserAsync(
        ExternalIdentity identity,
        Guid userId,
        Guid? tenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (userId == Guid.Empty)
            return AZOAResult<UserExternalLogin>.FailureWithCode(
                "An authenticated user is required to link a provider.", AzoaErrorCodes.Forbidden);
        if (string.IsNullOrWhiteSpace(identity.Subject))
            return AZOAResult<UserExternalLogin>.FailureWithCode(
                "The identity provider did not return a stable account identifier.",
                AzoaErrorCodes.InvalidRequest);

        var scope = KycPersonKeyDeriver.ScopeValue(tenantId);

        // The user id is the AUTHENTICATED caller's, threaded from the JWT through a
        // single-use LINK code — never read from the request body. Same rule as the
        // ActionGraphBundle upsert IDOR precedent: a caller-supplied identity never widens the
        // authenticated one.
        var owner = await _userStore.GetByIdAsync(userId, ct);
        if (owner.IsError || owner.Result is null)
            return AZOAResult<UserExternalLogin>.FailureWithCode(
                "User not found.", AzoaErrorCodes.NotFound);

        var existing = await _loginStore.GetBySubjectAsync(scope, identity.Provider, identity.Subject, ct);
        if (existing.IsError)
            return AZOAResult<UserExternalLogin>.FailureWithCode(
                existing.Message, AzoaErrorCodes.DependencyUnavailable);

        if (existing.Result is not null)
        {
            // Already linked. Idempotent for the same user; a conflict for any other,
            // because silently re-pointing a binding at a second user is the takeover
            // this table exists to prevent.
            return string.Equals(existing.Result.UserId, userId.ToString("N"), StringComparison.OrdinalIgnoreCase)
                ? AZOAResult<UserExternalLogin>.Success(existing.Result, "Already linked.")
                : AZOAResult<UserExternalLogin>.FailureWithCode(
                    "That provider account is already linked to a different user.",
                    AzoaErrorCodes.Conflict);
        }

        var binding = BuildBinding(identity, userId, tenantId, scope);
        return await _loginStore.CreateAsync(binding, ct);
    }

    /// <inheritdoc />
    public Task<AZOAResult<IReadOnlyList<UserExternalLogin>>> ListLinksAsync(
        Guid userId,
        CancellationToken ct = default)
        => _loginStore.GetByUserAsync(userId.ToString("N"), ct);

    /// <inheritdoc />
    public async Task<AZOAResult<IUser>> ResolveBoundUserAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        if (userId == Guid.Empty)
            return AZOAResult<IUser>.FailureWithCode("User not found.", AzoaErrorCodes.NotFound);

        var user = await _userStore.GetByIdAsync(userId, ct);
        if (user.IsError || user.Result is null)
            return AZOAResult<IUser>.FailureWithCode("User not found.", AzoaErrorCodes.NotFound);

        return user.Result.IsActive
            ? AZOAResult<IUser>.Success(user.Result, "Success")
            : AZOAResult<IUser>.FailureWithCode("This account is deactivated.", AzoaErrorCodes.Forbidden);
    }

    /// <inheritdoc />
    public async Task<AZOAResult<bool>> UnlinkAsync(
        ExternalAuthProvider provider,
        Guid userId,
        Guid? tenantId,
        CancellationToken ct = default)
    {
        var scope = KycPersonKeyDeriver.ScopeValue(tenantId);

        var user = await _userStore.GetByIdAsync(userId, ct);
        if (user.IsError || user.Result is null)
            return AZOAResult<bool>.FailureWithCode("User not found.", AzoaErrorCodes.NotFound);

        var links = await _loginStore.GetByUserAsync(userId.ToString("N"), ct);
        if (links.IsError)
            return AZOAResult<bool>.FailureWithCode(links.Message, AzoaErrorCodes.DependencyUnavailable);

        var remaining = (links.Result ?? [])
            .Where(link => link.Provider != provider)
            .ToList();

        // A revoked or deleted provider account must not orphan the user. Deleting the
        // binding never cascades to the user row — but if this were the LAST way in,
        // the account would survive with nobody able to reach it. Refuse instead, and
        // name the recovery step.
        if (remaining.Count == 0 && !HasNonFederatedCredential(user.Result))
        {
            return AZOAResult<bool>.FailureWithCode(
                "This is the only way to sign in to this account. Set a password or link "
                + "another provider first, then unlink this one.",
                AzoaErrorCodes.OperationNotAllowed);
        }

        return await _loginStore.DeleteAsync(scope, provider, userId.ToString("N"), ct);
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private async Task<AZOAResult<IUser>> LoadBoundUserAsync(
        UserExternalLogin binding,
        CancellationToken ct)
    {
        if (!Guid.TryParse(binding.UserId, out var boundUserId))
        {
            _logger.LogError(
                "External login binding {BindingId} carries an unparseable user id.", binding.Id);
            return AZOAResult<IUser>.FailureWithCode(
                "The linked account could not be loaded.", AzoaErrorCodes.DependencyUnavailable);
        }

        var user = await _userStore.GetByIdAsync(boundUserId, ct);
        if (user.IsError || user.Result is null)
        {
            // The binding outlived its user. Fail closed: never fall back to creating
            // a replacement, which would hand the provider account a fresh identity
            // carrying none of the original's history.
            return AZOAResult<IUser>.FailureWithCode(
                "The linked account is no longer available.", AzoaErrorCodes.NotFound);
        }

        if (!user.Result.IsActive)
        {
            return AZOAResult<IUser>.FailureWithCode(
                "This account is deactivated.", AzoaErrorCodes.Forbidden);
        }

        return AZOAResult<IUser>.Success(user.Result, "Success");
    }

    private async Task<AZOAResult<IUser>> CreateUserForIdentityAsync(
        ExternalIdentity identity,
        Guid? tenantId,
        string scope,
        CancellationToken ct)
    {
        // A first federated sign-in creates an account, so it asks the same switch as
        // password registration and first wallet sign-in (QA H-A).
        if (!SelfRegistrationPolicy.IsAllowed(_configuration, _environment))
            return AZOAResult<IUser>.FailureWithCode(
                SelfRegistrationPolicy.DisabledMessage,
                SelfRegistrationPolicy.DisabledCode);

        var seed = DeterministicSeed(scope, identity.Provider, identity.Subject);
        var userId = DeterministicUserId(seed);
        // 96 bits of the digest is ample to keep synthetic handles distinct without
        // stamping a 64-character hex string into a username.
        var handle = seed[..24];

        // Fully qualified: AZOA.WebAPI.Services.Users is a NAMESPACE, so a bare
        // `User` binds to it rather than to the model from inside Services.Auth.
        var candidate = new AZOA.WebAPI.Models.User
        {
            Id = userId,
            // Synthetic, scope-unique, and NOT derived from the provider's email.
            // user.email carries a global UNIQUE index, so reusing the provider
            // address would (a) collide the moment the same account signs in under a
            // second tenant — the cross-tenant case D23 requires to yield two distinct
            // users — and (b) reintroduce email as a de-facto join key. The real
            // address is kept on the binding as an audit snapshot instead.
            Username = $"oauth-{handle}",
            Email = $"oauth-{handle}@{SyntheticEmailDomain}",
            // Non-empty (user.password_hash asserts != "") but unguessable, so the
            // password path can never authenticate this user and BCrypt.Verify
            // returns false cleanly instead of throwing on a malformed hash.
            // Same construction as TenantManager.ProvisionChildAsync.
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N")),
            IsActive = true,
            // Federated sign-in proves control of a provider account, NOT a verified
            // person. IsVerified stays false and the value gate remains closed.
            IsVerified = false,
            FirstName = Trim(identity.FirstName),
            LastName = Trim(identity.LastName),
            OwnerTenantId = null,
            ExternalRef = $"oauth:{ExternalAuthProviderNames.ToRouteSegment(identity.Provider)}",
        };

        var created = await _userStore.CreateIfAbsentAsync(candidate, ct);
        if (created.IsError || created.Result is null)
        {
            return AZOAResult<IUser>.FailureWithCode(
                "The account could not be created.", AzoaErrorCodes.DependencyUnavailable);
        }

        var binding = BuildBinding(identity, created.Result.Id, tenantId, scope);
        var savedBinding = await _loginStore.CreateAsync(binding, ct);
        if (savedBinding.IsError)
        {
            // Losing the race to another concurrent first sign-in is benign: re-read the
            // winner's binding and use it, so two tabs never yield two users.
            var winner = await _loginStore.GetBySubjectAsync(scope, identity.Provider, identity.Subject, ct);
            if (!winner.IsError && winner.Result is not null)
                return await LoadBoundUserAsync(winner.Result, ct);

            return AZOAResult<IUser>.FailureWithCode(
                "The federated identity could not be linked.", AzoaErrorCodes.DependencyUnavailable);
        }

        return AZOAResult<IUser>.Success(created.Result, "Account created.");
    }

    private UserExternalLogin BuildBinding(
        ExternalIdentity identity,
        Guid userId,
        Guid? tenantId,
        string scope) => new()
        {
            Id = DeterministicSeed(scope, identity.Provider, identity.Subject),
            TenantScope = scope,
            TenantId = tenantId is null || tenantId == Guid.Empty ? null : tenantId.Value.ToString("N"),
            Provider = identity.Provider,
            Subject = identity.Subject,
            UserId = userId.ToString("N"),
            EmailAtLink = Trim(identity.Email),
            DisplayNameAtLink = Trim(identity.DisplayName),
            LinkedAt = _clock.GetUtcNow(),
            LastLoginAt = _clock.GetUtcNow(),
        };

    /// <summary>A user can still be reached without any federated provider.</summary>
    private static bool HasNonFederatedCredential(IUser user)
        => !string.IsNullOrWhiteSpace(user.AuthWalletAddress)
            || !IsSyntheticFederatedAccount(user);

    /// <summary>
    /// True when the user was minted BY federated sign-in and never given another
    /// credential. Such an account has no password its owner knows.
    /// </summary>
    private static bool IsSyntheticFederatedAccount(IUser user)
        => user.Email.EndsWith($"@{SyntheticEmailDomain}", StringComparison.OrdinalIgnoreCase);

    /// <summary>Stable per-scope digest of the federated identity.</summary>
    private static string DeterministicSeed(
        string scope,
        ExternalAuthProvider provider,
        string subject)
    {
        // Length-prefixed so ("ab","c") and ("a","bc") cannot collide — the same
        // unambiguous-preimage rule WebhookHmacSigner documents.
        var preimage = $"azoa.oauth.v1|{scope.Length}:{scope}|{provider}|{subject.Length}:{subject}";
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(preimage)))
            .ToLowerInvariant();
    }

    /// <summary>Deterministic user id, so a retried first sign-in converges.</summary>
    private static Guid DeterministicUserId(string seed)
        => new(Convert.FromHexString(seed.AsSpan(0, 32)));

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
