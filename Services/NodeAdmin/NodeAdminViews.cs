using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.NodeAdmin;

/// <summary>Projections from storage rows to the node-admin wire schemas (plan §8 Schemas).</summary>
public static class NodeAdminViews
{
    /// <summary>Wire status: pending_enrollment | pending_activation | active | suspended | revoked.</summary>
    public static string WireStatus(string status) => status switch
    {
        NodeAdminAccount.StatusPendingEnrollment => "pending_enrollment",
        NodeAdminAccount.StatusPendingActivation => "pending_activation",
        NodeAdminAccount.StatusActive => "active",
        NodeAdminAccount.StatusSuspended => "suspended",
        _ => "revoked",
    };

    public static string WireId(NodeAdminAccount admin) => Guid.ParseExact(admin.Id, "N").ToString("D");

    public static string Principal(NodeAdminAccount admin) => NodeAdminClaims.AdminPrincipal(Guid.ParseExact(admin.Id, "N"));

    public static NodeAdminRoleView Role(NodeAdminRole role)
        => new(role.Id, role.Name, role.BuiltIn, NodeAdminPermissions.Normalize(role.Permissions), role.Revision);

    public static NodeAdminKeyView Key(NodeAdminCredential credential)
        => new(credential.Fingerprint, credential.Label, credential.CreatedAt, credential.RevokedAt);

    public static NodeAdminView Admin(NodeAdminAccount admin, NodeAdminRoleSnapshot roles, IEnumerable<NodeAdminCredential> credentials)
        => new(
            WireId(admin),
            admin.Username,
            admin.DisplayName,
            WireStatus(admin.Status),
            admin.RoleIds.Where(roles.Roles.ContainsKey).Select(id => Role(roles.Roles[id])).ToArray(),
            NodeAdminPermissions.Normalize(roles.EffectivePermissions(admin)),
            admin.CreatedBy,
            admin.CreatedAt,
            admin.UpdatedAt,
            credentials.Select(Key).ToArray(),
            admin.TotpSecretProtected is not null);

    public static NodeAdminSessionView Session(NodeAdminSession session, bool current)
        => new(session.Handle, session.Principal, session.AuthTime, session.Amr, session.ExpiresAt,
            session.RevokedAt, session.Ip, current);

    /// <summary>Strips control characters, trims and truncates caller-supplied display text.</summary>
    public static string CleanText(string? value, int maxLength)
    {
        var clean = new string((value ?? string.Empty).Where(character => !char.IsControl(character)).ToArray()).Trim();
        return clean.Length > maxLength ? clean[..maxLength] : clean;
    }

    public static NodeAdminAuditRowView AuditRow(NodeAdminAudit row)
        => new(row.Seq, row.At, row.Actor, row.Amr, row.Action, row.Target, row.Outcome, row.Detail, row.RequestId, row.PrevHash, row.Hash);
}
