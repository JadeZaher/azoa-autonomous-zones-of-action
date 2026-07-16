using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Services.Governance;

public sealed class NodeGovernanceGuard : INodeGovernanceGuard
{
    private readonly NodeGovernanceOptions _options;
    private readonly INodeGovernanceStore? _store;

    public static INodeGovernanceGuard Unrestricted { get; } = new AllowAllNodeGovernanceGuard();

    public NodeGovernanceGuard(
        IOptions<NodeGovernanceOptions> options,
        INodeGovernanceStore? store = null)
    {
        _options = options.Value;
        _store = store;
    }

    public async Task<AZOAResult<bool>> EnsureAllowedAsync(
        string? chainType,
        string? assetType,
        string action,
        CancellationToken ct = default)
    {
        var parameters = await ResolveParametersAsync(ct);
        if (parameters.IsError || parameters.Result is null)
            return Deny(parameters.Message);

        var chain = EnsureListed(parameters.Result.AllowedChains, "chain", chainType, action);
        if (chain.IsError) return chain;
        return EnsureListed(parameters.Result.AllowedAssetTypes, "asset type", assetType, action);
    }

    public async Task<AZOAResult<bool>> EnsureChainAllowedAsync(
        string? chainType,
        string action,
        CancellationToken ct = default)
    {
        var parameters = await ResolveParametersAsync(ct);
        return parameters.IsError || parameters.Result is null
            ? Deny(parameters.Message)
            : EnsureListed(parameters.Result.AllowedChains, "chain", chainType, action);
    }

    public async Task<AZOAResult<bool>> EnsureAssetTypeAllowedAsync(
        string? assetType,
        string action,
        CancellationToken ct = default)
    {
        var parameters = await ResolveParametersAsync(ct);
        return parameters.IsError || parameters.Result is null
            ? Deny(parameters.Message)
            : EnsureListed(parameters.Result.AllowedAssetTypes, "asset type", assetType, action);
    }

    private async Task<AZOAResult<EffectiveParameters>> ResolveParametersAsync(CancellationToken ct)
    {
        if (_store is not null)
        {
            var stored = await _store.GetParametersAsync(ct);
            if (stored.IsError)
            {
                return new AZOAResult<EffectiveParameters>
                {
                    IsError = true,
                    Message = $"Node governance parameters unavailable: {stored.Message}",
                };
            }

            if (stored.Result is not null)
            {
                return new AZOAResult<EffectiveParameters>
                {
                    Result = new EffectiveParameters(stored.Result.AllowedChains, stored.Result.AllowedAssetTypes),
                };
            }
        }

        return new AZOAResult<EffectiveParameters>
        {
            Result = new EffectiveParameters(_options.AllowedChains, _options.AllowedAssetTypes),
        };
    }

    private static AZOAResult<bool> EnsureListed(
        IReadOnlyList<string>? configuredList,
        string dimension,
        string? value,
        string action)
    {
        if (configuredList is null)
            return Allow();

        var allowed = configuredList
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .ToArray();

        var requested = value?.Trim();
        if (string.IsNullOrWhiteSpace(requested))
        {
            return Deny(
                $"Node governance requires {ArticleFor(dimension)} {dimension} for '{action}' because " +
                $"{ArticleFor(dimension)} {dimension} allowlist is configured.");
        }

        if (allowed.Contains(requested, StringComparer.OrdinalIgnoreCase))
            return Allow();

        return Deny(
            $"Node governance disallows {action} on {dimension} '{requested}'. " +
            $"Allowed {dimension}s: {Describe(allowed)}.");
    }

    private static string Describe(string[] allowed)
        => allowed.Length == 0 ? "<none>" : string.Join(", ", allowed);

    private static string ArticleFor(string phrase)
        => phrase.StartsWith("a", StringComparison.OrdinalIgnoreCase) ? "an" : "a";

    private static AZOAResult<bool> Allow()
        => new() { Result = true };

    private static AZOAResult<bool> Deny(string message)
        => new() { IsError = true, Result = false, Message = message };

    private sealed class AllowAllNodeGovernanceGuard : INodeGovernanceGuard
    {
        public Task<AZOAResult<bool>> EnsureAllowedAsync(
            string? chainType,
            string? assetType,
            string action,
            CancellationToken ct = default)
            => Task.FromResult(Allow());

        public Task<AZOAResult<bool>> EnsureChainAllowedAsync(
            string? chainType,
            string action,
            CancellationToken ct = default)
            => Task.FromResult(Allow());

        public Task<AZOAResult<bool>> EnsureAssetTypeAllowedAsync(
            string? assetType,
            string action,
            CancellationToken ct = default)
            => Task.FromResult(Allow());
    }

    private sealed record EffectiveParameters(
        IReadOnlyList<string>? AllowedChains,
        IReadOnlyList<string>? AllowedAssetTypes);
}
