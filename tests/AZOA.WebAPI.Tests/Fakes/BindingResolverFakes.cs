using AZOA.WebAPI.Services.Quest;

namespace AZOA.WebAPI.Tests.Fakes;

/// <summary>
/// Shared <see cref="QuestConfigBindingResolver"/> factory for tests that do not
/// exercise $from binding. Uses a fail-closed holon manager so any holon-path
/// binding would return an error (correct posture for unrelated tests).
/// </summary>
public static class BindingResolverFakes
{
    /// <summary>No-op resolver: passes through configs that have no $from bindings.</summary>
    public static QuestConfigBindingResolver PassThrough() =>
        new(HolonManagerMocks.Empty());
}
