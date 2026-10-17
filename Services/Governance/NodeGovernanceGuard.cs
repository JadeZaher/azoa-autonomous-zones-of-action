using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Services.Governance;

public sealed class NodeGovernanceGuard : INodeGovernanceGuard
{
    private readonly NodeGovernanceOptions _options;
    private readonly INodeGovernanceStore? _store;
    private readonly ILogger<NodeGovernanceGuard>? _logger;

    public static INodeGovernanceGuard Unrestricted { get; } = new AllowAllNodeGovernanceGuard();

    public NodeGovernanceGuard(
        IOptions<NodeGovernanceOptions> options,
        INodeGovernanceStore? store = null,
        ILogger<NodeGovernanceGuard>? logger = null)
    {
        _options = options.Value;
        _store = store;
        _logger = logger;
    }

    public async Task<NodeGovernanceDecision> EnsureAllowedAsync(
        string? chainType,
        string? assetType,
        string action,
        CancellationToken ct = default)
    {
        var parameters = await ResolveParametersAsync(ct);
        if (parameters.IsError || parameters.Result is null)
            return Unavailable(parameters.Message, action);

        var chain = EnsureListed(parameters.Result.AllowedChains, "chain", chainType, action);
        if (chain.IsRefused) return chain;
        return EnsureListed(parameters.Result.AllowedAssetTypes, "asset type", assetType, action);
    }

    public async Task<NodeGovernanceDecision> EnsureChainAllowedAsync(
        string? chainType,
        string action,
        CancellationToken ct = default)
    {
        var parameters = await ResolveParametersAsync(ct);
        return parameters.IsError || parameters.Result is null
            ? Unavailable(parameters.Message, action)
            : EnsureListed(parameters.Result.AllowedChains, "chain", chainType, action);
    }

    public async Task<NodeGovernanceDecision> EnsureAssetTypeAllowedAsync(
        string? assetType,
        string action,
        CancellationToken ct = default)
    {
        var parameters = await ResolveParametersAsync(ct);
        return parameters.IsError || parameters.Result is null
            ? Unavailable(parameters.Message, action)
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

    private NodeGovernanceDecision EnsureListed(
        IReadOnlyList<string>? configuredList,
        string dimension,
        string? value,
        string action)
    {
        if (configuredList is null)
            return NodeGovernanceDecision.Allowed;

        var allowed = configuredList
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .ToArray();

        var requested = value?.Trim();
        if (string.IsNullOrWhiteSpace(requested))
        {
            return Deny(
                $"Node governance requires {ArticleFor(dimension)} {dimension} for '{action}' because " +
                $"{ArticleFor(dimension)} {dimension} allowlist is configured.",
                action);
        }

        if (allowed.Contains(requested, StringComparer.OrdinalIgnoreCase))
            return NodeGovernanceDecision.Allowed;

        return Deny(
            $"Node governance disallows {action} on {dimension} '{requested}'. " +
            $"Allowed {dimension}s: {Describe(allowed)}.",
            action);
    }

    private static string Describe(string[] allowed)
        => allowed.Length == 0 ? "<none>" : string.Join(", ", allowed);

    private static string ArticleFor(string phrase)
        => phrase.StartsWith("a", StringComparison.OrdinalIgnoreCase) ? "an" : "a";

    /// <summary>
    /// A POLICY refusal. The operator wording - which names the action, the dimension and the
    /// allowlist - is written to THIS log and then dropped: it is what an operator reading the
    /// log needs and what a member must never be shown. What the caller receives has already
    /// been through <see cref="ConsumerSafeErrors"/>, so no door can forget to scrub it.
    /// </summary>
    private NodeGovernanceDecision Deny(string operatorDetail, string action)
    {
        _logger?.LogWarning(
            "Node governance refused {Action} as {Code}: {OperatorDetail}",
            action, ConsumerSafeErrorCodes.NotPermitted, operatorDetail);

        return NodeGovernanceDecision.RefusePolicy(operatorDetail);
    }

    /// <summary>The governance parameters could not be read - a dependency failure, not a policy one.</summary>
    private NodeGovernanceDecision Unavailable(string operatorDetail, string action)
    {
        _logger?.LogWarning(
            "Node governance could not be read for {Action} as {Code}: {OperatorDetail}",
            action, ConsumerSafeErrorCodes.RailUnavailable, operatorDetail);

        return NodeGovernanceDecision.Unavailable(operatorDetail);
    }

    private sealed class AllowAllNodeGovernanceGuard : INodeGovernanceGuard
    {
        public Task<NodeGovernanceDecision> EnsureAllowedAsync(
            string? chainType,
            string? assetType,
            string action,
            CancellationToken ct = default)
            => Task.FromResult(NodeGovernanceDecision.Allowed);

        public Task<NodeGovernanceDecision> EnsureChainAllowedAsync(
            string? chainType,
            string action,
            CancellationToken ct = default)
            => Task.FromResult(NodeGovernanceDecision.Allowed);

        public Task<NodeGovernanceDecision> EnsureAssetTypeAllowedAsync(
            string? assetType,
            string action,
            CancellationToken ct = default)
            => Task.FromResult(NodeGovernanceDecision.Allowed);
    }

    private sealed record EffectiveParameters(
        IReadOnlyList<string>? AllowedChains,
        IReadOnlyList<string>? AllowedAssetTypes);
}
