// SPDX-License-Identifier: UNLICENSED
//
// OI-60. One call that turns the exchange's declared rails into registered ones.
//
// See Services/Reserve/AGENTS.md §buy-back.

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AZOA.WebAPI.Services.Reserve;

/// <summary>
/// DI wiring for the exchange's value rails - today, the buy-back backstop that lets the
/// platform reserve stand behind a project whose own funds are short.
/// </summary>
/// <remarks>
/// Registering the rail is NOT by itself an opening. <see cref="BuyBackOptions.Enabled"/> is
/// false unless an operator sets it and <see cref="BuyBackOptions.BackstopEnabled"/> only
/// decides whether the reserve may be asked at all; on top of both, the backstop can move
/// nothing until the operator has DECLARED
/// <see cref="BuyBackOptions.BackstopPartition"/> on the platform unit's reserve ledger, and
/// an undeclared partition fails closed inside the reserve itself. So what this call changes is
/// only that a declared budget can now actually be spent - never how much of one exists.
/// <para>
/// <c>Program.cs</c> owns the call site and invokes this as
/// <c>builder.Services.AddExchangeRail()</c>. Nothing else in this assembly calls it, so a node
/// that has not wired the rail keeps the previous behaviour exactly: no
/// <see cref="IBuyBackBackstop"/> in the container, and a policy that therefore describes no
/// shortfall to any seller as backed by the platform.
/// </para>
/// </remarks>
public static class ExchangeRailRegistration
{
    /// <summary>Registers the reserve-to-treasury buy-back backstop.</summary>
    /// <remarks>
    /// Scoped, because every collaborator it takes - the reserve, the project treasury and the
    /// idempotency ledger - is scoped, exactly as the buy-back queue and policy beside it are.
    /// <c>TryAdd</c> so a node that has substituted its own backstop keeps it.
    /// </remarks>
    public static IServiceCollection AddExchangeRail(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IBuyBackBackstop, ReserveToTreasuryBuyBackBackstop>();
        return services;
    }
}
