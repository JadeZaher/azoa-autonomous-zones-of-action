using AZOA.WebAPI.Services.Governance;

namespace AZOA.WebAPI.Interfaces;

/// <summary>
/// What this node will run. Every method answers with a <see cref="NodeGovernanceDecision"/>,
/// whose only sentence is consumer-safe by construction - see that type for why the scrub lives
/// here at the counter rather than at each of the six doors that ask.
/// </summary>
public interface INodeGovernanceGuard
{
    Task<NodeGovernanceDecision> EnsureAllowedAsync(
        string? chainType,
        string? assetType,
        string action,
        CancellationToken ct = default);

    Task<NodeGovernanceDecision> EnsureChainAllowedAsync(
        string? chainType,
        string action,
        CancellationToken ct = default);

    Task<NodeGovernanceDecision> EnsureAssetTypeAllowedAsync(
        string? assetType,
        string action,
        CancellationToken ct = default);
}
