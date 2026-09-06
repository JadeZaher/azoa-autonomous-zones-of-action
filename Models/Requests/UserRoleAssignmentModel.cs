namespace AZOA.WebAPI.Models.Requests;

/// <summary>
/// Body for <c>PUT api/user/{id}/dapp-role</c> (user-dapp-rbac). Assigns the
/// target user's DApp role. The target user id comes from the ROUTE, never this
/// body (IDOR rule). <see cref="Role"/> is validated against the
/// <c>AzoaDappRoles</c> allowlist; an operator:admin-yielding value is impossible
/// because DappRole only ranges over dapp:user/dapp:developer/dapp:manager. See
/// Controllers/AGENTS.md §user-dapp-rbac.
/// </summary>
public class UserRoleAssignmentModel
{
    /// <summary>The DApp role to assign — one of <c>dapp:user</c>/<c>dapp:developer</c>/<c>dapp:manager</c>.</summary>
    public string Role { get; set; } = string.Empty;
}
