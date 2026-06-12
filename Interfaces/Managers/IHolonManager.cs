using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Interfaces.Managers;

public interface IHolonManager
{
    Task<OASISResult<IHolon>> GetAsync(Guid id, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<IHolon>>> GetAllAsync(OASISRequest? request = null);
    Task<OASISResult<IHolon>> CreateAsync(HolonCreateModel model, Guid avatarId, OASISRequest? request = null);
    // avatarId scopes ownership: a non-null value enforces the IsOwnedBy guard
    // (closes the IDOR for the HTTP routes). A null avatarId is the trusted
    // internal path (quest node handlers) which has no avatar context and runs
    // unscoped — never pass null from a controller.
    Task<OASISResult<IHolon>> UpdateAsync(Guid id, HolonUpdateModel model, Guid? avatarId = null, OASISRequest? request = null);
    Task<OASISResult<bool>> DeleteAsync(Guid id, Guid? avatarId = null, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<IHolon>>> QueryAsync(HolonQueryRequest query, OASISRequest? request = null);
    Task<OASISResult<IHolon>> InteractAsync(Guid id, HolonInteractionRequest request, Guid? avatarId = null, OASISRequest? providerRequest = null);

    // Holarchy traversal — expose the holonic structure
    Task<OASISResult<IEnumerable<IHolon>>> GetChildrenAsync(Guid parentId, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<IHolon>>> GetPeersAsync(Guid id, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<IHolon>>> GetAncestorsAsync(Guid id, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<IHolon>>> GetDescendantsAsync(Guid id, OASISRequest? request = null);

    // Holonic functionality — operations across the holarchy
    Task<OASISResult<int>> PropagateAsync(Guid id, HolonPropagateRequest request, Guid? avatarId = null, OASISRequest? providerRequest = null);
    Task<OASISResult<HolonComposition>> ComposeAsync(Guid id, OASISRequest? request = null);
    Task<OASISResult<IHolon>> CloneAsync(Guid id, HolonCloneRequest request, Guid avatarId, OASISRequest? providerRequest = null);
    Task<OASISResult<bool>> MoveSubtreeAsync(Guid id, Guid newParentId, Guid? avatarId = null, OASISRequest? request = null);
}
