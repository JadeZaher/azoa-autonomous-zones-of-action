using System.Collections.Concurrent;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;

namespace OASIS.WebAPI.Core.ProviderSelection;

/// <summary>
/// Sticky session affinity — same avatar/session always routes to the same provider.
/// Falls back to HealthScore on first encounter or when sticky provider is unhealthy.
/// </summary>
public class StickySessionStrategy : IProviderSelectionStrategy
{
    public string StrategyName => "sticky-session";

    private readonly ConcurrentDictionary<string, string> _stickyMap = new();
    private readonly HealthScoreStrategy _fallback = new();

    /// <summary>
    /// Session key (e.g., AvatarId or JWT sub) to determine stickiness.
    /// Must be set before each selection call.
    /// </summary>
    public string? SessionKey { get; set; }

    public string? SelectProvider(IReadOnlyDictionary<string, ProviderHealthScore> candidates)
    {
        if (!string.IsNullOrEmpty(SessionKey) && _stickyMap.TryGetValue(SessionKey, out var stickyProvider))
        {
            // Check if sticky provider is still healthy
            if (candidates.TryGetValue(stickyProvider, out var score) && score.IsHealthy)
                return stickyProvider;

            // Sticky provider unhealthy — remove mapping, fall through
            _stickyMap.TryRemove(SessionKey, out _);
        }

        // First encounter or sticky failed — use fallback strategy
        var selected = _fallback.SelectProvider(candidates);

        if (!string.IsNullOrEmpty(SessionKey) && !string.IsNullOrEmpty(selected))
            _stickyMap[SessionKey] = selected;

        return selected;
    }

    public void ClearSession(string sessionKey)
    {
        _stickyMap.TryRemove(sessionKey, out _);
    }
}
