using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Tests.Fakes;

/// <summary>
/// Shared <see cref="ActionConfigBindingResolver"/> factory for tests that do not
/// exercise $from binding. Uses a fail-closed holon manager so any holon-path
/// binding would return an error (correct posture for unrelated tests).
/// </summary>
public static class BindingResolverFakes
{
    /// <summary>No-op resolver: passes through configs that have no $from bindings.</summary>
    public static ActionConfigBindingResolver PassThrough() =>
        new(HolonManagerMocks.Empty());
}
