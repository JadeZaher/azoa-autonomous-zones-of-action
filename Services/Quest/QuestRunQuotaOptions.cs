using Microsoft.Extensions.Configuration;

namespace AZOA.WebAPI.Services.Quest;

/// <summary>
/// Config-bound per-(avatar, quest) run-start quota (marketplace treasury-drain guard).
/// See Managers/AGENTS.md §quest-run-quota. Read from the <c>Quest</c> config section.
/// </summary>
public sealed class QuestRunQuotaOptions
{
    /// <summary>Config section root for quest-level knobs.</summary>
    public const string SectionName = "Quest";

    /// <summary>
    /// Max concurrent/recent NON-terminal runs a NON-owner may hold for a single quest.
    /// A new marketplace run-start is rejected when the runner already holds this many
    /// non-terminal runs of that quest. <c>&lt;= 0</c> disables the quota (unbounded).
    /// </summary>
    public int MaxRunsPerAvatarPerQuest { get; set; } = 5;

    /// <summary>
    /// Multiplier applied to <see cref="MaxRunsPerAvatarPerQuest"/> for the OWNER running
    /// their OWN quest (owners are not draining a foreign treasury). <c>&lt;= 0</c> ⇒ owner is exempt (unbounded).
    /// </summary>
    public int OwnerLimitMultiplier { get; set; } = 4;

    /// <summary>Bind the options from configuration; falls back to defaults for any absent key.</summary>
    public static QuestRunQuotaOptions FromConfiguration(IConfiguration config)
    {
        var opts = new QuestRunQuotaOptions();
        var section = config.GetSection(SectionName);
        if (section.GetValue<int?>(nameof(MaxRunsPerAvatarPerQuest)) is { } max)
            opts.MaxRunsPerAvatarPerQuest = max;
        if (section.GetValue<int?>(nameof(OwnerLimitMultiplier)) is { } mult)
            opts.OwnerLimitMultiplier = mult;
        return opts;
    }

    /// <summary>The effective non-terminal-run ceiling for a run-start, or null when unbounded.</summary>
    public int? EffectiveLimit(bool isOwner)
    {
        if (!isOwner)
            return MaxRunsPerAvatarPerQuest > 0 ? MaxRunsPerAvatarPerQuest : (int?)null;

        // Owner path.
        if (OwnerLimitMultiplier <= 0 || MaxRunsPerAvatarPerQuest <= 0)
            return null; // exempt / base unbounded
        return MaxRunsPerAvatarPerQuest * OwnerLimitMultiplier;
    }
}
