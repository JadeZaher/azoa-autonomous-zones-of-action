using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Interfaces.Managers;

public interface ISTARManager
{
    Task<OASISResult<ISTARODK>> GetAsync(Guid id, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<ISTARODK>>> GetAllAsync(OASISRequest? request = null);
    Task<OASISResult<ISTARODK>> CreateOrUpdateAsync(STARODKCreateModel model, OASISRequest? request = null);
    Task<OASISResult<bool>> DeleteAsync(Guid id, OASISRequest? request = null);
    Task<OASISResult<ISTARODK>> GenerateAsync(Guid id, STARDappGenerationRequest request, OASISRequest? providerRequest = null);
    Task<OASISResult<ISTARODK>> DeployAsync(Guid id, OASISRequest? providerRequest = null);
}
