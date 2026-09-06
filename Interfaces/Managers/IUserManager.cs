using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

public interface IUserManager
{
    Task<AZOAResult<IUser>> RegisterAsync(UserRegisterModel model, AZOARequest? request = null);
    Task<AZOAResult<string>> LoginAsync(UserLoginModel model, AZOARequest? request = null);
    Task<AZOAResult<IUser>> GetAsync(Guid id, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<IUser>>> GetAllAsync(AZOARequest? request = null);
    Task<AZOAResult<IUser>> UpdateAsync(Guid id, UserUpdateModel model, Guid userId, AZOARequest? request = null);
    Task<AZOAResult<bool>> DeleteAsync(Guid id, Guid userId, AZOARequest? request = null);

    /// <summary>Bumps the user's AuthNotBefore watermark to now, invalidating every
    /// live JWT it issued (server-side "logout everywhere"). See Managers/AGENTS.md.</summary>
    Task<AZOAResult<bool>> LogoutEverywhereAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// user-dapp-rbac: assign <paramref name="targetUserId"/>'s DApp role. Authority is
    /// decided by the caller's flags: an operator may set ANY assignable role (incl.
    /// manager — the operator-bootstrap path); a manager may set only developer/user;
    /// anyone else is denied. <paramref name="role"/> is validated against the
    /// AzoaDappRoles allowlist — an operator:admin-yielding value is impossible.
    /// See Managers/AGENTS.md §user-dapp-rbac.
    /// </summary>
    Task<AZOAResult<IUser>> AssignDappRoleAsync(
        Guid targetUserId, string role, bool actingIsOperator, bool actingCanManage, CancellationToken ct = default);
}
