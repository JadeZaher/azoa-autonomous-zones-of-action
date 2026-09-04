// SPDX-License-Identifier: UNLICENSED

using System.Security.Cryptography;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Auth;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Kyc;

namespace AZOA.WebAPI.Services.Auth;

/// <summary>
/// Resolves a verified federated identity to an AZOA avatar (Phase 1B, D17/D18).
/// </summary>
/// <remarks>
/// <para><b>Rule 1 — no email auto-linking, enforced structurally.</b> There is not one
/// email lookup in this file, and <see cref="IAvatarExternalLoginStore"/> exposes no
/// method that could perform one. An account is resolved ONLY by
/// <c>(tenant_scope, provider, subject)</c>. When no binding exists, a BRAND NEW avatar
/// is created with a synthetic scope-unique address — the provider's email is recorded
/// as an audit snapshot and never consulted to find an existing account. This is what
/// closes account pre-hijacking: an attacker who registers a password account on the
/// victim's email gains nothing, because the victim's later Google sign-in never looks
/// at email and so can never be merged into the attacker's account. Attaching a
/// provider to an existing avatar happens ONLY through <see cref="LinkToAvatarAsync"/>,
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

    private readonly IAvatarStore _avatarStore;
    private readonly IAvatarExternalLoginStore _loginStore;
    private readonly ILogger<ExternalLoginService> _logger;
    private readonly TimeProvider _clock;

    public ExternalLoginService(
        IAvatarStore avatarStore,
        IAvatarExternalLoginStore loginStore,
        ILogger<ExternalLoginService> logger,
        TimeProvider? clock = null)
    {
        _avatarStore = avatarStore ?? throw new ArgumentNullException(nameof(avatarStore));
        _loginStore = loginStore ?? throw new ArgumentNullException(nameof(loginStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<AZOAResult<IAvatar>> ResolveOrCreateAsync(
        ExternalIdentity identity,
        Guid? tenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.Subject))
        {
            // A provider that asserted no stable subject cannot be trusted to identify
            // anyone. Fail closed rather than inventing a key from the email.
            return AZOAResult<IAvatar>.FailureWithCode(
                "The identity provider did not return a stable account identifier.",
                AzoaErrorCodes.InvalidRequest);
        }

        var scope = KycPersonKeyDeriver.ScopeValue(tenantId);

        var existing = await _loginStore.GetBySubjectAsync(scope, identity.Provider, identity.Subject, ct);
        if (existing.IsError)
            return AZOAResult<IAvatar>.FailureWithCode(existing.Message, AzoaErrorCodes.DependencyUnavailable);

        if (existing.Result is not null)
        {
            var boundResult = await LoadBoundAvatarAsync(existing.Result, ct);
            if (boundResult.IsError || boundResult.Result is null)
                return boundResult;

            await _loginStore.TouchLastLoginAsync(existing.Result.Id, _clock.GetUtcNow(), ct);
            return AZOAResult<IAvatar>.Success(boundResult.Result, "Signed in.");
        }

        return await CreateAvatarForIdentityAsync(identity, tenantId, scope, ct);
    }

    /// <inheritdoc />
    public async Task<AZOAResult<AvatarExternalLogin>> LinkToAvatarAsync(
        ExternalIdentity identity,
        Guid avatarId,
        Guid? tenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (avatarId == Guid.Empty)
            return AZOAResult<AvatarExternalLogin>.FailureWithCode(
                "An authenticated avatar is required to link a provider.", AzoaErrorCodes.Forbidden);
        if (string.IsNullOrWhiteSpace(identity.Subject))
            return AZOAResult<AvatarExternalLogin>.FailureWithCode(
                "The identity provider did not return a stable account identifier.",
                AzoaErrorCodes.InvalidRequest);

        var scope = KycPersonKeyDeriver.ScopeValue(tenantId);

        // The avatar id is the AUTHENTICATED caller's, threaded from the JWT through a
        // single-use LINK code — never read from the request body. Same rule as the
        // STARODK upsert IDOR precedent: a caller-supplied identity never widens the
        // authenticated one.
        var owner = await _avatarStore.GetByIdAsync(avatarId, ct);
        if (owner.IsError || owner.Result is null)
            return AZOAResult<AvatarExternalLogin>.FailureWithCode(
                "Avatar not found.", AzoaErrorCodes.NotFound);

        var existing = await _loginStore.GetBySubjectAsync(scope, identity.Provider, identity.Subject, ct);
        if (existing.IsError)
            return AZOAResult<AvatarExternalLogin>.FailureWithCode(
                existing.Message, AzoaErrorCodes.DependencyUnavailable);

        if (existing.Result is not null)
        {
            // Already linked. Idempotent for the same avatar; a conflict for any other,
            // because silently re-pointing a binding at a second avatar is the takeover
            // this table exists to prevent.
            return string.Equals(existing.Result.AvatarId, avatarId.ToString("N"), StringComparison.OrdinalIgnoreCase)
                ? AZOAResult<AvatarExternalLogin>.Success(existing.Result, "Already linked.")
                : AZOAResult<AvatarExternalLogin>.FailureWithCode(
                    "That provider account is already linked to a different avatar.",
                    AzoaErrorCodes.Conflict);
        }

        var binding = BuildBinding(identity, avatarId, tenantId, scope);
        return await _loginStore.CreateAsync(binding, ct);
    }

    /// <inheritdoc />
    public Task<AZOAResult<IReadOnlyList<AvatarExternalLogin>>> ListLinksAsync(
        Guid avatarId,
        CancellationToken ct = default)
        => _loginStore.GetByAvatarAsync(avatarId.ToString("N"), ct);

    /// <inheritdoc />
    public async Task<AZOAResult<IAvatar>> ResolveBoundAvatarAsync(
        Guid avatarId,
        CancellationToken ct = default)
    {
        if (avatarId == Guid.Empty)
            return AZOAResult<IAvatar>.FailureWithCode("Avatar not found.", AzoaErrorCodes.NotFound);

        var avatar = await _avatarStore.GetByIdAsync(avatarId, ct);
        if (avatar.IsError || avatar.Result is null)
            return AZOAResult<IAvatar>.FailureWithCode("Avatar not found.", AzoaErrorCodes.NotFound);

        return avatar.Result.IsActive
            ? AZOAResult<IAvatar>.Success(avatar.Result, "Success")
            : AZOAResult<IAvatar>.FailureWithCode("This account is deactivated.", AzoaErrorCodes.Forbidden);
    }

    /// <inheritdoc />
    public async Task<AZOAResult<bool>> UnlinkAsync(
        ExternalAuthProvider provider,
        Guid avatarId,
        Guid? tenantId,
        CancellationToken ct = default)
    {
        var scope = KycPersonKeyDeriver.ScopeValue(tenantId);

        var avatar = await _avatarStore.GetByIdAsync(avatarId, ct);
        if (avatar.IsError || avatar.Result is null)
            return AZOAResult<bool>.FailureWithCode("Avatar not found.", AzoaErrorCodes.NotFound);

        var links = await _loginStore.GetByAvatarAsync(avatarId.ToString("N"), ct);
        if (links.IsError)
            return AZOAResult<bool>.FailureWithCode(links.Message, AzoaErrorCodes.DependencyUnavailable);

        var remaining = (links.Result ?? [])
            .Where(link => link.Provider != provider)
            .ToList();

        // A revoked or deleted provider account must not orphan the avatar. Deleting the
        // binding never cascades to the avatar row — but if this were the LAST way in,
        // the account would survive with nobody able to reach it. Refuse instead, and
        // name the recovery step.
        if (remaining.Count == 0 && !HasNonFederatedCredential(avatar.Result))
        {
            return AZOAResult<bool>.FailureWithCode(
                "This is the only way to sign in to this account. Set a password or link "
                + "another provider first, then unlink this one.",
                AzoaErrorCodes.OperationNotAllowed);
        }

        return await _loginStore.DeleteAsync(scope, provider, avatarId.ToString("N"), ct);
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private async Task<AZOAResult<IAvatar>> LoadBoundAvatarAsync(
        AvatarExternalLogin binding,
        CancellationToken ct)
    {
        if (!Guid.TryParse(binding.AvatarId, out var boundAvatarId))
        {
            _logger.LogError(
                "External login binding {BindingId} carries an unparseable avatar id.", binding.Id);
            return AZOAResult<IAvatar>.FailureWithCode(
                "The linked account could not be loaded.", AzoaErrorCodes.DependencyUnavailable);
        }

        var avatar = await _avatarStore.GetByIdAsync(boundAvatarId, ct);
        if (avatar.IsError || avatar.Result is null)
        {
            // The binding outlived its avatar. Fail closed: never fall back to creating
            // a replacement, which would hand the provider account a fresh identity
            // carrying none of the original's history.
            return AZOAResult<IAvatar>.FailureWithCode(
                "The linked account is no longer available.", AzoaErrorCodes.NotFound);
        }

        if (!avatar.Result.IsActive)
        {
            return AZOAResult<IAvatar>.FailureWithCode(
                "This account is deactivated.", AzoaErrorCodes.Forbidden);
        }

        return AZOAResult<IAvatar>.Success(avatar.Result, "Success");
    }

    private async Task<AZOAResult<IAvatar>> CreateAvatarForIdentityAsync(
        ExternalIdentity identity,
        Guid? tenantId,
        string scope,
        CancellationToken ct)
    {
        var seed = DeterministicSeed(scope, identity.Provider, identity.Subject);
        var avatarId = DeterministicAvatarId(seed);
        // 96 bits of the digest is ample to keep synthetic handles distinct without
        // stamping a 64-character hex string into a username.
        var handle = seed[..24];

        // Fully qualified: AZOA.WebAPI.Services.Avatar is a NAMESPACE, so a bare
        // `Avatar` binds to it rather than to the model from inside Services.Auth.
        var candidate = new AZOA.WebAPI.Models.Avatar
        {
            Id = avatarId,
            // Synthetic, scope-unique, and NOT derived from the provider's email.
            // avatar.email carries a global UNIQUE index, so reusing the provider
            // address would (a) collide the moment the same account signs in under a
            // second tenant — the cross-tenant case D23 requires to yield two distinct
            // avatars — and (b) reintroduce email as a de-facto join key. The real
            // address is kept on the binding as an audit snapshot instead.
            Username = $"oauth-{handle}",
            Email = $"oauth-{handle}@{SyntheticEmailDomain}",
            // Non-empty (avatar.password_hash asserts != "") but unguessable, so the
            // password path can never authenticate this avatar and BCrypt.Verify
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

        var created = await _avatarStore.CreateIfAbsentAsync(candidate, ct);
        if (created.IsError || created.Result is null)
        {
            return AZOAResult<IAvatar>.FailureWithCode(
                "The account could not be created.", AzoaErrorCodes.DependencyUnavailable);
        }

        var binding = BuildBinding(identity, created.Result.Id, tenantId, scope);
        var savedBinding = await _loginStore.CreateAsync(binding, ct);
        if (savedBinding.IsError)
        {
            // Losing the race to another concurrent first sign-in is benign: re-read the
            // winner's binding and use it, so two tabs never yield two avatars.
            var winner = await _loginStore.GetBySubjectAsync(scope, identity.Provider, identity.Subject, ct);
            if (!winner.IsError && winner.Result is not null)
                return await LoadBoundAvatarAsync(winner.Result, ct);

            return AZOAResult<IAvatar>.FailureWithCode(
                "The federated identity could not be linked.", AzoaErrorCodes.DependencyUnavailable);
        }

        return AZOAResult<IAvatar>.Success(created.Result, "Account created.");
    }

    private AvatarExternalLogin BuildBinding(
        ExternalIdentity identity,
        Guid avatarId,
        Guid? tenantId,
        string scope) => new()
        {
            Id = DeterministicSeed(scope, identity.Provider, identity.Subject),
            TenantScope = scope,
            TenantId = tenantId is null || tenantId == Guid.Empty ? null : tenantId.Value.ToString("N"),
            Provider = identity.Provider,
            Subject = identity.Subject,
            AvatarId = avatarId.ToString("N"),
            EmailAtLink = Trim(identity.Email),
            DisplayNameAtLink = Trim(identity.DisplayName),
            LinkedAt = _clock.GetUtcNow(),
            LastLoginAt = _clock.GetUtcNow(),
        };

    /// <summary>An avatar can still be reached without any federated provider.</summary>
    private static bool HasNonFederatedCredential(IAvatar avatar)
        => !string.IsNullOrWhiteSpace(avatar.AuthWalletAddress)
            || !IsSyntheticFederatedAccount(avatar);

    /// <summary>
    /// True when the avatar was minted BY federated sign-in and never given another
    /// credential. Such an account has no password its owner knows.
    /// </summary>
    private static bool IsSyntheticFederatedAccount(IAvatar avatar)
        => avatar.Email.EndsWith($"@{SyntheticEmailDomain}", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Deterministic avatar id, so a retried first sign-in converges.</summary>
    private static Guid DeterministicAvatarId(string seed)
        => new(Convert.FromHexString(seed.AsSpan(0, 32)));

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
