// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Numerics;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Reserve;

namespace AZOA.WebAPI.Services.Fiat.Reversal;

/// <summary>Operator resolution of an ambiguous recovery move; see Services/Fiat/Reversal/AGENTS.md §attempt-keys.</summary>
public sealed partial class ReversalCompensationService
{
    /// <summary>Resolution payload meaning "the chain proves the move never took effect".</summary>
    public const string ResolvedAbsentPayload = "absent";

    /// <summary>The idempotency key recording an operator's chain-evidence resolution of one move.</summary>
    public static string ResolutionKey(string moveKey) => moveKey + ":resolution";

    /// <summary>The ambiguity marker plus what the resolution needs to re-read the chain.</summary>
    private static string AmbiguousMoveError(string chainType, BigInteger amount, string? transactionId)
        => string.Create(CultureInfo.InvariantCulture,
            $"{AmbiguousRecoveryReason} chain={chainType} amount={ReversalMath.Format(amount)} tx={transactionId?.Trim()}");

    /// <inheritdoc />
    public async Task<AZOAResult<FiatReversal>> ResolveParkedAsync(
        string reversalId, Guid actor, string reason, CancellationToken ct = default)
    {
        if (actor == Guid.Empty || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > MaxReasonLength)
            return AZOAResult<FiatReversal>.FailureWithCode(
                "An operator and a reason of 1–512 characters are required.", AzoaErrorCodes.InvalidRequest);

        var read = await _reversals.GetAsync(reversalId, ct);
        if (read.IsError)
            return AZOAResult<FiatReversal>.Failure(read.Message);
        if (read.Result is not { } row)
            return AZOAResult<FiatReversal>.FailureWithCode($"Reversal '{reversalId}' was not found.", AzoaErrorCodes.NotFound);
        if (!FiatReversalStatuses.NeedsPerson(row.Status))
            return Conflict("Only a reversal waiting on a person has an operator resolution.");
        if (string.IsNullOrWhiteSpace(row.PendingMoveKey)
            || !row.PendingMoveKey.StartsWith($"{RecoveryOperationType}:{row.Id}:", StringComparison.Ordinal))
            return Conflict("This reversal was not parked on a recorded recovery move; there is no chain evidence to resolve.");

        var moveKey = row.PendingMoveKey;
        var record = await _idempotency.GetAsync(moveKey, ct);
        var error = record?.Error ?? string.Empty;
        if (record is not { State: IdempotencyState.Failed } || !error.Contains(AmbiguousRecoveryReason, StringComparison.Ordinal))
            return Conflict("The parked move is no longer ambiguous; run the unwind instead.");
        if (!TryReadAmbiguousMove(error, out var chainType, out var amount, out var transactionId))
            return Conflict("The parked move recorded no transaction to re-read; reconcile it on chain by hand.");

        ChainConfirmation verdict;
        try
        {
            var provider = _providerFactory.GetProvider(chainType, _providerFactory.DefaultNetwork);
            var confirmation = await provider.GetTransactionConfirmationAsync(transactionId, ct);
            if (confirmation.IsError)
                return Conflict("The chain could not be read. Nothing was changed.");
            verdict = confirmation.Result;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // The provider factory refuses an unknown chain; that is an unresolvable record, not a fault.
            return Conflict($"The '{chainType}' chain cannot be read on this node.");
        }

        var payload = verdict switch
        {
            ChainConfirmation.Confirmed => RecoveryPayload(amount, transactionId),
            ChainConfirmation.FailedOnChain => ResolvedAbsentPayload,
            _ => null,
        };
        if (payload is null)
            return Conflict("The move is still unconfirmed on chain. Try again once it is final.");

        var claim = await _idempotency.TryClaimAsync(ResolutionKey(moveKey), RecoveryOperationType, ct);
        if (claim.Won)
            await _idempotency.CompleteAsync(ResolutionKey(moveKey), payload, ct);
        else if (claim.Record.State != IdempotencyState.Completed || claim.Record.ResultPayload != payload)
            return Conflict("Another resolution of this move is in progress or disagrees. Nothing was changed.");

        // A landed balance move still owes the reserve partition its draw; the move key is its replay anchor.
        if (payload != ResolvedAbsentPayload && moveKey.Contains(":balance:", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(row.AssetId))
        {
            var draw = await _reserve.DrawAsync(new ReserveDrawRequest(
                chainType, row.AssetId!, _onRamp.ReservePartition, ReserveDrawKind.Return, amount, moveKey,
                ReserveDrawAuthority.NodeOperator), ct);
            if (draw.IsError)
                _logger.LogError("Reversal {ReversalId}: the resolved move's partition draw failed: {Detail}", row.Id, draw.Message);
        }

        row.Status = FiatReversalStatuses.Recovering;
        row.PendingMoveKey = null;
        row.StatusReason = Truncate(
            $"Resolved by operator {actor:N} ({(payload == ResolvedAbsentPayload ? "move absent" : "move landed")}): {reason.Trim()}",
            MaxReasonLength);
        var saved = await SaveAsync(row, ct);
        if (saved.IsError)
            return saved;

        _logger.LogWarning(
            "Operator {Actor} resolved the parked move {MoveKey} on reversal {ReversalId} as {Verdict}.",
            actor, moveKey, row.Id, verdict);
        return saved;
    }

    /// <summary>Parses <c>chain=…</c>, <c>amount=…</c> and <c>tx=…</c> out of an ambiguous move's recorded error.</summary>
    private static bool TryReadAmbiguousMove(string error, out string chainType, out BigInteger amount, out string transactionId)
    {
        chainType = Field(error, " chain=") ?? string.Empty;
        transactionId = Field(error, " tx=") ?? string.Empty;
        amount = ReversalMath.ParseBaseUnits(Field(error, " amount="));
        return chainType.Length > 0 && transactionId.Length > 0 && amount > BigInteger.Zero;

        static string? Field(string text, string name)
        {
            var start = text.LastIndexOf(name, StringComparison.Ordinal);
            if (start < 0)
                return null;
            start += name.Length;
            var end = text.IndexOf(' ', start);
            return (end < 0 ? text[start..] : text[start..end]).Trim();
        }
    }

    private static AZOAResult<FiatReversal> Conflict(string message)
        => AZOAResult<FiatReversal>.FailureWithCode(message, AzoaErrorCodes.Conflict);
}
