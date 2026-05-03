using System.Text.Json;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Managers;

public class STARManager : ISTARManager
{
    private readonly ProviderContext _providerContext;

    public STARManager(ProviderContext providerContext)
    {
        _providerContext = providerContext;
    }

    public async Task<OASISResult<ISTARODK>> GetAsync(Guid id, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<ISTARODK> { IsError = true, Message = activation.Message };

        return await _providerContext.CurrentProvider.LoadSTARODKAsync(id);
    }

    public async Task<OASISResult<IEnumerable<ISTARODK>>> GetAllAsync(OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IEnumerable<ISTARODK>> { IsError = true, Message = activation.Message };

        return await _providerContext.CurrentProvider.LoadAllSTARODKsAsync();
    }

    public async Task<OASISResult<ISTARODK>> CreateOrUpdateAsync(STARODKCreateModel model, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<ISTARODK> { IsError = true, Message = activation.Message };

        var existing = await _providerContext.CurrentProvider.LoadAllSTARODKsAsync();
        var match = existing.Result?.FirstOrDefault(s => s.Name.Equals(model.Name, StringComparison.OrdinalIgnoreCase));

        var odk = match as STARODK ?? new STARODK();
        odk.Name = model.Name;
        odk.Description = model.Description;
        odk.PublicKey = model.PublicKey;
        odk.AvatarId = model.AvatarId;
        odk.ModifiedDate = DateTime.UtcNow;

        return await _providerContext.CurrentProvider.SaveSTARODKAsync(odk);
    }

    public async Task<OASISResult<bool>> DeleteAsync(Guid id, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<bool> { IsError = true, Message = activation.Message };

        return await _providerContext.CurrentProvider.DeleteSTARODKAsync(id);
    }

    public async Task<OASISResult<ISTARODK>> GenerateAsync(Guid id, STARDappGenerationRequest request, OASISRequest? providerRequest = null)
    {
        var activation = _providerContext.Activate(providerRequest);
        if (activation.IsError) return new OASISResult<ISTARODK> { IsError = true, Message = activation.Message };

        var existing = await _providerContext.CurrentProvider.LoadSTARODKAsync(id);
        if (existing.IsError || existing.Result == null) return existing;

        var odk = (STARODK)existing.Result;
        odk.TargetChain = request.TargetChain;
        odk.BoundHolonIds = request.BoundHolonIds;
        odk.GeneratedCode = GenerateDappCode(odk, request);
        odk.ModifiedDate = DateTime.UtcNow;

        return await _providerContext.CurrentProvider.SaveSTARODKAsync(odk);
    }

    public async Task<OASISResult<ISTARODK>> DeployAsync(Guid id, OASISRequest? providerRequest = null)
    {
        var activation = _providerContext.Activate(providerRequest);
        if (activation.IsError) return new OASISResult<ISTARODK> { IsError = true, Message = activation.Message };

        var existing = await _providerContext.CurrentProvider.LoadSTARODKAsync(id);
        if (existing.IsError || existing.Result == null) return existing;

        var odk = (STARODK)existing.Result;
        if (string.IsNullOrEmpty(odk.GeneratedCode))
            return new OASISResult<ISTARODK> { IsError = true, Message = "Dapp must be generated before deployment." };

        odk.DeploymentConfig = JsonSerializer.Serialize(new
        {
            DeployedAt = DateTime.UtcNow,
            Chain = odk.TargetChain,
            Holons = odk.BoundHolonIds,
            TxHash = $"0x{Guid.NewGuid():N}"
        });
        odk.ModifiedDate = DateTime.UtcNow;

        return await _providerContext.CurrentProvider.SaveSTARODKAsync(odk);
    }

    private static string GenerateDappCode(ISTARODK odk, STARDappGenerationRequest request)
    {
        var config = new
        {
            Name = odk.Name,
            Description = odk.Description,
            TargetChain = request.TargetChain,
            BoundHolons = request.BoundHolonIds,
            UserConfig = request.Config,
            GeneratedAt = DateTime.UtcNow
        };
        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }
}
