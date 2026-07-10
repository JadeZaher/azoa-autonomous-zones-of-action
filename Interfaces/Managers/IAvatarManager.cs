using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

public interface IAvatarManager
{
    Task<AZOAResult<IAvatar>> RegisterAsync(AvatarRegisterModel model, AZOARequest? request = null);
    Task<AZOAResult<string>> LoginAsync(AvatarLoginModel model, AZOARequest? request = null);
    Task<AZOAResult<IAvatar>> GetAsync(Guid id, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<IAvatar>>> GetAllAsync(AZOARequest? request = null);
    Task<AZOAResult<IAvatar>> UpdateAsync(Guid id, AvatarUpdateModel model, Guid avatarId, AZOARequest? request = null);
    Task<AZOAResult<bool>> DeleteAsync(Guid id, Guid avatarId, AZOARequest? request = null);

    /// <summary>Bumps the avatar's AuthNotBefore watermark to now, invalidating every
    /// live JWT it issued (server-side "logout everywhere"). See Managers/AGENTS.md.</summary>
    Task<AZOAResult<bool>> LogoutEverywhereAsync(Guid avatarId, CancellationToken ct = default);

    /// <summary>
    /// avatar-dapp-rbac: assign <paramref name="targetAvatarId"/>'s DApp role. Authority is
    /// decided by the caller's flags: an operator may set ANY assignable role (incl.
    /// manager — the operator-bootstrap path); a manager may set only developer/user;
    /// anyone else is denied. <paramref name="role"/> is validated against the
    /// AzoaDappRoles allowlist — an operator:admin-yielding value is impossible.
    /// See Managers/AGENTS.md §avatar-dapp-rbac.
    /// </summary>
    Task<AZOAResult<IAvatar>> AssignDappRoleAsync(
        Guid targetAvatarId, string role, bool actingIsOperator, bool actingCanManage, CancellationToken ct = default);
}
