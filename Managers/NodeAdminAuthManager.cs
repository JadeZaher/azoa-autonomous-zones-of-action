using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.NodeAdmin;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Managers;

/// <summary>Node-admin authentication: Ed25519 challenge login (+TOTP for named admins), self revoke and enrollment; see <c>Services/NodeAdmin/AGENTS.md</c> §auth.</summary>
public sealed class NodeAdminAuthManager(
    SurrealNodeAdminStore store,
    NodeAdminAuditLog audit,
    NodeAdminRuntime runtime,
    NodeAdminTotpProtector totpProtector,
    NodeAdminTokenIssuer issuer,
    NodeAdminPermissionResolver resolver,
    NodeAdminAnonymousFailures anonymousFailures,
    TimeProvider clock,
    ILogger<NodeAdminAuthManager> logger)
{
    private const int LockThreshold = 5;
    private const int MaxEnrollmentFailures = 5;
    private const string Anonymous = "anonymous";
    private const string EnrollmentInvalidMessage = "The enrollment is invalid, expired or already used.";
    private const int MaxCasAttempts = 32;
    private static readonly TimeSpan BaseLockDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaxLockDuration = TimeSpan.FromHours(24);
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromSeconds(60);
    private static readonly string[] RootAmr = ["ed25519", "root"];
    private static readonly string[] AdminAmr = ["ed25519", "totp"];

    /// <summary>Always issues a challenge for a well-formed request: no key-existence oracle.</summary>
    public async Task<AZOAResult<NodeAdminChallengeResponse>> CreateChallengeAsync(NodeAdminChallengeRequest request, CancellationToken ct)
    {
        if (runtime.Audience is not { } audience) return Unavailable<NodeAdminChallengeResponse>();
        if (request.Purpose is not (NodeAdminChallenge.PurposeRootLogin or NodeAdminChallenge.PurposeAdminLogin)
            || !NodeAdminCrypto.IsFingerprint(request.Fingerprint))
            return NodeAdminResults.Fail<NodeAdminChallengeResponse>(NodeAdminErrorCodes.ValidationFailed,
                "purpose must be root_login or admin_login and fingerprint must be 16 lowercase hex characters.");
        var now = clock.GetUtcNow();
        if (await store.OutstandingChallengesReachedAsync(runtime.Options.MaxOutstandingChallenges, now, ct))
        {
            logger.LogCritical("Node-admin challenge issuance refused: {Cap} live challenges outstanding (possible flood).",
                runtime.Options.MaxOutstandingChallenges);
            return NodeAdminResults.Fail<NodeAdminChallengeResponse>(NodeAdminErrorCodes.Unavailable,
                "Node-admin sign-in is temporarily unavailable. Try again shortly.");
        }
        var expiresAt = now + ChallengeLifetime;
        var id = Guid.NewGuid();
        var challenge = new NodeAdminChallenge
        {
            Id = id.ToString("N"),
            Purpose = request.Purpose,
            Nonce = NodeAdminCrypto.RandomToken(),
            Subject = request.Fingerprint,
            Audience = audience,
            ExpiresAt = expiresAt,
            ExpiresAtText = NodeAdminCrypto.FormatTimestamp(expiresAt),
            CreatedAt = now,
        };
        await store.CreateChallengeAsync(challenge, ct);
        return AZOAResult<NodeAdminChallengeResponse>.Success(
            new NodeAdminChallengeResponse(id.ToString("D"), challenge.Nonce, challenge.ExpiresAtText, audience));
    }

    /// <summary>Consumes the challenge first, then verifies; every failure is the uniform AUTH_FAILED (M4) with the reason only in the audit.</summary>
    public async Task<AZOAResult<NodeAdminSessionResponse>> CreateSessionAsync(
        NodeAdminSessionRequest request, NodeAdminRequestInfo info, CancellationToken ct)
    {
        if (runtime.Audience is null) return Unavailable<NodeAdminSessionResponse>();
        var now = clock.GetUtcNow();
        if (!Guid.TryParseExact(request.ChallengeId, "D", out var challengeId)
            || !NodeAdminCrypto.TryDecodePublicKey(request.PublicKey, out var publicKey)
            || !NodeAdminCrypto.TryDecodeSignature(request.Signature, out var signature))
            return AnonymousFailure<NodeAdminSessionResponse>("auth.session", "malformed_request", info);

        var fingerprint = NodeAdminCrypto.Fingerprint(publicKey);
        var target = "fp=" + fingerprint;
        var challenge = await store.GetChallengeAsync(challengeId.ToString("N"), ct);
        if (challenge is null || challenge.Purpose is not (NodeAdminChallenge.PurposeRootLogin or NodeAdminChallenge.PurposeAdminLogin))
            return AnonymousFailure<NodeAdminSessionResponse>("auth.session", "unknown_challenge", info);
        if (!await store.TryConsumeChallengeAsync(challenge.Id, now, ct))
            return await AuthFailedAsync(Anonymous, target, "challenge_consumed_or_expired", info, ct);
        if (!NodeAdminCrypto.FixedTimeEquals(Encoding.ASCII.GetBytes(challenge.Subject), Encoding.ASCII.GetBytes(fingerprint)))
            return await AuthFailedAsync(Anonymous, target, "fingerprint_mismatch", info, ct);
        if (!string.Equals(challenge.Audience, runtime.Audience, StringComparison.Ordinal))
            return await AuthFailedAsync(Anonymous, target, "audience_changed", info, ct);
        var message = NodeAdminCrypto.LoginMessage(challenge.Purpose, challenge.Audience, challengeId.ToString("D"),
            challenge.Nonce, challenge.ExpiresAtText);
        if (!NodeAdminCrypto.Verify(publicKey, message, signature))
            return await AuthFailedAsync(Anonymous, target, "bad_signature", info, ct);

        return challenge.Purpose == NodeAdminChallenge.PurposeRootLogin
            ? await RootSessionAsync(publicKey, fingerprint, info, now, ct)
            : await AdminSessionAsync(publicKey, fingerprint, request.Totp, info, now, ct);
    }

    /// <summary>Revokes the caller's own session.</summary>
    public async Task<AZOAResult<bool>> RevokeOwnAsync(NodeAdminCaller caller, CancellationToken ct)
    {
        await store.TryRevokeSessionAsync(caller.SessionRecordId, clock.GetUtcNow(), ct);
        return AZOAResult<bool>.Success(true, "Session revoked.");
    }

    /// <summary>Binds exactly one key and a pending TOTP secret to a pending enrollment (H3).</summary>
    public async Task<AZOAResult<NodeAdminEnrollStartView>> StartEnrollmentAsync(
        NodeAdminEnrollStartRequest request, NodeAdminRequestInfo info, CancellationToken ct)
    {
        const string action = "enroll.start";
        if (runtime.Audience is not { } audience) return Unavailable<NodeAdminEnrollStartView>();
        var now = clock.GetUtcNow();
        if (string.IsNullOrEmpty(request.EnrollmentToken) || request.EnrollmentToken.Length > 128
            || !NodeAdminCrypto.TryDecodePublicKey(request.PublicKey, out var publicKey))
            return AnonymousFailure<NodeAdminEnrollStartView>(action, "malformed_request", info);
        var fingerprint = NodeAdminCrypto.Fingerprint(publicKey);
        var target = "fp=" + fingerprint;
        var enrollment = await store.GetEnrollmentByTokenHashAsync(NodeAdminCrypto.HashSecret(request.EnrollmentToken), ct);
        if (enrollment is null)
            return AnonymousFailure<NodeAdminEnrollStartView>(action, "unknown_token", info);
        if (enrollment.ConsumedAt is not null || enrollment.ExpiresAt <= now)
            return await EnrollmentInvalidAsync<NodeAdminEnrollStartView>(action, Anonymous, "enrollment=" + EnrollmentWireId(enrollment),
                "consumed_or_expired_token", info, ct);
        var admin = await store.GetAdminAsync(enrollment.AdminId, ct);
        if (admin is null || admin.Status != NodeAdminAccount.StatusPendingEnrollment)
            return await EnrollmentInvalidAsync<NodeAdminEnrollStartView>(action, Anonymous, target, "admin_not_pending", info, ct);
        var actor = NodeAdminViews.Principal(admin);
        if (enrollment.StartedAt is not null)
            return await AlreadyStartedAsync(actor, target, info, ct);
        if (runtime.IsRootKey(publicKey))
            return await EnrollmentInvalidAsync<NodeAdminEnrollStartView>(action, actor, target, "root_key_reuse", info, ct);
        var existing = await store.GetCredentialAsync(fingerprint, ct);
        if (existing is not null && (existing.AdminId != admin.Id || existing.RevokedAt is null))
            return await EnrollmentInvalidAsync<NodeAdminEnrollStartView>(action, actor, target, "key_already_registered", info, ct);

        if (!await TryAuditAsync(Entry(actor, [], action, target, NodeAdminAuditLog.OutcomeIntent, string.Empty, info), ct))
            return AuditUnavailable<NodeAdminEnrollStartView>();
        var secret = Totp.NewSecret();
        var protectedSecret = totpProtector.Protect(admin.Id, secret);
        var label = NodeAdminViews.CleanText(request.Label, 64);
        var bind = SurrealWriter.UpdateOnly<NodeAdminEnrollment>(enrollment.Id)
            .Where(row => row.StartedAt == null && row.ConsumedAt == null && row.ExpiresAt > now)
            .Set(row => row.StartedAt, now)
            .Set(row => row.PendingPublicKey, request.PublicKey)
            .Set(row => row.PendingFingerprint, fingerprint)
            .Set(row => row.PendingLabel, label)
            .Set(row => row.TotpSecretPendingProtected, protectedSecret)
            .Build();
        if (await store.CommitAsync(null, [bind], ct) != NodeAdminCommit.Committed)
            return await AlreadyStartedAsync(actor, target, info, ct);
        await AuditBestEffortAsync(Entry(actor, [], action, target, NodeAdminAuditLog.OutcomeOk, string.Empty, info), ct);

        var secretBase32 = Totp.ToBase32(secret);
        var host = new Uri(audience).Host;
        return AZOAResult<NodeAdminEnrollStartView>.Success(new NodeAdminEnrollStartView(
            Guid.ParseExact(enrollment.Id, "N").ToString("D"),
            Totp.ProvisioningUri(host, admin.Username, secretBase32),
            secretBase32,
            audience,
            fingerprint));
    }

    /// <summary>Proves possession of the bound key and the authenticator; the admin moves to pending_activation.</summary>
    public async Task<AZOAResult<NodeAdminEnrollCompleteView>> CompleteEnrollmentAsync(
        NodeAdminEnrollCompleteRequest request, NodeAdminRequestInfo info, CancellationToken ct)
    {
        const string action = "enroll.complete";
        if (runtime.Audience is not { } audience) return Unavailable<NodeAdminEnrollCompleteView>();
        var now = clock.GetUtcNow();
        if (!Guid.TryParseExact(request.EnrollmentId, "D", out var enrollmentId)
            || !NodeAdminCrypto.TryDecodeSignature(request.Signature, out var signature))
            return AnonymousFailure<NodeAdminEnrollCompleteView>(action, "malformed_request", info);
        var enrollment = await store.GetEnrollmentAsync(enrollmentId.ToString("N"), ct);
        if (enrollment is null)
            return AnonymousFailure<NodeAdminEnrollCompleteView>(action, "unknown_enrollment", info);
        if (enrollment.ConsumedAt is not null || enrollment.ExpiresAt <= now || enrollment.StartedAt is null
            || enrollment.FailedAttempts >= MaxEnrollmentFailures
            || !NodeAdminCrypto.TryDecodePublicKey(enrollment.PendingPublicKey, out var publicKey)
            || enrollment.PendingFingerprint is null || enrollment.TotpSecretPendingProtected is null)
            return await EnrollmentInvalidAsync<NodeAdminEnrollCompleteView>(action, Anonymous, "enrollment=" + enrollmentId.ToString("D"), "consumed_expired_or_unstarted", info, ct);
        var target = "fp=" + enrollment.PendingFingerprint;
        var admin = await store.GetAdminAsync(enrollment.AdminId, ct);
        if (admin is null || admin.Status != NodeAdminAccount.StatusPendingEnrollment)
            return await EnrollmentInvalidAsync<NodeAdminEnrollCompleteView>(action, Anonymous, target, "admin_not_pending", info, ct);
        var actor = NodeAdminViews.Principal(admin);

        byte[] secret;
        try
        {
            secret = totpProtector.Unprotect(admin.Id, enrollment.TotpSecretPendingProtected);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            await SecretUnreadableAsync(exception, admin, actor, target, info, ct);
            return NodeAdminResults.Fail<NodeAdminEnrollCompleteView>(NodeAdminErrorCodes.EnrollmentInvalid, EnrollmentInvalidMessage);
        }
        var signed = NodeAdminCrypto.Verify(publicKey, NodeAdminCrypto.EnrollMessage(audience, enrollmentId.ToString("D")), signature);
        var totpValid = Totp.TryVerify(secret, request.Totp, now, 0, out var step);
        if (!signed || !totpValid)
        {
            await RecordEnrollmentFailureAsync(enrollment.Id, ct);
            return await EnrollmentInvalidAsync<NodeAdminEnrollCompleteView>(action, actor, target, signed ? "bad_totp" : "bad_signature", info, ct);
        }

        var existing = await store.GetCredentialAsync(enrollment.PendingFingerprint, ct);
        if (existing is not null && (existing.AdminId != admin.Id || existing.RevokedAt is null))
            return await EnrollmentInvalidAsync<NodeAdminEnrollCompleteView>(action, actor, target, "key_already_registered", info, ct);
        if (!await TryAuditAsync(Entry(actor, [], action, target, NodeAdminAuditLog.OutcomeIntent, string.Empty, info), ct))
            return AuditUnavailable<NodeAdminEnrollCompleteView>();

        var credential = existing is null
            ? SurrealWriter.Create(new NodeAdminCredential
            {
                Id = enrollment.PendingFingerprint,
                AdminId = admin.Id,
                PublicKey = enrollment.PendingPublicKey!,
                Fingerprint = enrollment.PendingFingerprint,
                Label = enrollment.PendingLabel ?? string.Empty,
                CreatedAt = now,
            })
            : SurrealWriter.UpdateOnly<NodeAdminCredential>(existing.Id)
                .Where(row => row.AdminId == admin.Id && row.RevokedAt != null)
                .Unset(row => row.RevokedAt)
                .Set(row => row.Label, enrollment.PendingLabel ?? string.Empty)
                .Build();
        var commit = await store.CommitAsync(null,
        [
            SurrealWriter.UpdateOnly<NodeAdminEnrollment>(enrollment.Id)
                .Where(row => row.ConsumedAt == null && row.FailedAttempts == enrollment.FailedAttempts && row.ExpiresAt > now)
                .Set(row => row.ConsumedAt, now).Build(),
            credential,
            SurrealWriter.UpdateOnly<NodeAdminAccount>(admin.Id)
                .Where(row => row.AdminRevision == admin.AdminRevision && row.Status == NodeAdminAccount.StatusPendingEnrollment)
                .Set(row => row.Status, NodeAdminAccount.StatusPendingActivation)
                .Set(row => row.TotpSecretProtected, enrollment.TotpSecretPendingProtected)
                .Set(row => row.TotpLastStep, step)
                .Set(row => row.FailedAttempts, 0L)
                .Set(row => row.AdminRevision, admin.AdminRevision + 1)
                .Set(row => row.UpdatedAt, now)
                .Build(),
        ], ct);
        if (commit != NodeAdminCommit.Committed)
            return await EnrollmentInvalidAsync<NodeAdminEnrollCompleteView>(action, actor, target, "concurrent_change", info, ct);
        await AuditBestEffortAsync(Entry(actor, [], action, target, NodeAdminAuditLog.OutcomeOk, string.Empty, info), ct);
        return AZOAResult<NodeAdminEnrollCompleteView>.Success(new NodeAdminEnrollCompleteView(
            NodeAdminViews.WireId(admin), NodeAdminViews.WireStatus(NodeAdminAccount.StatusPendingActivation), enrollment.PendingFingerprint),
            "Enrollment complete; an administrator must activate this account by confirming the key fingerprint.");
    }

    private async Task<AZOAResult<NodeAdminSessionResponse>> RootSessionAsync(
        byte[] publicKey, string fingerprint, NodeAdminRequestInfo info, DateTimeOffset now, CancellationToken ct)
    {
        var target = "fp=" + fingerprint;
        if (!runtime.RootLoginEnabled || !runtime.IsRootKey(publicKey))
            return await AuthFailedAsync(Anonymous, target, runtime.RootLoginEnabled ? "not_a_root_key" : "root_login_disabled", info, ct);
        var state = await store.GetRootStateAsync(ct);
        if (state is null || state.RootKeyRevision != runtime.RootKeyRevision)
            return await AuthFailedAsync(Anonymous, target, "root_state_mismatch", info, ct);
        return await IssueAsync(NodeAdminClaims.RootPrincipal(fingerprint), RootAmr,
            new NodeAdminTokenRevisions(NodeAdminClaims.RootKeyRevision, state.RootKeyRevision,
                NodeAdminClaims.RootSessionRevision, state.RootSessionRevision),
            fingerprint, NodeAdminPermissions.Root, null, null, info, now, ct);
    }

    private async Task<AZOAResult<NodeAdminSessionResponse>> AdminSessionAsync(
        byte[] publicKey, string fingerprint, string? code, NodeAdminRequestInfo info, DateTimeOffset now, CancellationToken ct)
    {
        var target = "fp=" + fingerprint;
        var credential = await store.GetCredentialAsync(fingerprint, ct);
        if (credential is null || credential.RevokedAt is not null
            || !NodeAdminCrypto.TryDecodePublicKey(credential.PublicKey, out var storedKey)
            || !NodeAdminCrypto.FixedTimeEquals(storedKey, publicKey))
            return await AuthFailedAsync(Anonymous, target, "unknown_credential", info, ct);
        var admin = await store.GetAdminAsync(credential.AdminId, ct);
        if (admin is null)
            return await AuthFailedAsync(Anonymous, target, "unknown_credential", info, ct);
        var actor = NodeAdminViews.Principal(admin);
        if (admin.Status != NodeAdminAccount.StatusActive)
            return await AuthFailedAsync(actor, target, "admin_not_active", info, ct);
        if (admin.LockedUntil is { } lockedUntil && lockedUntil > now)
        {
            // Only reachable after a valid signature, so the lock is not an oracle for key holders' identities.
            await AuditBestEffortAsync(Entry(actor, [], "auth.session", target, NodeAdminAuditLog.OutcomeFailed, Reason("locked"), info), ct);
            return new AZOAResult<NodeAdminSessionResponse>
            {
                IsError = true,
                Code = NodeAdminErrorCodes.AdminLocked,
                Message = "This administrator is temporarily locked after repeated failed sign-ins.",
                RetryAfterSeconds = Math.Max(1, (int)Math.Ceiling((lockedUntil - now).TotalSeconds)),
            };
        }
        if (admin.TotpSecretProtected is null)
            return await AuthFailedAsync(actor, target, "mfa_not_enrolled", info, ct);
        byte[] secret;
        try
        {
            secret = totpProtector.Unprotect(admin.Id, admin.TotpSecretProtected);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            await SecretUnreadableAsync(exception, admin, actor, target, info, ct);
            return NodeAdminResults.Fail<NodeAdminSessionResponse>(NodeAdminErrorCodes.AuthFailed, "Authentication failed.");
        }
        if (!Totp.TryVerify(secret, code, now, admin.TotpLastStep, out var step))
        {
            await RecordSecondFactorFailureAsync(admin.Id, actor, target, info, now, ct);
            return await AuthFailedAsync(actor, target, "bad_or_replayed_totp", info, ct);
        }
        var advanced = await store.CommitAsync(null,
        [
            SurrealWriter.UpdateOnly<NodeAdminAccount>(admin.Id)
                // lock_count in the predicate: a lock set after our read bumps it, so this write never clears a fresh lock (H-1).
                .Where(row => row.AdminRevision == admin.AdminRevision && row.TotpLastStep == admin.TotpLastStep
                    && row.LockCount == admin.LockCount && row.Status == NodeAdminAccount.StatusActive)
                .Set(row => row.TotpLastStep, step)
                .Set(row => row.FailedAttempts, 0L)
                .Set(row => row.LockCount, 0L)
                .Unset(row => row.LockedUntil)
                .Build(),
        ], ct);
        if (advanced != NodeAdminCommit.Committed)
            return await AuthFailedAsync(actor, target, "totp_step_race", info, ct);
        var roles = await resolver.SnapshotAsync(store, ct);
        return await IssueAsync(actor, AdminAmr,
            new NodeAdminTokenRevisions(NodeAdminClaims.AdminRevision, admin.AdminRevision,
                NodeAdminClaims.SessionRevision, admin.SessionRevision),
            fingerprint, roles.EffectivePermissions(admin), admin, roles, info, now, ct);
    }

    /// <summary>
    /// Counts one second-factor failure by CAS retry until it lands (H-1), locking at the threshold with exponential backoff
    /// (L-4). A row that is already locked stops counting.
    /// </summary>
    private async Task RecordSecondFactorFailureAsync(string adminRecordId, string actor, string target, NodeAdminRequestInfo info,
        DateTimeOffset now, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxCasAttempts; attempt++)
        {
            var current = await store.GetAdminAsync(adminRecordId, ct);
            if (current is null || current.Status != NodeAdminAccount.StatusActive || current.LockedUntil > now) return;
            var attempts = current.FailedAttempts + 1;
            var update = SurrealWriter.UpdateOnly<NodeAdminAccount>(current.Id)
                .Where(row => row.AdminRevision == current.AdminRevision && row.TotpLastStep == current.TotpLastStep
                    && row.FailedAttempts == current.FailedAttempts && row.LockCount == current.LockCount);
            var lockCount = current.LockCount + 1;
            var lockFor = LockDuration(lockCount);
            var locks = attempts >= LockThreshold;
            var mutation = locks
                ? update.Set(row => row.FailedAttempts, 0L).Set(row => row.LockCount, lockCount)
                    .Set(row => row.LockedUntil, (DateTimeOffset?)(now + lockFor)).Build()
                : update.Set(row => row.FailedAttempts, attempts).Build();
            if (await store.CommitAsync(null, [mutation], ct) != NodeAdminCommit.Committed) continue;
            if (locks)
                await AuditBestEffortAsync(Entry(actor, [], "auth.lock", target, NodeAdminAuditLog.OutcomeOk,
                    JsonSerializer.Serialize(new { reason = "second_factor_failures", lockCount, lockedForSeconds = (long)lockFor.TotalSeconds }), info), ct);
            return;
        }
        logger.LogError("Second-factor failure for node admin {AdminId} could not be counted after {Attempts} attempts.", adminRecordId, MaxCasAttempts);
    }

    /// <summary>15 minutes doubled per consecutive lock, capped at 24 hours (L-4).</summary>
    public static TimeSpan LockDuration(long lockCount)
    {
        var doublings = (int)Math.Clamp(lockCount - 1, 0, 10);
        var duration = TimeSpan.FromTicks(BaseLockDuration.Ticks << doublings);
        return duration > MaxLockDuration ? MaxLockDuration : duration;
    }

    /// <summary>Counts one failed enrollment completion by CAS retry until it lands (H-1).</summary>
    private async Task RecordEnrollmentFailureAsync(string enrollmentRecordId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxCasAttempts; attempt++)
        {
            var current = await store.GetEnrollmentAsync(enrollmentRecordId, ct);
            if (current is null || current.ConsumedAt is not null || current.FailedAttempts >= MaxEnrollmentFailures) return;
            var commit = await store.CommitAsync(null, [SurrealWriter.UpdateOnly<NodeAdminEnrollment>(current.Id)
                .Where(row => row.FailedAttempts == current.FailedAttempts && row.ConsumedAt == null)
                .Set(row => row.FailedAttempts, current.FailedAttempts + 1).Build()], ct);
            if (commit == NodeAdminCommit.Committed) return;
        }
        logger.LogError("Enrollment failure for {EnrollmentId} could not be counted after {Attempts} attempts.", enrollmentRecordId, MaxCasAttempts);
    }

    private async Task SecretUnreadableAsync(Exception exception, NodeAdminAccount admin, string actor, string target,
        NodeAdminRequestInfo info, CancellationToken ct)
    {
        logger.LogError(exception, "TOTP secret for node admin {AdminId} could not be decrypted or decoded", admin.Id);
        await AuditBestEffortAsync(Entry(actor, [], "totp.secret_unreadable", target, NodeAdminAuditLog.OutcomeFailed,
            Reason(exception.GetType().Name), info), ct);
    }

    private async Task<AZOAResult<NodeAdminSessionResponse>> IssueAsync(
        string principal, IReadOnlyList<string> amr, NodeAdminTokenRevisions revisions, string fingerprint,
        IReadOnlySet<string> permissions, NodeAdminAccount? admin, NodeAdminRoleSnapshot? roles, NodeAdminRequestInfo info,
        DateTimeOffset now, CancellationToken ct)
    {
        if (!await TryAuditAsync(Entry(principal, amr, "auth.session", "fp=" + fingerprint, NodeAdminAuditLog.OutcomeOk, string.Empty, info), ct))
            return AuditUnavailable<NodeAdminSessionResponse>();
        var (sid, recordId, handle) = NodeAdminCrypto.NewSession();
        var expiresAt = now + runtime.Options.SessionLifetime;
        await store.CreateSessionAsync(new NodeAdminSession
        {
            Id = recordId,
            Principal = principal,
            AuthTime = now,
            Amr = amr,
            ExpiresAt = expiresAt,
            Ip = info.Ip,
            UserAgent = info.UserAgent,
            CredentialFingerprint = fingerprint,
            Handle = handle,
        }, ct);
        var token = issuer.Issue(principal, sid, now, amr, revisions, expiresAt);
        NodeAdminView? view = null;
        if (admin is not null && roles is not null)
            view = NodeAdminViews.Admin(admin, roles, await store.ListCredentialsAsync(admin.Id, ct));
        return AZOAResult<NodeAdminSessionResponse>.Success(new NodeAdminSessionResponse(
            token, NodeAdminCrypto.FormatTimestamp(expiresAt), handle, principal, NodeAdminPermissions.Normalize(permissions), view),
            "Node admin authenticated.");
    }

    private async Task<AZOAResult<NodeAdminSessionResponse>> AuthFailedAsync(
        string actor, string target, string reason, NodeAdminRequestInfo info, CancellationToken ct)
    {
        await AuditBestEffortAsync(Entry(actor, [], "auth.session", target, NodeAdminAuditLog.OutcomeFailed, Reason(reason), info), ct);
        return NodeAdminResults.Fail<NodeAdminSessionResponse>(NodeAdminErrorCodes.AuthFailed, "Authentication failed.");
    }

    private async Task<AZOAResult<T>> EnrollmentInvalidAsync<T>(
        string action, string actor, string target, string reason, NodeAdminRequestInfo info, CancellationToken ct)
    {
        await AuditBestEffortAsync(Entry(actor, [], action, target, NodeAdminAuditLog.OutcomeFailed, Reason(reason), info), ct);
        return NodeAdminResults.Fail<T>(NodeAdminErrorCodes.EnrollmentInvalid, EnrollmentInvalidMessage);
    }

    /// <summary>A failure tied to no real challenge, enrollment or admin: counted and logged, never chained (M-3).</summary>
    private AZOAResult<T> AnonymousFailure<T>(string action, string reason, NodeAdminRequestInfo info)
    {
        anonymousFailures.Record(action, reason, info);
        return action == "auth.session"
            ? NodeAdminResults.Fail<T>(NodeAdminErrorCodes.AuthFailed, "Authentication failed.")
            : NodeAdminResults.Fail<T>(NodeAdminErrorCodes.EnrollmentInvalid, EnrollmentInvalidMessage);
    }

    private static string EnrollmentWireId(NodeAdminEnrollment enrollment) => Guid.ParseExact(enrollment.Id, "N").ToString("D");

    private async Task<AZOAResult<NodeAdminEnrollStartView>> AlreadyStartedAsync(
        string actor, string target, NodeAdminRequestInfo info, CancellationToken ct)
    {
        await AuditBestEffortAsync(Entry(actor, [], "enroll.start", target, NodeAdminAuditLog.OutcomeDenied,
            Reason("already_started"), info), ct);
        return NodeAdminResults.Fail<NodeAdminEnrollStartView>(NodeAdminErrorCodes.EnrollmentAlreadyStarted,
            "This enrollment already bound a key. Ask an administrator to reset MFA for a new enrollment token.");
    }

    private async Task<bool> TryAuditAsync(NodeAdminAuditEntry entry, CancellationToken ct)
    {
        try
        {
            await audit.AppendAsync(entry, ct);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Node-admin audit write failed for {Action}", entry.Action);
            return false;
        }
    }

    private async Task AuditBestEffortAsync(NodeAdminAuditEntry entry, CancellationToken ct)
        => await TryAuditAsync(entry, ct);

    private static NodeAdminAuditEntry Entry(string actor, IReadOnlyList<string> amr, string action, string target, string outcome,
        string detail, NodeAdminRequestInfo info)
        => new(actor, amr, action, target, outcome, detail, info.Ip, info.RequestId);

    private static string Reason(string reason) => JsonSerializer.Serialize(new { reason });

    private static AZOAResult<T> Unavailable<T>()
        => NodeAdminResults.Fail<T>(NodeAdminErrorCodes.Unavailable, "Node-admin sign-in is not configured on this node.");

    private static AZOAResult<T> AuditUnavailable<T>()
        => NodeAdminResults.Fail<T>(NodeAdminErrorCodes.AuditUnavailable, "The audit trail is unavailable, so this action was not performed.");
}
