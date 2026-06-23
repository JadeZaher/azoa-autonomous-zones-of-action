using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Managers;

public class STARManager : ISTARManager
{
    private readonly ISTARStore _starStore;

    public STARManager(ISTARStore starStore)
    {
        _starStore = starStore;
    }

    public async Task<AZOAResult<ISTARODK>> GetAsync(Guid id, AZOARequest? request = null)
    {
        return await _starStore.GetByIdAsync(id, default);
    }

    public async Task<AZOAResult<IEnumerable<ISTARODK>>> GetAllAsync(AZOARequest? request = null)
    {
        return await _starStore.GetAllAsync(default);
    }

    public async Task<AZOAResult<ISTARODK>> CreateOrUpdateAsync(
        STARODKCreateModel model,
        Guid avatarId,
        Guid? routeId = null,
        AZOARequest? request = null)
    {
        // IDOR-safe upsert:
        //   - PUT (routeId != null): load by id, then require IsOwnedBy(record, avatarId)
        //   - POST (routeId == null): load by (name, avatarId) — name collisions
        //     across avatars never overwrite each other.
        // The caller-supplied model.AvatarId is intentionally ignored — the
        // authenticated avatar id from the controller is the only source of truth.

        STARODK odk;
        if (routeId.HasValue)
        {
            var loaded = await _starStore.GetByIdAsync(routeId.Value, default);
            if (loaded.IsError || loaded.Result == null)
                return Fail(STARODKAuthorizationError.NotFound + "STAR ODK not found.");

            if (!IsOwnedBy(loaded.Result, avatarId))
                return Fail(STARODKAuthorizationError.Forbidden + "STAR ODK is owned by a different avatar.");

            odk = (STARODK)loaded.Result;
        }
        else
        {
            var match = await _starStore.GetByNameAndAvatarAsync(model.Name, avatarId, default);
            odk = (match.Result as STARODK) ?? new STARODK { AvatarId = avatarId };
        }

        odk.Name         = model.Name;
        odk.Description  = model.Description;
        odk.PublicKey    = model.PublicKey;
        odk.AvatarId     = avatarId; // authoritative — never trust model.AvatarId
        odk.ModifiedDate = DateTime.UtcNow;

        return await _starStore.UpsertAsync(odk, default);
    }

    private static bool IsOwnedBy(ISTARODK record, Guid avatarId) =>
        record.AvatarId.HasValue && record.AvatarId.Value == avatarId;

    private static AZOAResult<ISTARODK> Fail(string message) =>
        new() { IsError = true, Message = message };

    public async Task<AZOAResult<bool>> DeleteAsync(Guid id, Guid? avatarId = null, AZOARequest? request = null)
    {
        if (avatarId.HasValue)
        {
            var loaded = await _starStore.GetByIdAsync(id, default);
            if (loaded.IsError || loaded.Result == null)
                return new AZOAResult<bool> { IsError = true, Message = "STAR ODK not found." };
            if (!IsOwnedBy(loaded.Result, avatarId.Value))
                return new AZOAResult<bool> { IsError = true, Message = "STAR ODK is owned by a different avatar." };
        }

        return await _starStore.DeleteAsync(id, default);
    }

    public async Task<AZOAResult<ISTARODK>> GenerateAsync(Guid id, STARDappGenerationRequest request, Guid? avatarId = null, AZOARequest? providerRequest = null)
    {
        var existing = await _starStore.GetByIdAsync(id, default);
        if (existing.IsError || existing.Result == null) return existing;
        if (avatarId.HasValue && !IsOwnedBy(existing.Result, avatarId.Value))
            return Fail(STARODKAuthorizationError.Forbidden + "STAR ODK is owned by a different avatar.");

        var odk = (STARODK)existing.Result;
        odk.TargetChain = request.TargetChain;
        odk.BoundHolonIds = request.BoundHolonIds;
        odk.GeneratedCode = GenerateDappCode(odk, request);
        odk.ModifiedDate = DateTime.UtcNow;

        return await _starStore.UpsertAsync(odk, default);
    }

    public async Task<AZOAResult<ISTARODK>> DeployAsync(Guid id, Guid? avatarId = null, AZOARequest? providerRequest = null)
    {
        var existing = await _starStore.GetByIdAsync(id, default);
        if (existing.IsError || existing.Result == null) return existing;
        if (avatarId.HasValue && !IsOwnedBy(existing.Result, avatarId.Value))
            return Fail(STARODKAuthorizationError.Forbidden + "STAR ODK is owned by a different avatar.");

        var odk = (STARODK)existing.Result;
        if (string.IsNullOrEmpty(odk.GeneratedCode))
            return new AZOAResult<ISTARODK> { IsError = true, Message = "Dapp must be generated before deployment." };

        odk.DeploymentConfig = JsonSerializer.Serialize(new
        {
            DeployedAt = DateTime.UtcNow,
            Chain = odk.TargetChain,
            Holons = odk.BoundHolonIds,
            TxHash = $"0x{Guid.NewGuid():N}"
        });
        odk.ModifiedDate = DateTime.UtcNow;

        return await _starStore.UpsertAsync(odk, default);
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
