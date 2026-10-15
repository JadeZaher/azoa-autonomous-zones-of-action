// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Services.Hold;

/// <summary>
/// Operator configuration for the HOLD primitive and its hosted sweep. Bound from the
/// <c>Hold</c> section; see <c>Services/Hold/AGENTS.md</c> §sweep.
/// </summary>
/// <remarks>
/// The defaults are the ones a configured node wants: the sweep runs, and it sweeps the
/// chains the reserve is already configured for. What it will NOT do is guess a chain —
/// with no <c>Hold:SweepChains</c> and no <c>Reserve:Chains</c> the sweep has no scope
/// and idles, saying so once, rather than picking the platform default and releasing
/// value somewhere nobody asked it to.
/// </remarks>
public sealed class HoldOptions
{
    /// <summary>Configuration section this binds from.</summary>
    public const string SectionName = "Hold";

    /// <summary>Shortest interval the sweep is allowed to run at.</summary>
    public const int MinimumSweepIntervalSeconds = 15;

    /// <summary>Longest interval the sweep is allowed to run at (one hour).</summary>
    public const int MaximumSweepIntervalSeconds = 3600;

    /// <summary>Largest batch one sweep cycle will settle.</summary>
    public const int MaximumSweepBatchSize = 200;

    /// <summary>Whether the hosted sweep releases due holds at all.</summary>
    public bool SweepEnabled { get; set; } = true;

    /// <summary>How often the sweep looks for due holds; clamped to the bounds above.</summary>
    public int SweepIntervalSeconds { get; set; } = 60;

    /// <summary>How many holds one cycle will settle before waiting for the next.</summary>
    public int SweepBatchSize { get; set; } = 50;

    /// <summary>
    /// Chains the sweep looks at. Empty means "whatever <c>Reserve:Chains</c> names";
    /// empty on both sides means the sweep has no scope and does nothing.
    /// </summary>
    public List<string> SweepChains { get; set; } = new();
}
