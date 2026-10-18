using System.Text.RegularExpressions;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.NodeAdmin;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Managers;

/// <summary>Node-admin directory management enforcing dominance, no escalation, self-protection and last owner; see <c>Services/NodeAdmin/AGENTS.md</c> §invariants.</summary>
public sealed partial class NodeAdminManager(
    SurrealNodeAdminStore store,
    NodeAdminAuditLog audit,
    NodeAdminRuntime runtime,
    NodeAdminPermissionResolver resolver,
    TimeProvider clock)
{
    private const int MaxAttempts = 5;
    private const int MaxRolesPerAdmin = 32;

    public async Task<AZOAResult<NodeAdminMeView>> MeAsync(NodeAdminCaller caller, CancellationToken ct)
    {
        var session = await store.GetSessionAsync(caller.SessionRecordId, ct);
        if (session is null) return Fail<NodeAdminMeView>(NodeAdminErrorCodes.NotFound, "Session not found.");
        NodeAdminView? admin = null;
        if (caller.Admin is { } row)
            admin = NodeAdminViews.Admin(row, await resolver.SnapshotAsync(store, ct), await store.ListCredentialsAsync(row.Id, ct));
        return AZOAResult<NodeAdminMeView>.Success(new NodeAdminMeView(
            caller.Principal, admin, caller.RootFingerprint is { } fingerprint ? new NodeAdminRootView(fingerprint) : null,
            NodeAdminPermissions.Normalize(caller.Permissions), NodeAdminViews.Session(session, current: true)));
    }

    public async Task<AZOAResult<IReadOnlyList<NodeAdminView>>> ListAdminsAsync(CancellationToken ct)
    {
        var roles = await resolver.SnapshotAsync(store, ct);
        var credentials = (await store.ListAllCredentialsAsync(ct)).ToLookup(credential => credential.AdminId, StringComparer.Ordinal);
        IReadOnlyList<NodeAdminView> views = (await store.ListAdminsAsync(ct))
            .Select(admin => NodeAdminViews.Admin(admin, roles, credentials[admin.Id])).ToArray();
        return AZOAResult<IReadOnlyList<NodeAdminView>>.Success(views);
    }

    public async Task<AZOAResult<NodeAdminView>> GetAdminAsync(Guid id, CancellationToken ct)
    {
        var admin = await store.GetAdminAsync(id.ToString("N"), ct);
        return admin is null
            ? Fail<NodeAdminView>(NodeAdminErrorCodes.NotFound, "Node admin not found.")
            : AZOAResult<NodeAdminView>.Success(await ViewAsync(admin, ct));
    }

    /// <summary>Creates a pending admin; the one-time enrollment token goes to its creator only (C1).</summary>
    public async Task<AZOAResult<NodeAdminCreatedView>> CreateAdminAsync(NodeAdminCaller caller, NodeAdminCreateRequest request, CancellationToken ct)
    {
        var username = (request.Username ?? string.Empty).Trim();
        var displayName = Clean(request.DisplayName);
        var roleIds = (request.RoleIds ?? []).Distinct(StringComparer.Ordinal).ToArray();
        if (!UsernamePattern().IsMatch(username) || displayName.Length is < 1 or > 128 || roleIds.Length > MaxRolesPerAdmin)
            return Fail<NodeAdminCreatedView>(NodeAdminErrorCodes.ValidationFailed,
                "username must be 3-64 lowercase letters, digits, '.', '_' or '-'; displayName 1-128 characters.");
        var roles = await resolver.SnapshotAsync(store, ct);
        if (roleIds.Any(roleId => !roles.Roles.ContainsKey(roleId)))
            return Fail<NodeAdminCreatedView>(NodeAdminErrorCodes.ValidationFailed, "One or more role ids do not exist.");
        if (!caller.IsRoot && NodeAdminInvariants.Escalates(caller.Permissions, roles.PermissionsOf(roleIds)))
            return Fail<NodeAdminCreatedView>(NodeAdminErrorCodes.EscalationRefused, "You cannot grant permissions you do not hold.");
        if (await store.GetAdminByUsernameAsync(username, ct) is not null)
            return Fail<NodeAdminCreatedView>(NodeAdminErrorCodes.UsernameTaken, "That username is already taken.");

        var now = clock.GetUtcNow();
        var admin = new NodeAdminAccount
        {
            Id = Guid.NewGuid().ToString("N"),
            Username = username,
            DisplayName = displayName,
            Status = NodeAdminAccount.StatusPendingEnrollment,
            RoleIds = roleIds,
            AdminRevision = 1,
            CreatedBy = caller.Principal,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var (token, enrollment) = NewEnrollment(admin.Id, caller.Principal, now);
        var commit = await store.CommitAsync(null, [SurrealWriter.Create(admin), SurrealWriter.Create(enrollment)], ct);
        if (commit == NodeAdminCommit.Duplicate)
            return Fail<NodeAdminCreatedView>(NodeAdminErrorCodes.UsernameTaken, "That username is already taken.");
        if (commit != NodeAdminCommit.Committed)
            return Fail<NodeAdminCreatedView>(NodeAdminErrorCodes.Conflict, "The admin could not be created; retry.");
        return AZOAResult<NodeAdminCreatedView>.Success(
            new NodeAdminCreatedView(NodeAdminViews.Admin(admin, roles, []), token, enrollment.ExpiresAt),
            "Admin created. Deliver the enrollment token to the new admin out of band; it is shown once.");
    }

    public Task<AZOAResult<NodeAdminView>> SuspendAsync(NodeAdminCaller caller, Guid id, CancellationToken ct)
        => TransitionAsync(caller, id, status => status == NodeAdminAccount.StatusActive ? NodeAdminAccount.StatusSuspended : null,
            "Only an active admin can be suspended.", ct);

    public Task<AZOAResult<NodeAdminView>> ReactivateAsync(NodeAdminCaller caller, Guid id, CancellationToken ct)
        => TransitionAsync(caller, id, status => status == NodeAdminAccount.StatusSuspended ? NodeAdminAccount.StatusActive : null,
            "Only a suspended admin can be reactivated.", ct);

    public Task<AZOAResult<NodeAdminView>> RevokeAsync(NodeAdminCaller caller, Guid id, CancellationToken ct)
        => TransitionAsync(caller, id, status => status == NodeAdminAccount.StatusRevoked ? null : NodeAdminAccount.StatusRevoked,
            "The admin is already revoked.", ct);

    /// <summary>pending_activation → active after the fingerprint is confirmed out of band (H3); root or a dominating admin.</summary>
    public async Task<AZOAResult<NodeAdminView>> ActivateAsync(NodeAdminCaller caller, Guid id, NodeAdminActivateRequest request, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var roles = await resolver.SnapshotAsync(store, ct);
            var target = await store.GetAdminAsync(id.ToString("N"), ct);
            if (target is null) return Fail<NodeAdminView>(NodeAdminErrorCodes.NotFound, "Node admin not found.");
            if (target.Status != NodeAdminAccount.StatusPendingActivation)
                return Fail<NodeAdminView>(NodeAdminErrorCodes.ValidationFailed, "Only an admin awaiting activation can be activated.");
            if (!caller.IsRoot && !NodeAdminInvariants.Dominates(caller.Permissions, roles.PermissionsOf(target.RoleIds)))
                return Fail<NodeAdminView>(NodeAdminErrorCodes.DominanceRequired, "You must hold every permission of the admin you activate.");
            var credentials = await store.ListCredentialsAsync(target.Id, ct);
            if (!credentials.Any(credential => credential.RevokedAt is null
                    && string.Equals(credential.Fingerprint, request.Fingerprint, StringComparison.Ordinal)))
                return Fail<NodeAdminView>(NodeAdminErrorCodes.FingerprintMismatch,
                    "The fingerprint does not match the key this admin enrolled. Confirm it with them out of band.");
            var now = clock.GetUtcNow();
            var commit = await store.CommitAsync(roles.DirectoryRevision,
            [
                SurrealWriter.UpdateOnly<NodeAdminAccount>(target.Id)
                    .Where(row => row.AdminRevision == target.AdminRevision && row.Status == NodeAdminAccount.StatusPendingActivation)
                    .Set(row => row.Status, NodeAdminAccount.StatusActive)
                    .Set(row => row.AdminRevision, target.AdminRevision + 1)
                    .Set(row => row.UpdatedAt, now)
                    .Build(),
            ], ct);
            if (commit == NodeAdminCommit.Committed) return await ReloadAsync(target.Id, ct);
        }
        return ConflictResult<NodeAdminView>();
    }

    /// <summary>Replaces an admin's roles: dominance over the current roles, no escalation, no self-strip, last owner (M1).</summary>
    public async Task<AZOAResult<NodeAdminView>> AssignRolesAsync(NodeAdminCaller caller, Guid id, NodeAdminAssignRolesRequest request, CancellationToken ct)
    {
        var roleIds = (request.RoleIds ?? []).Distinct(StringComparer.Ordinal).ToArray();
        if (roleIds.Length > MaxRolesPerAdmin)
            return Fail<NodeAdminView>(NodeAdminErrorCodes.ValidationFailed, $"At most {MaxRolesPerAdmin} roles.");
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var roles = await resolver.SnapshotAsync(store, ct);
            var admins = await store.ListAdminsAsync(ct);
            var target = admins.FirstOrDefault(admin => admin.Id == id.ToString("N"));
            if (target is null) return Fail<NodeAdminView>(NodeAdminErrorCodes.NotFound, "Node admin not found.");
            if (target.Status == NodeAdminAccount.StatusRevoked)
                return Fail<NodeAdminView>(NodeAdminErrorCodes.ValidationFailed, "A revoked admin cannot be given roles.");
            if (roleIds.Any(roleId => !roles.Roles.ContainsKey(roleId)))
                return Fail<NodeAdminView>(NodeAdminErrorCodes.ValidationFailed, "One or more role ids do not exist.");
            var granted = roles.PermissionsOf(roleIds);
            if (!caller.IsRoot)
            {
                if (!NodeAdminInvariants.Dominates(caller.Permissions, roles.PermissionsOf(target.RoleIds)))
                    return Fail<NodeAdminView>(NodeAdminErrorCodes.DominanceRequired, "You must hold every permission of the admin you change.");
                if (NodeAdminInvariants.Escalates(caller.Permissions, granted))
                    return Fail<NodeAdminView>(NodeAdminErrorCodes.EscalationRefused, "You cannot grant permissions you do not hold.");
            }
            if (IsSelf(caller, target) && caller.Permissions.Contains(NodeAdminPermissions.AdminsManage)
                && !granted.Contains(NodeAdminPermissions.AdminsManage))
                return Fail<NodeAdminView>(NodeAdminErrorCodes.SelfActionRefused, "You cannot remove your own admins.manage permission.");
            if (NodeAdminInvariants.BreaksLastOwner(NodeAdminInvariants.OwnerCount(admins, roles),
                    NodeAdminInvariants.OwnerCount(NodeAdminInvariants.With(admins, NodeAdminInvariants.Proposed(target, roleIds: roleIds)), roles)))
                return LastOwner<NodeAdminView>();
            var now = clock.GetUtcNow();
            var commit = await store.CommitAsync(roles.DirectoryRevision,
            [
                SurrealWriter.UpdateOnly<NodeAdminAccount>(target.Id)
                    .Where(row => row.AdminRevision == target.AdminRevision)
                    .Set(row => row.RoleIds, (IReadOnlyList<string>)roleIds)
                    .Set(row => row.AdminRevision, target.AdminRevision + 1)
                    .Set(row => row.UpdatedAt, now)
                    .Build(),
            ], ct);
            if (commit != NodeAdminCommit.Committed) continue;
            await store.RevokeSessionsOfAsync(NodeAdminViews.Principal(target), now, ct);
            return await ReloadAsync(target.Id, ct);
        }
        return ConflictResult<NodeAdminView>();
    }

    /// <summary>Ends every session of an admin (session_revision bump plus row revocation).</summary>
    public async Task<AZOAResult<bool>> RevokeAdminSessionsAsync(NodeAdminCaller caller, Guid id, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var roles = await resolver.SnapshotAsync(store, ct);
            var target = await store.GetAdminAsync(id.ToString("N"), ct);
            if (target is null) return Fail<bool>(NodeAdminErrorCodes.NotFound, "Node admin not found.");
            if (!caller.IsRoot && !NodeAdminInvariants.Dominates(caller.Permissions, roles.PermissionsOf(target.RoleIds)))
                return Fail<bool>(NodeAdminErrorCodes.DominanceRequired, "You must hold every permission of the admin you change.");
            var now = clock.GetUtcNow();
            var commit = await store.CommitAsync(null,
            [
                SurrealWriter.UpdateOnly<NodeAdminAccount>(target.Id)
                    .Where(row => row.SessionRevision == target.SessionRevision)
                    .Set(row => row.SessionRevision, target.SessionRevision + 1)
                    .Set(row => row.UpdatedAt, now)
                    .Build(),
            ], ct);
            if (commit != NodeAdminCommit.Committed) continue;
            await store.RevokeSessionsOfAsync(NodeAdminViews.Principal(target), now, ct);
            return AZOAResult<bool>.Success(true, "All sessions of the admin were revoked.");
        }
        return ConflictResult<bool>();
    }

    /// <summary>Adds a key: the new key signs the add_key message; another admin's key needs root or a dominating owner-equivalent (C1, H2).</summary>
    public async Task<AZOAResult<NodeAdminKeyView>> AddKeyAsync(NodeAdminCaller caller, Guid id, NodeAdminAddKeyRequest request, CancellationToken ct)
    {
        if (runtime.Audience is not { } audience)
            return Fail<NodeAdminKeyView>(NodeAdminErrorCodes.Unavailable, "Node-admin sign-in is not configured on this node.");
        var roles = await resolver.SnapshotAsync(store, ct);
        var target = await store.GetAdminAsync(id.ToString("N"), ct);
        if (target is null) return Fail<NodeAdminKeyView>(NodeAdminErrorCodes.NotFound, "Node admin not found.");
        if (target.Status != NodeAdminAccount.StatusActive)
            return Fail<NodeAdminKeyView>(NodeAdminErrorCodes.ValidationFailed, "Keys can only be added to an active admin.");
        if (!IsSelf(caller, target) && !caller.IsRoot
            && !(caller.Permissions.Contains(NodeAdminPermissions.AdminsManage) && NodeAdminPermissions.IsOwnerEquivalent(caller.Permissions)
                && NodeAdminInvariants.Dominates(caller.Permissions, roles.PermissionsOf(target.RoleIds))))
            return Fail<NodeAdminKeyView>(NodeAdminErrorCodes.DominanceRequired,
                "Adding a key to another admin requires root or a node-owner-equivalent administrator.");
        if (!NodeAdminCrypto.TryDecodePublicKey(request.PublicKey, out var publicKey)
            || !NodeAdminCrypto.TryDecodeSignature(request.Signature, out var signature))
            return Fail<NodeAdminKeyView>(NodeAdminErrorCodes.ValidationFailed, "publicKey and signature must be base64url Ed25519 values.");
        var fingerprint = NodeAdminCrypto.Fingerprint(publicKey);
        if (runtime.IsRootKey(publicKey)
            || !NodeAdminCrypto.Verify(publicKey, NodeAdminCrypto.AddKeyMessage(audience, NodeAdminViews.WireId(target), fingerprint), signature))
            return Fail<NodeAdminKeyView>(NodeAdminErrorCodes.ValidationFailed, "The new key did not prove possession of its private key.");
        var existing = await store.GetCredentialAsync(fingerprint, ct);
        if (existing is not null && (existing.AdminId != target.Id || existing.RevokedAt is null))
            return Fail<NodeAdminKeyView>(NodeAdminErrorCodes.ValidationFailed, "That key is already registered.");

        var now = clock.GetUtcNow();
        var label = Clean(request.Label);
        label = label.Length > 64 ? label[..64] : label;
        var credential = existing is null
            ? SurrealWriter.Create(new NodeAdminCredential
            {
                Id = fingerprint,
                AdminId = target.Id,
                PublicKey = request.PublicKey,
                Fingerprint = fingerprint,
                Label = label,
                CreatedAt = now,
            })
            : SurrealWriter.UpdateOnly<NodeAdminCredential>(existing.Id)
                .Where(row => row.AdminId == target.Id && row.RevokedAt != null)
                .Unset(row => row.RevokedAt)
                .Set(row => row.Label, label)
                .Build();
        var commit = await store.CommitAsync(null,
        [
            credential,
            SurrealWriter.UpdateOnly<NodeAdminAccount>(target.Id)
                .Where(row => row.AdminRevision == target.AdminRevision && row.Status == NodeAdminAccount.StatusActive)
                .Set(row => row.AdminRevision, target.AdminRevision + 1)
                .Set(row => row.UpdatedAt, now)
                .Build(),
        ], ct);
        if (commit != NodeAdminCommit.Committed) return ConflictResult<NodeAdminKeyView>();
        return AZOAResult<NodeAdminKeyView>.Success(new NodeAdminKeyView(fingerprint, label, existing?.CreatedAt ?? now, null),
            "Key added. Sessions of this admin were ended; sign in again.");
    }

    /// <summary>Revokes one key; an admin cannot revoke their own only key, another admin's key needs dominance.</summary>
    public async Task<AZOAResult<NodeAdminKeyView>> RevokeKeyAsync(NodeAdminCaller caller, Guid id, string fingerprint, CancellationToken ct)
    {
        var roles = await resolver.SnapshotAsync(store, ct);
        var target = await store.GetAdminAsync(id.ToString("N"), ct);
        if (target is null) return Fail<NodeAdminKeyView>(NodeAdminErrorCodes.NotFound, "Node admin not found.");
        var active = (await store.ListCredentialsAsync(target.Id, ct)).Where(credential => credential.RevokedAt is null).ToArray();
        var credential = active.FirstOrDefault(item => string.Equals(item.Fingerprint, fingerprint, StringComparison.Ordinal));
        if (credential is null) return Fail<NodeAdminKeyView>(NodeAdminErrorCodes.NotFound, "No active key with that fingerprint.");
        if (IsSelf(caller, target))
        {
            if (active.Length == 1)
                return Fail<NodeAdminKeyView>(NodeAdminErrorCodes.SelfActionRefused, "You cannot revoke your only key.");
        }
        else if (!caller.IsRoot)
        {
            if (!caller.Permissions.Contains(NodeAdminPermissions.AdminsManage))
                return Fail<NodeAdminKeyView>(NodeAdminErrorCodes.PermissionDenied, "Revoking another admin's key requires admins.manage.");
            if (!NodeAdminInvariants.Dominates(caller.Permissions, roles.PermissionsOf(target.RoleIds)))
                return Fail<NodeAdminKeyView>(NodeAdminErrorCodes.DominanceRequired, "You must hold every permission of the admin you change.");
        }
        var now = clock.GetUtcNow();
        var commit = await store.CommitAsync(null,
        [
            SurrealWriter.UpdateOnly<NodeAdminCredential>(credential.Id)
                .Where(row => row.AdminId == target.Id && row.RevokedAt == null)
                .Set(row => row.RevokedAt, now)
                .Build(),
            SurrealWriter.UpdateOnly<NodeAdminAccount>(target.Id)
                .Where(row => row.AdminRevision == target.AdminRevision)
                .Set(row => row.AdminRevision, target.AdminRevision + 1)
                .Set(row => row.UpdatedAt, now)
                .Build(),
        ], ct);
        if (commit != NodeAdminCommit.Committed) return ConflictResult<NodeAdminKeyView>();
        await store.RevokeSessionsOfAsync(NodeAdminViews.Principal(target), now, ct);
        return AZOAResult<NodeAdminKeyView>.Success(new NodeAdminKeyView(credential.Fingerprint, credential.Label, credential.CreatedAt, now));
    }

    /// <summary>
    /// Resets MFA: back to pending_enrollment with every key and session revoked. Root or a dominating owner-equivalent may reset;
    /// only root receives the new enrollment token (C1), so a peer cannot take over another admin's identity.
    /// </summary>
    public async Task<AZOAResult<NodeAdminMfaResetView>> ResetMfaAsync(NodeAdminCaller caller, Guid id, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var roles = await resolver.SnapshotAsync(store, ct);
            var admins = await store.ListAdminsAsync(ct);
            var target = admins.FirstOrDefault(admin => admin.Id == id.ToString("N"));
            if (target is null) return Fail<NodeAdminMfaResetView>(NodeAdminErrorCodes.NotFound, "Node admin not found.");
            if (IsSelf(caller, target))
                return Fail<NodeAdminMfaResetView>(NodeAdminErrorCodes.SelfActionRefused, "You cannot reset your own MFA.");
            if (target.Status == NodeAdminAccount.StatusRevoked)
                return Fail<NodeAdminMfaResetView>(NodeAdminErrorCodes.ValidationFailed, "A revoked admin cannot be re-enrolled.");
            if (!caller.IsRoot && !(NodeAdminPermissions.IsOwnerEquivalent(caller.Permissions)
                    && NodeAdminInvariants.Dominates(caller.Permissions, roles.PermissionsOf(target.RoleIds))))
                return Fail<NodeAdminMfaResetView>(NodeAdminErrorCodes.DominanceRequired,
                    "Resetting another admin's MFA requires root or a node-owner-equivalent administrator.");
            if (NodeAdminInvariants.BreaksLastOwner(NodeAdminInvariants.OwnerCount(admins, roles),
                    NodeAdminInvariants.OwnerCount(NodeAdminInvariants.With(admins,
                        NodeAdminInvariants.Proposed(target, status: NodeAdminAccount.StatusPendingEnrollment)), roles)))
                return LastOwner<NodeAdminMfaResetView>();

            var now = clock.GetUtcNow();
            var mutations = new List<SurrealQuery>
            {
                SurrealWriter.UpdateOnly<NodeAdminAccount>(target.Id)
                    .Where(row => row.AdminRevision == target.AdminRevision && row.Status == target.Status)
                    .Set(row => row.Status, NodeAdminAccount.StatusPendingEnrollment)
                    .Unset(row => row.TotpSecretProtected)
                    .Set(row => row.TotpLastStep, 0L)
                    .Set(row => row.FailedAttempts, 0L)
                    .Unset(row => row.LockedUntil)
                    .Set(row => row.AdminRevision, target.AdminRevision + 1)
                    .Set(row => row.SessionRevision, target.SessionRevision + 1)
                    .Set(row => row.UpdatedAt, now)
                    .Build(),
            };
            foreach (var credential in (await store.ListCredentialsAsync(target.Id, ct)).Where(item => item.RevokedAt is null))
                mutations.Add(SurrealWriter.UpdateOnly<NodeAdminCredential>(credential.Id)
                    .Where(row => row.AdminId == target.Id && row.RevokedAt == null).Set(row => row.RevokedAt, now).Build());
            foreach (var open in await store.ListOpenEnrollmentsAsync(target.Id, ct))
                mutations.Add(SurrealWriter.UpdateOnly<NodeAdminEnrollment>(open.Id)
                    .Where(row => row.ConsumedAt == null).Set(row => row.ConsumedAt, now).Build());
            string? token = null;
            DateTimeOffset? expiresAt = null;
            if (caller.IsRoot)
            {
                var (issued, enrollment) = NewEnrollment(target.Id, caller.Principal, now);
                mutations.Add(SurrealWriter.Create(enrollment));
                token = issued;
                expiresAt = enrollment.ExpiresAt;
            }
            if (await store.CommitAsync(roles.DirectoryRevision, mutations, ct) != NodeAdminCommit.Committed) continue;
            await store.RevokeSessionsOfAsync(NodeAdminViews.Principal(target), now, ct);
            var view = await ViewAsync((await store.GetAdminAsync(target.Id, ct))!, ct);
            return AZOAResult<NodeAdminMfaResetView>.Success(new NodeAdminMfaResetView(view, token, expiresAt), token is null
                ? "MFA reset. Only root can issue the new enrollment token."
                : "MFA reset. Deliver the enrollment token out of band; it is shown once.");
        }
        return ConflictResult<NodeAdminMfaResetView>();
    }

    /// <summary>Own sessions, or another principal's with admins.read.</summary>
    public async Task<AZOAResult<IReadOnlyList<NodeAdminSessionView>>> ListSessionsAsync(NodeAdminCaller caller, string? principal, CancellationToken ct)
    {
        var subject = string.IsNullOrWhiteSpace(principal) ? caller.Principal : principal.Trim();
        if (!IsPrincipal(subject))
            return Fail<IReadOnlyList<NodeAdminSessionView>>(NodeAdminErrorCodes.ValidationFailed, "principal must be admin:<id> or root:<fingerprint>.");
        if (subject != caller.Principal && !caller.Permissions.Contains(NodeAdminPermissions.AdminsRead))
            return Fail<IReadOnlyList<NodeAdminSessionView>>(NodeAdminErrorCodes.PermissionDenied, "Listing another principal's sessions requires admins.read.");
        IReadOnlyList<NodeAdminSessionView> views = (await store.ListSessionsAsync(subject, clock.GetUtcNow(), ct))
            .Select(session => NodeAdminViews.Session(session, session.Id == caller.SessionRecordId)).ToArray();
        return AZOAResult<IReadOnlyList<NodeAdminSessionView>>.Success(views);
    }

    /// <summary>Revokes one session: own freely; another principal's with admins.manage and dominance.</summary>
    public async Task<AZOAResult<bool>> RevokeSessionAsync(NodeAdminCaller caller, string sid, CancellationToken ct)
    {
        if (!NodeAdminCrypto.TrySessionRecordId(sid, out var recordId) || await store.GetSessionAsync(recordId, ct) is not { } session)
            return Fail<bool>(NodeAdminErrorCodes.NotFound, "Session not found.");
        if (session.Principal != caller.Principal && !caller.IsRoot)
        {
            if (!caller.Permissions.Contains(NodeAdminPermissions.AdminsManage))
                return Fail<bool>(NodeAdminErrorCodes.PermissionDenied, "Revoking another principal's session requires admins.manage.");
            if (!NodeAdminInvariants.Dominates(caller.Permissions, await PermissionsOfPrincipalAsync(session.Principal, ct)))
                return Fail<bool>(NodeAdminErrorCodes.DominanceRequired, "You must hold every permission of the session's principal.");
        }
        await store.TryRevokeSessionAsync(recordId, clock.GetUtcNow(), ct);
        return AZOAResult<bool>.Success(true, "Session revoked.");
    }

    public async Task<AZOAResult<IReadOnlyList<NodeAdminRoleView>>> ListRolesAsync(CancellationToken ct)
    {
        IReadOnlyList<NodeAdminRoleView> views = (await store.ListRolesAsync(ct)).Select(NodeAdminViews.Role).ToArray();
        return AZOAResult<IReadOnlyList<NodeAdminRoleView>>.Success(views);
    }

    public async Task<AZOAResult<NodeAdminRoleView>> CreateRoleAsync(NodeAdminCaller caller, NodeAdminRoleRequest request, CancellationToken ct)
    {
        if (ValidateRole(request, out var name, out var permissions) is { } invalid) return invalid;
        if (!caller.IsRoot && NodeAdminInvariants.Escalates(caller.Permissions, permissions))
            return Fail<NodeAdminRoleView>(NodeAdminErrorCodes.EscalationRefused, "You cannot create a role with permissions you do not hold.");
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var roles = await resolver.SnapshotAsync(store, ct);
            if (roles.Roles.Values.Any(role => role.Name == name))
                return Fail<NodeAdminRoleView>(NodeAdminErrorCodes.ValidationFailed, "A role with that name exists.");
            var now = clock.GetUtcNow();
            var role = new NodeAdminRole
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                BuiltIn = false,
                Permissions = permissions,
                Revision = 1,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var commit = await store.CommitAsync(roles.DirectoryRevision, [SurrealWriter.Create(role)], ct);
            if (commit == NodeAdminCommit.Duplicate)
                return Fail<NodeAdminRoleView>(NodeAdminErrorCodes.ValidationFailed, "A role with that name exists.");
            if (commit == NodeAdminCommit.Committed) return AZOAResult<NodeAdminRoleView>.Success(NodeAdminViews.Role(role));
        }
        return ConflictResult<NodeAdminRoleView>();
    }

    /// <summary>Edits a custom role: dominance over its old permissions, no escalation, no self-strip, last owner (M1).</summary>
    public async Task<AZOAResult<NodeAdminRoleView>> UpdateRoleAsync(NodeAdminCaller caller, string roleId, NodeAdminRoleRequest request, CancellationToken ct)
    {
        if (ValidateRole(request, out var name, out var permissions) is { } invalid) return invalid;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var roles = await resolver.SnapshotAsync(store, ct);
            if (!roles.Roles.TryGetValue(roleId, out var role)) return Fail<NodeAdminRoleView>(NodeAdminErrorCodes.NotFound, "Role not found.");
            if (role.BuiltIn) return Fail<NodeAdminRoleView>(NodeAdminErrorCodes.RoleBuiltIn, "Built-in roles are immutable.");
            if (roles.Roles.Values.Any(other => other.Id != role.Id && other.Name == name))
                return Fail<NodeAdminRoleView>(NodeAdminErrorCodes.ValidationFailed, "A role with that name exists.");
            if (!caller.IsRoot)
            {
                if (!NodeAdminInvariants.Dominates(caller.Permissions, roles.PermissionsOf([role.Id])))
                    return Fail<NodeAdminRoleView>(NodeAdminErrorCodes.DominanceRequired, "You must hold every permission of the role you edit.");
                if (NodeAdminInvariants.Escalates(caller.Permissions, permissions))
                    return Fail<NodeAdminRoleView>(NodeAdminErrorCodes.EscalationRefused, "You cannot grant permissions you do not hold.");
            }
            var admins = await store.ListAdminsAsync(ct);
            var proposed = NodeAdminInvariants.WithRole(roles, role.Id, permissions);
            if (caller.Admin is not null && caller.Permissions.Contains(NodeAdminPermissions.AdminsManage)
                && admins.FirstOrDefault(admin => admin.Id == caller.Admin.Id) is { } self
                && !proposed.EffectivePermissions(self).Contains(NodeAdminPermissions.AdminsManage))
                return Fail<NodeAdminRoleView>(NodeAdminErrorCodes.SelfActionRefused, "This edit would remove your own admins.manage permission.");
            if (NodeAdminInvariants.BreaksLastOwner(NodeAdminInvariants.OwnerCount(admins, roles), NodeAdminInvariants.OwnerCount(admins, proposed)))
                return LastOwner<NodeAdminRoleView>();
            var now = clock.GetUtcNow();
            var commit = await store.CommitAsync(roles.DirectoryRevision,
            [
                SurrealWriter.UpdateOnly<NodeAdminRole>(role.Id)
                    .Where(row => row.Revision == role.Revision && row.BuiltIn == false)
                    .Set(row => row.Name, name)
                    .Set(row => row.Permissions, permissions)
                    .Set(row => row.Revision, role.Revision + 1)
                    .Set(row => row.UpdatedAt, now)
                    .Build(),
            ], ct);
            if (commit == NodeAdminCommit.Duplicate)
                return Fail<NodeAdminRoleView>(NodeAdminErrorCodes.ValidationFailed, "A role with that name exists.");
            if (commit == NodeAdminCommit.Committed)
                return AZOAResult<NodeAdminRoleView>.Success(new NodeAdminRoleView(role.Id, name, false, permissions, role.Revision + 1));
        }
        return ConflictResult<NodeAdminRoleView>();
    }

    public async Task<AZOAResult<bool>> DeleteRoleAsync(NodeAdminCaller caller, string roleId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var roles = await resolver.SnapshotAsync(store, ct);
            if (!roles.Roles.TryGetValue(roleId, out var role)) return Fail<bool>(NodeAdminErrorCodes.NotFound, "Role not found.");
            if (role.BuiltIn) return Fail<bool>(NodeAdminErrorCodes.RoleBuiltIn, "Built-in roles are immutable.");
            if (!caller.IsRoot && !NodeAdminInvariants.Dominates(caller.Permissions, roles.PermissionsOf([role.Id])))
                return Fail<bool>(NodeAdminErrorCodes.DominanceRequired, "You must hold every permission of the role you delete.");
            var admins = await store.ListAdminsAsync(ct);
            if (admins.Any(admin => admin.Status != NodeAdminAccount.StatusRevoked && admin.RoleIds.Contains(role.Id)))
                return Fail<bool>(NodeAdminErrorCodes.RoleInUse, "Unassign the role from every admin first.");
            var commit = await store.CommitAsync(roles.DirectoryRevision, [], ct,
                [SurrealWriter.DeleteOnly<NodeAdminRole>(role.Id).Where(row => row.Revision == role.Revision && row.BuiltIn == false).Build()]);
            if (commit == NodeAdminCommit.Committed) return AZOAResult<bool>.Success(true, "Role deleted.");
        }
        return ConflictResult<bool>();
    }

    public async Task<AZOAResult<NodeAdminAuditPageView>> ListAuditAsync(long afterSeq, int limit, CancellationToken ct)
    {
        var rows = await audit.ListAsync(Math.Max(0, afterSeq), limit, ct);
        var head = await audit.GetHeadAsync(ct);
        return AZOAResult<NodeAdminAuditPageView>.Success(new NodeAdminAuditPageView(
            rows.Select(NodeAdminViews.AuditRow).ToArray(),
            new NodeAdminAuditHeadView(head?.Seq ?? 0, head?.Hash ?? NodeAdminAuditLog.GenesisHash)));
    }

    public async Task<AZOAResult<NodeAdminAuditVerifyView>> VerifyAuditAsync(CancellationToken ct)
    {
        var result = await audit.VerifyAsync(ct);
        return AZOAResult<NodeAdminAuditVerifyView>.Success(
            new NodeAdminAuditVerifyView(result.Ok, result.VerifiedThroughSeq, result.HeadHash, result.FirstBrokenSeq));
    }

    private async Task<AZOAResult<NodeAdminView>> TransitionAsync(
        NodeAdminCaller caller, Guid id, Func<string, string?> next, string invalidMessage, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var roles = await resolver.SnapshotAsync(store, ct);
            var admins = await store.ListAdminsAsync(ct);
            var target = admins.FirstOrDefault(admin => admin.Id == id.ToString("N"));
            if (target is null) return Fail<NodeAdminView>(NodeAdminErrorCodes.NotFound, "Node admin not found.");
            if (IsSelf(caller, target))
                return Fail<NodeAdminView>(NodeAdminErrorCodes.SelfActionRefused, "You cannot change your own status.");
            if (next(target.Status) is not { } status) return Fail<NodeAdminView>(NodeAdminErrorCodes.ValidationFailed, invalidMessage);
            if (!caller.IsRoot && !NodeAdminInvariants.Dominates(caller.Permissions, roles.PermissionsOf(target.RoleIds)))
                return Fail<NodeAdminView>(NodeAdminErrorCodes.DominanceRequired, "You must hold every permission of the admin you change.");
            if (NodeAdminInvariants.BreaksLastOwner(NodeAdminInvariants.OwnerCount(admins, roles),
                    NodeAdminInvariants.OwnerCount(NodeAdminInvariants.With(admins, NodeAdminInvariants.Proposed(target, status: status)), roles)))
                return LastOwner<NodeAdminView>();
            var now = clock.GetUtcNow();
            var commit = await store.CommitAsync(roles.DirectoryRevision,
            [
                SurrealWriter.UpdateOnly<NodeAdminAccount>(target.Id)
                    .Where(row => row.AdminRevision == target.AdminRevision && row.Status == target.Status)
                    .Set(row => row.Status, status)
                    .Set(row => row.AdminRevision, target.AdminRevision + 1)
                    .Set(row => row.UpdatedAt, now)
                    .Build(),
            ], ct);
            if (commit != NodeAdminCommit.Committed) continue;
            if (status != NodeAdminAccount.StatusActive)
                await store.RevokeSessionsOfAsync(NodeAdminViews.Principal(target), now, ct);
            return await ReloadAsync(target.Id, ct);
        }
        return ConflictResult<NodeAdminView>();
    }

    private async Task<IReadOnlySet<string>> PermissionsOfPrincipalAsync(string principal, CancellationToken ct)
    {
        if (principal.StartsWith(NodeAdminClaims.RootPrincipalPrefix, StringComparison.Ordinal)) return NodeAdminPermissions.Root;
        if (!Guid.TryParseExact(principal[NodeAdminClaims.AdminPrincipalPrefix.Length..], "D", out var adminId)
            || await store.GetAdminAsync(adminId.ToString("N"), ct) is not { } admin)
            return new HashSet<string>(StringComparer.Ordinal);
        return (await resolver.SnapshotAsync(store, ct)).PermissionsOf(admin.RoleIds);
    }

    private (string Token, NodeAdminEnrollment Enrollment) NewEnrollment(string adminRecordId, string createdBy, DateTimeOffset now)
    {
        var token = NodeAdminCrypto.RandomToken();
        return (token, new NodeAdminEnrollment
        {
            Id = Guid.NewGuid().ToString("N"),
            AdminId = adminRecordId,
            TokenHash = NodeAdminCrypto.HashSecret(token),
            ExpiresAt = now + runtime.Options.EnrollmentLifetime,
            CreatedBy = createdBy,
            CreatedAt = now,
        });
    }

    private async Task<NodeAdminView> ViewAsync(NodeAdminAccount admin, CancellationToken ct)
        => NodeAdminViews.Admin(admin, await resolver.SnapshotAsync(store, ct), await store.ListCredentialsAsync(admin.Id, ct));

    private async Task<AZOAResult<NodeAdminView>> ReloadAsync(string recordId, CancellationToken ct)
        => AZOAResult<NodeAdminView>.Success(await ViewAsync((await store.GetAdminAsync(recordId, ct))!, ct));

    private static AZOAResult<NodeAdminRoleView>? ValidateRole(NodeAdminRoleRequest request, out string name, out IReadOnlyList<string> permissions)
    {
        name = (request.Name ?? string.Empty).Trim();
        var requested = request.Permissions ?? [];
        permissions = NodeAdminPermissions.Normalize(requested);
        if (!RoleNamePattern().IsMatch(name) || NodeAdminPermissions.IsBuiltInRoleId(name))
            return Fail<NodeAdminRoleView>(NodeAdminErrorCodes.ValidationFailed, "name must be 2-48 lowercase letters, digits or '-' and not a built-in role name.");
        if (requested.Any(permission => !NodeAdminPermissions.IsKnown(permission)))
            return Fail<NodeAdminRoleView>(NodeAdminErrorCodes.UnknownPermission, "One or more permissions are not in the catalog.");
        if (permissions.Count == 0)
            return Fail<NodeAdminRoleView>(NodeAdminErrorCodes.ValidationFailed, "A role needs at least one permission.");
        return null;
    }

    private static bool IsSelf(NodeAdminCaller caller, NodeAdminAccount target) => caller.Admin?.Id == target.Id;

    private static bool IsPrincipal(string value)
        => (value.StartsWith(NodeAdminClaims.AdminPrincipalPrefix, StringComparison.Ordinal)
                && Guid.TryParseExact(value[NodeAdminClaims.AdminPrincipalPrefix.Length..], "D", out _))
            || (value.StartsWith(NodeAdminClaims.RootPrincipalPrefix, StringComparison.Ordinal)
                && NodeAdminCrypto.IsFingerprint(value[NodeAdminClaims.RootPrincipalPrefix.Length..]));

    private static string Clean(string? value)
        => new string((value ?? string.Empty).Where(character => !char.IsControl(character)).ToArray()).Trim();

    private static AZOAResult<T> Fail<T>(string code, string message) => NodeAdminResults.Fail<T>(code, message);

    private static AZOAResult<T> LastOwner<T>()
        => Fail<T>(NodeAdminErrorCodes.LastOwner, "This change would leave no active admin holding admins.manage and roles.manage.");

    private static AZOAResult<T> ConflictResult<T>()
        => Fail<T>(NodeAdminErrorCodes.Conflict, "The node-admin directory changed concurrently; retry.");

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,47}$", RegexOptions.CultureInvariant)]
    private static partial Regex RoleNamePattern();
}
