namespace AZOA.WebAPI.Core;

public static class AzoaDappRoles
{
    public const string User = "dapp:user";
    public const string Manager = "dapp:manager";
    public const string Developer = "dapp:developer";

    public static string Normalize(string? role)
        => string.IsNullOrWhiteSpace(role) ? User : role.Trim();

    public static bool CanDevelop(string? role)
    {
        var normalized = Normalize(role);
        return string.Equals(normalized, Manager, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, Developer, StringComparison.OrdinalIgnoreCase);
    }

    public static bool CanManage(string? role)
        => string.Equals(Normalize(role), Manager, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The complete, canonical set of assignable DApp roles. operator:admin is
    /// deliberately absent — operator authority is a JWT-only scope, never a DApp
    /// role (see AzoaScopes.Operator). The role-assignment endpoint rejects any
    /// value not in this set, so a caller can never set a role that yields
    /// operator:admin.
    /// </summary>
    public static readonly System.Collections.Generic.IReadOnlySet<string> AssignableRoles =
        new System.Collections.Generic.HashSet<string>(new[] { User, Developer, Manager }, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True iff <paramref name="role"/> normalizes to one of the canonical assignable
    /// DApp roles. Blank normalizes to <see cref="User"/> and is valid; any unknown
    /// token (including <c>operator:admin</c>) is rejected.
    /// </summary>
    public static bool IsAssignableRole(string? role)
        => AssignableRoles.Contains(Normalize(role));
}
