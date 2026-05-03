using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Core;

public class ProviderContext
{
    private readonly IEnumerable<IOASISStorageProvider> _providers;
    private readonly IConfiguration _config;
    private readonly IProviderHealthMonitor? _healthMonitor;
    private readonly IProviderSelectionStrategy? _customStrategy;

    public ProviderContext(
        IEnumerable<IOASISStorageProvider> providers,
        IConfiguration config,
        IProviderHealthMonitor? healthMonitor = null,
        IProviderSelectionStrategy? customStrategy = null)
    {
        _providers = providers;
        _config = config;
        _healthMonitor = healthMonitor;
        _customStrategy = customStrategy;
    }

    public IOASISStorageProvider CurrentProvider { get; private set; } = null!;
    public List<IOASISStorageProvider> AllActiveProviders { get; private set; } = new();

    public OASISResponse Activate(OASISRequest? request = null)
    {
        request ??= new OASISRequest();

        var defaultName = _config.GetValue<string>("OASIS:DefaultProvider") ?? "InMemory";
        var dynamicMode = _config.GetValue<DynamicProviderMode>("OASIS:DynamicProviderMode");

        IOASISStorageProvider? target = null;

        // ─── Explicit provider selection (highest priority) ───
        if (request.ProviderType != ProviderType.Default)
        {
            target = _providers.FirstOrDefault(p =>
                p.ProviderName.Equals(request.ProviderType.ToString(), StringComparison.OrdinalIgnoreCase));

            // If explicit provider not found, fall back to first available
            target ??= _providers.FirstOrDefault();
        }

        // ─── Dynamic selection (when no explicit choice and mode is on) ───
        if (target == null && _healthMonitor != null)
        {
            string? bestName = null;

            // Custom strategy takes priority over built-in mode
            if (_customStrategy != null)
            {
                bestName = _healthMonitor.SelectBestProvider(_customStrategy);
            }
            else if (dynamicMode != DynamicProviderMode.Off)
            {
                bestName = _healthMonitor.SelectBestProvider(dynamicMode);
            }

            if (!string.IsNullOrEmpty(bestName))
            {
                target = _providers.FirstOrDefault(p =>
                    p.ProviderName.Equals(bestName, StringComparison.OrdinalIgnoreCase));
            }
        }

        // ─── Config default fallback ───
        target ??= _providers.FirstOrDefault(p =>
            p.ProviderName.Equals(defaultName, StringComparison.OrdinalIgnoreCase));

        // ─── Any available fallback ───
        target ??= _providers.FirstOrDefault();

        if (target == null)
            return new OASISResponse { IsError = true, Message = "No storage provider available." };

        CurrentProvider = target;
        AllActiveProviders = new List<IOASISStorageProvider> { target };

        if (request.AutoFailOverMode == AutoFailOverMode.On)
        {
            var extras = _providers.Where(p => p != target);
            AllActiveProviders.AddRange(extras);
        }

        if (request.AutoReplicationMode != AutoReplicationMode.Off)
        {
            var extras = _providers.Where(p => p != target);
            AllActiveProviders.AddRange(extras);
            AllActiveProviders = AllActiveProviders.Distinct().ToList();
        }

        return new OASISResponse { Message = $"Activated provider: {target.ProviderName}" };
    }

    /// <summary>
    /// Record a successful operation on the current provider for health tracking.
    /// Call this after every successful provider operation when dynamic mode is active.
    /// </summary>
    public void RecordSuccess(double latencyMs)
    {
        _healthMonitor?.RecordSuccess(CurrentProvider.ProviderName, latencyMs);
    }

    /// <summary>
    /// Record a failed operation on the current provider for health tracking.
    /// </summary>
    public void RecordFailure()
    {
        _healthMonitor?.RecordFailure(CurrentProvider.ProviderName);
    }
}
