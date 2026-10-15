// SPDX-License-Identifier: UNLICENSED
//
// OI-28, the reader half. ValueRailCapabilityGate.IsAccountPauseEnforced was built as a positive
// signal for exactly this situation and then had ZERO production readers — its only readers were
// two assertions in its own test file, which is a control whose only reader is its own test.
//
// The hole it was built for is real: the reversal ledger is a TRAILING OPTIONAL constructor
// parameter on both dispatch seams (it has to be, because dozens of positional fixtures construct
// them), so a container composed without AddFiatReversalPipeline silently disables the account
// pause at BOTH seams and every value node passes. This guard is what makes that loud.

using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Actions.Workflow;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Actions;

public sealed class ValueRailStartupTests
{
    [Fact]
    public void Both_real_dispatch_seams_report_whether_the_account_pause_is_enforced()
    {
        // The guard can only ask a seam that answers. If either seam stops implementing this, the
        // guard silently degrades into "the type is registered", which is the claim OI-29 already
        // proved is not the same as a capability.
        typeof(ActionManager).Should().BeAssignableTo<IValueNodePauseSeam>(
            "the in-process executor is half of the pause");
        typeof(ActionNodeStepHandler).Should().BeAssignableTo<IValueNodePauseSeam>(
            "the durable saga handler is the other half, and a pause honoured by half the engine "
            + "is not honoured");
    }

    [Fact]
    public void A_container_whose_in_process_seam_lost_the_reversal_ledger_refuses_to_boot()
    {
        var services = Provider(inProcessEnforced: false, durableEnforced: true);

        var act = () => ValueRailStartup.GuardAccountPauseWired(services);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*in-process action executor*WITHOUT the reversal ledger*");
    }

    [Fact]
    public void A_container_whose_durable_seam_lost_the_reversal_ledger_refuses_to_boot()
    {
        // The one a per-seam test would miss. The durable path is the one a workflow actually
        // runs on, and it takes the ledger through its OWN optional parameter.
        var services = Provider(inProcessEnforced: true, durableEnforced: false);

        var act = () => ValueRailStartup.GuardAccountPauseWired(services);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*durable action step handler*WITHOUT the reversal ledger*");
    }

    [Fact]
    public void A_container_with_the_pause_wired_at_both_seams_boots()
    {
        var services = Provider(inProcessEnforced: true, durableEnforced: true);

        var act = () => ValueRailStartup.GuardAccountPauseWired(services);

        act.Should().NotThrow();
    }

    [Fact]
    public void A_seam_that_is_not_registered_at_all_refuses_to_boot()
    {
        var services = new ServiceCollection().BuildServiceProvider();

        var act = () => ValueRailStartup.GuardAccountPauseWired(services);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*is not registered*");
    }

    /// <summary>
    /// Seams registered SCOPED, exactly as Program.cs registers them, so this also fails if the
    /// guard stops creating its own scope and resolves them off the root.
    /// </summary>
    private static ServiceProvider Provider(bool inProcessEnforced, bool durableEnforced)
    {
        var manager = new Mock<IActionManager>();
        manager.As<IValueNodePauseSeam>()
            .SetupGet(seam => seam.IsAccountPauseEnforced).Returns(inProcessEnforced);

        var handler = new Mock<IStepHandler<ActionStepPayload>>();
        handler.As<IValueNodePauseSeam>()
            .SetupGet(seam => seam.IsAccountPauseEnforced).Returns(durableEnforced);

        var services = new ServiceCollection();
        services.AddScoped(_ => manager.Object);
        services.AddScoped(_ => handler.Object);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
