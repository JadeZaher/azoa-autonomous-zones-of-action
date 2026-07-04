namespace AZOA.WebAPI.Services.Bridge;

/// <summary>Configuration for bridge safety gates. Bind from <see cref="SectionName"/>.</summary>
public sealed class BridgeOptions
{
    /// <summary>Configuration section: <c>"Blockchain:Bridge"</c>.</summary>
    public const string SectionName = "Blockchain:Bridge";

    /// <summary>
    /// When false, real-value bridge operations are refused for non-simulated chains.
    /// Simulated-chain routes remain allowed. Default false (safe).
    /// </summary>
    public bool RealValueEnabled { get; set; } = false;

    /// <summary>
    /// Seconds after which an in-progress idempotency claim may be taken over by a
    /// competing request. Used by Phase B claim-takeover logic; defined here now.
    /// Default 120.
    /// </summary>
    public int StaleClaimTakeoverSeconds { get; set; } = 120;
}
