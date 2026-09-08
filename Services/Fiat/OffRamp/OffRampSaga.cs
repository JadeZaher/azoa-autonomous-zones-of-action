// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Sagas;

namespace AZOA.WebAPI.Services.Fiat.OffRamp;

/// <summary>
/// The cash-out saga: two forward steps and one compensation step, in the only order that is
/// survivable.
///
/// <para><see cref="CollectStepName"/> takes the units back to the reserve.
/// <see cref="PayStepName"/> sends the money. Between them is the only state where a person has
/// neither — which is exactly why it is a durable, named state with a compensation step behind
/// it rather than a gap inside one method.</para>
///
/// <para><see cref="ReturnStepName"/> is the honest floor: give the units back. When even that
/// cannot be done, the cash-out parks at <c>NeedsOperator</c> carrying the figure the node owes,
/// rather than dead-lettering a saga step nobody is looking at.</para>
/// </summary>
public static class OffRampSaga
{
    /// <summary>The registered saga name. One instance per cash-out.</summary>
    public const string Name = "fiat-off-ramp";

    /// <summary>Step 1: the units leave the person's account for the reserve.</summary>
    public const string CollectStepName = "collect-cash-out-units";

    /// <summary>Step 2: the money leaves the platform for the person's own bank.</summary>
    public const string PayStepName = "send-cash-out-money";

    /// <summary>Compensation: give the units back and close the cash-out.</summary>
    public const string ReturnStepName = "return-cash-out-units";

    /// <summary>The saga instance for one cash-out. The payout's record id IS the correlation key.</summary>
    public static string CorrelationKeyFor(string payoutId) => payoutId;
}

/// <summary>
/// What the collect step carries. The payout id is the only field the handler needs; the subject
/// travels with it so an operator reading a saga row can see whose money it is without a second
/// lookup.
/// </summary>
/// <param name="PayoutId">The <c>fiat_payout</c> record this instance settles.</param>
/// <param name="SubjectUserId">The person cashing out, as a compact Guid.</param>
public sealed record OffRampCollectPayload(string PayoutId, string SubjectUserId);

/// <summary>
/// The pay step's view of the same payload. A DISTINCT type so its handler is unambiguously
/// DI-resolvable, and a strict subset of <see cref="OffRampCollectPayload"/> so the payload the
/// processor flows forward unchanged deserializes into it cleanly.
/// </summary>
/// <param name="PayoutId">The cash-out being paid.</param>
public sealed record OffRampPayPayload(string PayoutId);

/// <summary>The compensation step's view. Distinct type, same subset rule as the pay payload.</summary>
/// <param name="PayoutId">The cash-out whose units are being given back.</param>
public sealed record OffRampReturnPayload(string PayoutId);

/// <summary>Builds the registered <see cref="SagaDefinition"/> for the cash-out saga.</summary>
public static class OffRampSagaDefinition
{
    /// <summary>
    /// The retry budget is deliberately slow. A rail that cannot be read now is usually readable
    /// in a minute, and a person's money is not made safer by hammering the provider.
    /// </summary>
    public static RetryPolicy PolicyFor(int maxAttempts) => new()
    {
        MaxAttempts = maxAttempts,
        BaseBackoff = TimeSpan.FromSeconds(30),
        MaxBackoff = TimeSpan.FromMinutes(10),
    };

    /// <summary>
    /// The definition, with the operator's attempt bound applied to both forward steps. Both
    /// declare the return step, so a cash-out can never quietly stop moving: it either pays, or
    /// the units go back, or it lands in front of a person.
    /// </summary>
    public static SagaDefinition Build(int maxAttempts)
    {
        var policy = PolicyFor(maxAttempts);

        return SagaDefinition.Create(OffRampSaga.Name)
            .AddForwardStep(new SagaStep<OffRampCollectPayload>(
                OffRampSaga.CollectStepName,
                retryPolicy: policy,
                compensationStepName: OffRampSaga.ReturnStepName))
            .AddForwardStep(new SagaStep<OffRampPayPayload>(
                OffRampSaga.PayStepName,
                retryPolicy: policy,
                compensationStepName: OffRampSaga.ReturnStepName))
            .AddCompensationStep(new SagaStep<OffRampReturnPayload>(
                OffRampSaga.ReturnStepName,
                retryPolicy: policy,
                compensationStepName: null))
            .Build();
    }
}

/// <summary>
/// Step 1. Fails the step ONLY when the outcome could not be established — a cash-out the node
/// has definitely parked or refused is a success that lets the saga stop cleanly.
/// </summary>
public sealed class OffRampCollectStepHandler : IStepHandler<OffRampCollectPayload>
{
    private readonly IOffRamp _offRamp;
    private readonly ILogger<OffRampCollectStepHandler> _logger;

    public OffRampCollectStepHandler(IOffRamp offRamp, ILogger<OffRampCollectStepHandler> logger)
    {
        _offRamp = offRamp ?? throw new ArgumentNullException(nameof(offRamp));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<StepResult> ExecuteAsync(
        StepExecutionContext<OffRampCollectPayload> ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.Payload is not { } payload || string.IsNullOrWhiteSpace(payload.PayoutId))
            return StepResult.Fail("The collect step carried no cash-out to settle.");

        var outcome = await _offRamp.CollectUnitsAsync(payload.PayoutId, ct);
        if (outcome.IsError)
            return StepResult.Fail(outcome.Message);

        var result = outcome.Result!;
        if (!result.Definite)
            return StepResult.Fail(result.IndeterminateReason ?? "The collection outcome is not established yet.");

        _logger.LogInformation(
            "Cash-out {PayoutId} collect pass finished: status={Status}.", result.Row.Id, result.Row.Status);

        return StepResult.Ok(result.Row.Status);
    }
}

/// <summary>Step 2. The money leaves.</summary>
public sealed class OffRampPayStepHandler : IStepHandler<OffRampPayPayload>
{
    private readonly IOffRamp _offRamp;
    private readonly ILogger<OffRampPayStepHandler> _logger;

    public OffRampPayStepHandler(IOffRamp offRamp, ILogger<OffRampPayStepHandler> logger)
    {
        _offRamp = offRamp ?? throw new ArgumentNullException(nameof(offRamp));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<StepResult> ExecuteAsync(
        StepExecutionContext<OffRampPayPayload> ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.Payload is not { } payload || string.IsNullOrWhiteSpace(payload.PayoutId))
            return StepResult.Fail("The payment step carried no cash-out to send.");

        var outcome = await _offRamp.SendMoneyAsync(payload.PayoutId, ct);
        if (outcome.IsError)
            return StepResult.Fail(outcome.Message);

        var result = outcome.Result!;
        if (!result.Definite)
            return StepResult.Fail(result.IndeterminateReason ?? "The payment outcome is not established yet.");

        _logger.LogInformation(
            "Cash-out {PayoutId} payment pass finished: status={Status}.", result.Row.Id, result.Row.Status);

        return StepResult.Ok(result.Row.Status);
    }
}

/// <summary>
/// Compensation. Gives the units back, and when it cannot, parks the cash-out carrying the figure
/// the node owes — never silently keeping units it paid nothing for.
/// </summary>
public sealed class OffRampReturnStepHandler : IStepHandler<OffRampReturnPayload>
{
    private readonly IOffRamp _offRamp;
    private readonly ILogger<OffRampReturnStepHandler> _logger;

    public OffRampReturnStepHandler(IOffRamp offRamp, ILogger<OffRampReturnStepHandler> logger)
    {
        _offRamp = offRamp ?? throw new ArgumentNullException(nameof(offRamp));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<StepResult> ExecuteAsync(
        StepExecutionContext<OffRampReturnPayload> ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.Payload is not { } payload || string.IsNullOrWhiteSpace(payload.PayoutId))
            return StepResult.Fail("The compensation step carried no cash-out to unwind.");

        var outcome = await _offRamp.AbandonAsync(
            payload.PayoutId, "The cash-out could not be completed.", ct);

        if (outcome.IsError)
        {
            // Even the unwind could not be established. Park it rather than let a dead-lettered
            // step be the only trace of somebody's missing units.
            var parked = await _offRamp.ParkForOperatorAsync(payload.PayoutId, outcome.Message, ct);
            return parked.IsError
                ? StepResult.Fail(outcome.Message)
                : StepResult.Ok(parked.Result!.Status);
        }

        var result = outcome.Result!;
        if (!result.Definite)
        {
            var parked = await _offRamp.ParkForOperatorAsync(
                payload.PayoutId,
                result.IndeterminateReason ?? OffRampService.AmbiguousMoveReason,
                ct);
            return parked.IsError
                ? StepResult.Fail(result.IndeterminateReason ?? "The unwind outcome is not established yet.")
                : StepResult.Ok(parked.Result!.Status);
        }

        _logger.LogWarning(
            "Cash-out {PayoutId} was unwound: status={Status}, reason={Reason}.",
            result.Row.Id, result.Row.Status, result.Row.StatusReason ?? "none recorded");

        return StepResult.Ok(result.Row.Status);
    }
}
