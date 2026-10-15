// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Services.Fiat.Reversal;

namespace AZOA.WebAPI.Sagas;

/// <summary>
/// The D10 compensation saga: the durable, crash-safe driver of what happens after money that
/// this node already turned into units is taken back.
///
/// <para>Two forward steps and one compensation step, and the split between them is the whole
/// design. <see cref="RecoverStepName"/> takes back what it can and NEVER absorbs;
/// <see cref="SettleStepName"/> closes the reversal by turning whatever is left into a recorded
/// platform loss and a flagged account. Keeping them apart is what makes the retry budget mean
/// "the node could not establish an outcome" rather than "the person had nothing left to take" —
/// a definite "nothing is recoverable" advances immediately instead of burning five attempts to
/// reach a conclusion it already had.</para>
///
/// <para><see cref="EscalateStepName"/> is the honest floor. When even absorbing the loss cannot
/// be written down, the reversal is parked at <c>NeedsOperator</c> with the reason on the row and
/// the account flagged, rather than dead-lettering a saga step nobody is looking at.</para>
/// </summary>
public static class FiatCompensationSaga
{
    /// <summary>The registered saga name. One instance per reversal.</summary>
    public const string Name = "fiat-compensation";

    /// <summary>Rungs 1 and 2: the units themselves, then what they bought.</summary>
    public const string RecoverStepName = "recover-reversed-units";

    /// <summary>Rung 3: absorb whatever is left and flag the account.</summary>
    public const string SettleStepName = "settle-reversal-shortfall";

    /// <summary>The floor: park the reversal for a person, with the reason on the row.</summary>
    public const string EscalateStepName = "escalate-unsettled-reversal";

    /// <summary>The saga instance for one reversal. The reversal's record id IS the correlation key.</summary>
    public static string CorrelationKeyFor(string reversalId) => reversalId;
}

/// <summary>
/// What one compensation saga instance carries. The reversal id is the only field the handlers
/// need; the charge and lot travel with it so an operator reading a saga row can see which money
/// it is about without a second lookup.
/// </summary>
/// <param name="ReversalId">The <c>fiat_reversal</c> record this instance unwinds.</param>
/// <param name="ChargeId">The provider charge the money was taken back from.</param>
/// <param name="LotId">The issuance being unwound, when one was correlated.</param>
public sealed record FiatCompensationPayload(string ReversalId, string ChargeId, string? LotId);

/// <summary>
/// The settlement step's view of the same payload. A DISTINCT type so its handler is
/// unambiguously DI-resolvable, and a strict subset of <see cref="FiatCompensationPayload"/> so
/// the payload the processor flows forward unchanged deserializes into it cleanly.
/// </summary>
/// <param name="ReversalId">The reversal being closed.</param>
public sealed record FiatSettlementPayload(string ReversalId);

/// <summary>
/// The compensation step's view of the same payload. Distinct type, same subset rule as
/// <see cref="FiatSettlementPayload"/>.
/// </summary>
/// <param name="ReversalId">The reversal being parked for a person.</param>
public sealed record FiatEscalationPayload(string ReversalId);

/// <summary>Builds the registered <see cref="SagaDefinition"/> for the compensation saga.</summary>
public static class FiatCompensationSagaDefinition
{
    /// <summary>
    /// The retry budget is deliberately slow: a rail that cannot be read now is usually readable
    /// in a minute, and hammering it while somebody's money is in dispute helps nobody.
    /// </summary>
    public static RetryPolicy PolicyFor(int maxAttempts) => new()
    {
        MaxAttempts = maxAttempts,
        BaseBackoff = TimeSpan.FromSeconds(30),
        MaxBackoff = TimeSpan.FromMinutes(10),
    };

    /// <summary>
    /// The definition, with the operator's attempt bound applied to both forward steps. Both
    /// declare the escalation step, so a reversal can never quietly stop moving: it either
    /// settles or it lands in front of a person.
    /// </summary>
    public static SagaDefinition Build(int maxAttempts)
    {
        var policy = PolicyFor(maxAttempts);

        return SagaDefinition.Create(FiatCompensationSaga.Name)
            .AddForwardStep(new SagaStep<FiatCompensationPayload>(
                FiatCompensationSaga.RecoverStepName,
                retryPolicy: policy,
                compensationStepName: FiatCompensationSaga.EscalateStepName))
            .AddForwardStep(new SagaStep<FiatSettlementPayload>(
                FiatCompensationSaga.SettleStepName,
                retryPolicy: policy,
                compensationStepName: FiatCompensationSaga.EscalateStepName))
            .AddCompensationStep(new SagaStep<FiatEscalationPayload>(
                FiatCompensationSaga.EscalateStepName,
                retryPolicy: policy,
                compensationStepName: null))
            .Build();
    }
}

/// <summary>
/// Rungs 1 and 2. Fails the step ONLY when the outcome could not be established — a definite
/// "nothing more is recoverable" is a success that lets the saga advance to settlement.
/// </summary>
public sealed class FiatReversalRecoveryStepHandler : IStepHandler<FiatCompensationPayload>
{
    private readonly IReversalCompensation _compensation;
    private readonly ILogger<FiatReversalRecoveryStepHandler> _logger;

    public FiatReversalRecoveryStepHandler(
        IReversalCompensation compensation, ILogger<FiatReversalRecoveryStepHandler> logger)
    {
        _compensation = compensation ?? throw new ArgumentNullException(nameof(compensation));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<StepResult> ExecuteAsync(
        StepExecutionContext<FiatCompensationPayload> ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.Payload is not { } payload || string.IsNullOrWhiteSpace(payload.ReversalId))
            return StepResult.Fail("The compensation step carried no reversal to unwind.");

        var outcome = await _compensation.RecoverAsync(payload.ReversalId, ct);
        if (outcome.IsError)
            return StepResult.Fail(outcome.Message);

        var result = outcome.Result!;
        if (!result.Definite)
            return StepResult.Fail(result.IndeterminateReason ?? "The recovery outcome is not established yet.");

        _logger.LogInformation(
            "Reversal {ReversalId} recovery pass finished: status={Status}, recovered={Recovered} of "
            + "{Target} base units via {Path}.",
            result.Row.Id, result.Row.Status, result.Row.RecoveredQuantityBaseUnits,
            result.Row.TargetQuantityBaseUnits, result.Row.RecoveryPath);

        return StepResult.Ok(result.Row.Status);
    }
}

/// <summary>Rung 3. Closes the reversal: absorb what is left, and flag the account when policy says so.</summary>
public sealed class FiatReversalSettlementStepHandler : IStepHandler<FiatSettlementPayload>
{
    private readonly IReversalCompensation _compensation;
    private readonly ILogger<FiatReversalSettlementStepHandler> _logger;

    public FiatReversalSettlementStepHandler(
        IReversalCompensation compensation, ILogger<FiatReversalSettlementStepHandler> logger)
    {
        _compensation = compensation ?? throw new ArgumentNullException(nameof(compensation));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<StepResult> ExecuteAsync(
        StepExecutionContext<FiatSettlementPayload> ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.Payload is not { } payload || string.IsNullOrWhiteSpace(payload.ReversalId))
            return StepResult.Fail("The settlement step carried no reversal to close.");

        var outcome = await _compensation.SettleShortfallAsync(payload.ReversalId, ct);
        if (outcome.IsError)
            return StepResult.Fail(outcome.Message);

        var result = outcome.Result!;
        if (!result.Definite)
            return StepResult.Fail(result.IndeterminateReason ?? "The reversal could not be closed yet.");

        _logger.LogInformation(
            "Reversal {ReversalId} settled at {Status}; absorbed {Absorbed} base units, account "
            + "flagged={Flagged}.",
            result.Row.Id, result.Row.Status, result.Row.AbsorbedQuantityBaseUnits,
            result.Row.AccountFlagged);

        return StepResult.Ok(result.Row.Status);
    }
}

/// <summary>
/// The floor. Parks the reversal where a person will see it, with the reason on the row and the
/// account flagged, rather than letting a dead-lettered saga step be the only trace.
/// </summary>
public sealed class FiatReversalEscalationStepHandler : IStepHandler<FiatEscalationPayload>
{
    /// <summary>The reason stamped on a reversal the saga could not settle on its own.</summary>
    public const string ExhaustedReason =
        "The automatic unwind could not finish. Confirm what was recovered against the rail "
        + "before deciding what the account still owes.";

    private readonly IReversalCompensation _compensation;
    private readonly ILogger<FiatReversalEscalationStepHandler> _logger;

    public FiatReversalEscalationStepHandler(
        IReversalCompensation compensation, ILogger<FiatReversalEscalationStepHandler> logger)
    {
        _compensation = compensation ?? throw new ArgumentNullException(nameof(compensation));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<StepResult> ExecuteAsync(
        StepExecutionContext<FiatEscalationPayload> ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.Payload is not { } payload || string.IsNullOrWhiteSpace(payload.ReversalId))
            return StepResult.Fail("The escalation step carried no reversal to park.");

        var parked = await _compensation.ParkForOperatorAsync(payload.ReversalId, ExhaustedReason, ct);
        if (parked.IsError)
            return StepResult.Fail(parked.Message);

        _logger.LogError(
            "Reversal {ReversalId} was parked for an operator after the compensation saga exhausted "
            + "its attempts.",
            payload.ReversalId);

        return StepResult.Ok(parked.Result!.Status);
    }
}
