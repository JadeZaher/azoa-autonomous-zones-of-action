using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces;

public interface INodeGovernanceGuard
{
    Task<AZOAResult<bool>> EnsureAllowedAsync(
        string? chainType,
        string? assetType,
        string action,
        CancellationToken ct = default);

    Task<AZOAResult<bool>> EnsureChainAllowedAsync(
        string? chainType,
        string action,
        CancellationToken ct = default);

    Task<AZOAResult<bool>> EnsureAssetTypeAllowedAsync(
        string? assetType,
        string action,
        CancellationToken ct = default);
}
