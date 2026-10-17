// SPDX-License-Identifier: UNLICENSED

using System.Numerics;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Fiat.OffRamp;

/// <summary>D-8 tenant-funded payouts and the cross-scope exit cap; see Services/Fiat/OffRamp/AGENTS.md §tenant-funded.</summary>
public sealed partial class OffRampService
{
    /// <summary>Typed refusal code: the pricing tenant's payout balance does not cover this cash-out.</summary>
    public const string TenantPayoutBalanceInsufficientCode = "TENANT_PAYOUT_BALANCE_INSUFFICIENT";

    /// <summary>Message paired with <see cref="TenantPayoutBalanceInsufficientCode"/>; nothing moved.</summary>
    public const string TenantPayoutBalanceInsufficientMessage =
        "This cash-out is priced by an organisation whose payout balance does not cover it right now. Nothing was sent.";

    /// <summary>The reserve partition a tenant-funded collect returns units to (owner decision b).</summary>
    public static string TenantPartitionFor(Guid tenantId) => $"tenant-payout:{tenantId:N}";

    /// <summary>Applies whatever debit transition the row's evidence supports; a no-op for node-funded rows.</summary>
    private async Task SyncFundingAsync(FiatPayout row, CancellationToken ct)
    {
        if (row.FundingTenantId is null)
            return;
        // A Retry here is not lost: the funding sweep re-syncs stale Reserved debits from the row.
        await _funding.SyncAsync(row, ct);
    }

    private async Task<AZOAResult<OffRampStepOutcome>> SyncedAsync(AZOAResult<OffRampStepOutcome> outcome, CancellationToken ct)
    {
        if (!outcome.IsError && outcome.Result is { } step)
            await SyncFundingAsync(step.Row, ct);
        return outcome;
    }

    /// <summary>
    /// Critic rule 5: the funding party is re-derived from <c>row.PriceScheduleId</c>. A tenant-priced row
    /// must name that tenant and hold its Reserved debit; a node-priced row must name no tenant. Null = send.
    /// </summary>
    private async Task<string?> RefuseUnfundedSendAsync(FiatPayout row, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(row.PriceScheduleId))
            return "This cash-out names no price, so who funds it cannot be established. Reconcile before sending.";
        var schedule = await _schedules.GetVersionAsync(row.PriceScheduleId, ct);
        if (schedule.IsError || schedule.Result is not { } priced)
            return "The price this cash-out was quoted at could not be read, so who funds it cannot be established.";

        var funder = SurrealRecordGuid.ParseOptional(row.FundingTenantId);
        if (PriceScheduleScope.IsNode(priced.ScopeKey))
            return funder is null ? null : "This cash-out names a funding organisation but was priced by the node.";

        if (priced.TenantId is not { } pricingTenant || funder != pricingTenant)
            return "This cash-out was priced by an organisation that is not recorded as funding it.";
        return await _funding.HoldsReservedDebitAsync(row, pricingTenant, ct)
            ? null
            : "No reserved payout balance funds this cash-out. Reconcile before sending.";
    }

    /// <summary>
    /// Owner decision (c): a node-funded cash-out may not pay more per unit than the tenant price the
    /// consumed lots were sold at. Null when the cap holds; the refusal otherwise.
    /// </summary>
    private async Task<string?> ExceedsSoldPriceAsync(
        Guid subjectUserId, BigInteger quantity, OffRampContext context, CancellationToken ct)
    {
        var lots = await _lots.ListIssuedForUserAsync(subjectUserId, ct);
        if (lots.IsError)
            return "Your available balance could not be read. Nothing was sent.";
        var matching = lots.Result!.Where(lot => lot.Environment == _fiat.ActiveEnvironment.Name
            && lot.ChainNetwork == _providerFactory.DefaultNetwork.ToString()).ToList();
        var plan = OffRampAging.PlanConsumption(matching, quantity, _clock.GetUtcNow());
        foreach (var draw in plan.Draws)
        {
            var lot = matching.FirstOrDefault(candidate => candidate.Id == draw.LotId);
            if (lot?.PricingScopeKey is not { } scope || PriceScheduleScope.IsNode(scope))
                continue;
            // A lower units-per-minor-unit rate pays more money per unit than the lot was sold for.
            if (!PriceScheduleMath.TryParseRate(lot.UnitsPerMinorUnit, out var soldRate, out _)
                || context.UnitsPerMinorUnit < soldRate)
                return "Some of these units were bought at an organisation's price and cannot be cashed out above it here.";
        }

        return null;
    }
}
