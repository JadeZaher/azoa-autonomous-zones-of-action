using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

public interface IHolonManager
{
    Task<AZOAResult<IHolon>> GetAsync(Guid id, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<IHolon>>> GetAllAsync(AZOARequest? request = null);
    Task<AZOAResult<IHolon>> CreateAsync(HolonCreateModel model, Guid avatarId, AZOARequest? request = null);
    // avatarId scopes ownership: a non-null value enforces the IsOwnedBy guard
    // (closes the IDOR for the HTTP routes). A null avatarId is the trusted
    // internal path (quest node handlers) which has no avatar context and runs
    // unscoped — never pass null from a controller.
    Task<AZOAResult<IHolon>> UpdateAsync(Guid id, HolonUpdateModel model, Guid? avatarId = null, AZOARequest? request = null);
    Task<AZOAResult<bool>> DeleteAsync(Guid id, Guid? avatarId = null, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<IHolon>>> QueryAsync(HolonQueryRequest query, AZOARequest? request = null);
    Task<AZOAResult<IHolon>> InteractAsync(Guid id, HolonInteractionRequest request, Guid? avatarId = null, AZOARequest? providerRequest = null);

    // Holarchy traversal — expose the holonic structure
    Task<AZOAResult<IEnumerable<IHolon>>> GetChildrenAsync(Guid parentId, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<IHolon>>> GetPeersAsync(Guid id, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<IHolon>>> GetAncestorsAsync(Guid id, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<IHolon>>> GetDescendantsAsync(Guid id, AZOARequest? request = null);

    // Holonic functionality — operations across the holarchy
    Task<AZOAResult<int>> PropagateAsync(Guid id, HolonPropagateRequest request, Guid? avatarId = null, AZOARequest? providerRequest = null);
    Task<AZOAResult<HolonComposition>> ComposeAsync(Guid id, AZOARequest? request = null);
    Task<AZOAResult<IHolon>> CloneAsync(Guid id, HolonCloneRequest request, Guid avatarId, AZOARequest? providerRequest = null);
    Task<AZOAResult<bool>> MoveSubtreeAsync(Guid id, Guid newParentId, Guid? avatarId = null, AZOARequest? request = null);
}
