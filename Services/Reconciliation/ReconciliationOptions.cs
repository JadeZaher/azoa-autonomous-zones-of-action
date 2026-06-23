namespace AZOA.WebAPI.Services.Reconciliation;

/// <summary>
/// Configuration for chain reconciliation. Bind from the
/// <see cref="SectionName"/> configuration section (Wave 3 wires this up):
/// <code>
/// builder.Services
///     .AddOptions&lt;ReconciliationOptions&gt;()
///     .Bind(builder.Configuration.GetSection(ReconciliationOptions.SectionName))
///     .ValidateDataAnnotations();
/// </code>
/// All thresholds are seconds so they are trivially expressed in
/// <c>appsettings.json</c>.
/// </summary>
public sealed class ReconciliationOptions
{
    /// <summary>Configuration section name: <c>"Reconciliation"</c>.</summary>
    public const string SectionName = "Reconciliation";

    /// <summary>
    /// Whether the background sweep is enabled. The scoped
    /// <see cref="AZOA.WebAPI.Interfaces.IReconciliationService"/> can still be
    /// invoked manually (e.g. from an ops endpoint) even when this is false.
    /// Default <c>true</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Seconds between background sweeps. Default 300 (5 minutes). Clamped to a
    /// minimum of 10s by the hosted service to avoid a hot loop on
    /// misconfiguration.
    /// </summary>
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Delay before the first sweep after host start, letting the app warm up.
    /// Default 30s.
    /// </summary>
    public int StartupDelaySeconds { get; set; } = 30;

    /// <summary>
    /// Max records of each kind (bridge / operations) examined per sweep.
    /// Bounds DB and RPC fan-out. Default 100; clamped to [1, 1000].
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// A bridge transaction is only considered for reconciliation once it has
    /// been non-terminal for at least this long (avoids racing healthy
    /// in-flight requests). Default 120s.
    /// </summary>
    public int BridgeStaleAfterSeconds { get; set; } = 120;

    /// <summary>
    /// If a bridge transaction is still non-terminal AND chain truth is not
    /// derivable past this age, it is flagged "MANUAL INTERVENTION REQUIRED"
    /// (logged + reported). Never auto-failed, never auto-reversed.
    /// Default 3600s (1 hour).
    /// </summary>
    public int BridgeHardStuckAfterSeconds { get; set; } = 3600;

    /// <summary>
    /// A blockchain operation stuck in Pending / AwaitingSignature is only
    /// considered once it is at least this old. Default 120s.
    /// </summary>
    public int OperationStaleAfterSeconds { get; set; } = 120;

    /// <summary>
    /// Hard-stuck threshold for blockchain operations (flag for manual
    /// intervention; never mutated past this with no chain truth).
    /// Default 3600s (1 hour).
    /// </summary>
    public int OperationHardStuckAfterSeconds { get; set; } = 3600;
}
