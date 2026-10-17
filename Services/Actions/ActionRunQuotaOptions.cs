
namespace AZOA.WebAPI.Services.Actions;

/// <summary>
/// Config-bound per-(user, action) run-start quota (marketplace treasury-drain guard).
/// See Managers/AGENTS.md §action-run-quota. Read from the <c>ActionDefinition</c> config section.
/// </summary>
public sealed class ActionRunQuotaOptions
{
    /// <summary>Config section root for action-level knobs.</summary>
    public const string SectionName = "ActionDefinition";

    /// <summary>
    /// Max concurrent/recent NON-terminal runs a NON-owner may hold for a single action.
    /// A new marketplace run-start is rejected when the runner already holds this many
    /// non-terminal runs of that action. <c>&lt;= 0</c> disables the quota (unbounded).
    /// </summary>
    public int MaxRunsPerUserPerAction { get; set; } = 5;

    /// <summary>
    /// Multiplier applied to <see cref="MaxRunsPerUserPerAction"/> for the OWNER running
    /// their OWN action (owners are not draining a foreign treasury). <c>&lt;= 0</c> ⇒ owner is exempt (unbounded).
    /// </summary>
    public int OwnerLimitMultiplier { get; set; } = 4;

    /// <summary>Bind the options from configuration; falls back to defaults for any absent key.</summary>
    public static ActionRunQuotaOptions FromConfiguration(IConfiguration config)
    {
        var opts = new ActionRunQuotaOptions();
        var section = config.GetSection(SectionName);
        if (section.GetValue<int?>(nameof(MaxRunsPerUserPerAction)) is { } max)
            opts.MaxRunsPerUserPerAction = max;
        if (section.GetValue<int?>(nameof(OwnerLimitMultiplier)) is { } mult)
            opts.OwnerLimitMultiplier = mult;
        return opts;
    }

    /// <summary>The effective non-terminal-run ceiling for a run-start, or null when unbounded.</summary>
    public int? EffectiveLimit(bool isOwner)
    {
        if (!isOwner)
            return MaxRunsPerUserPerAction > 0 ? MaxRunsPerUserPerAction : (int?)null;

        // Owner path.
        if (OwnerLimitMultiplier <= 0 || MaxRunsPerUserPerAction <= 0)
            return null; // exempt / base unbounded
        return MaxRunsPerUserPerAction * OwnerLimitMultiplier;
    }
}
