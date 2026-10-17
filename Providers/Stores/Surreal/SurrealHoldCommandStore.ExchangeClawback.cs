using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>Reserves fiat-reversal clawbacks of delivered purchase units before anything is sent on a rail.</summary>
/// <remarks>See Providers/Stores/Surreal/AGENTS.md §Hold command and exchange source transactions (clawback reservation).</remarks>
public sealed partial class SurrealHoldCommandStore
{
    /// <inheritdoc/>
    public async Task<ExchangePurchaseRecoveryResult?> ReserveExchangeClawbackAsync(ExchangePurchaseRecovery request,
        CancellationToken ct = default)
    {
        if (request is null || request.Version != 1 || string.IsNullOrWhiteSpace(request.ReversalId) || string.IsNullOrWhiteSpace(request.LotId)
            || request.SubjectUserId == Guid.Empty || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 500
            || string.IsNullOrWhiteSpace(request.SourceId)
            || !UnsignedIntegerText.TryParse(request.AttributedSourceUnits, out var want) || want == 0
            || !UnsignedIntegerText.TryParse(request.ClaimSourceUnits, out var cap) || cap == 0 || want > cap) return null;
        for (var attempt = 1; attempt <= MaxReplans; attempt++)
        {
            var already = (await ReversalReceiptsAsync(request.ReversalId, request.SubjectUserId, ct))
                .Where(entry => entry.Request.LotId == request.LotId && entry.Result.SourceId == request.SourceId)
                .Aggregate(BigInteger.Zero, (total, entry) => total + Parse(entry.Result.CreditedSourceUnits));
            if (want <= already) return null;
            var receiptId = CanonicalJson.Hash(new { Domain = "exchange-purchase-clawback-v1", request.ReversalId, request.LotId,
                request.SourceId, RecoveredBefore = Format(already) });
            var outcome = await ReserveClawbackStepAsync(request, want - already, cap, receiptId, ct);
            if (!outcome.Contended) return outcome.Result;
        }
        return null;
    }

    /// <summary>One reservation step at a fixed receipt position; a loser returns the winner's reservation.</summary>
    private async Task<Attempt<ExchangePurchaseRecoveryResult>> ReserveClawbackStepAsync(ExchangePurchaseRecovery request,
        BigInteger wanted, BigInteger cap, string receiptId, CancellationToken ct)
    {
        var source = await GetExchangeSourceAsync(request.SourceId, ct);
        var context = ReadExchangeSource(source);
        if (context is null || source!.State != "Completed" || source.Version is < 0 or long.MaxValue
            || context.Intent.ParticipantUserId != request.SubjectUserId
            || !UnsignedIntegerText.TryParse(context.Intent.PlatformQuantityBaseUnits, out var spent) || spent == 0) return default;
        var intent = context.Intent;
        BigInteger received;
        BigInteger delivered;
        if (intent.Units is { Kind: "Issue" } units)
        {
            if (!UnsignedIntegerText.TryParse(units.QuantityBaseUnits, out received)) return default;
            delivered = received;
        }
        else if (intent.Opening is { Definition: { } definition } opening && source.HoldRecordId is not null)
        {
            // A held purchase is clawed back only once its liability has been released to someone.
            var liability = await CurrentLiabilityAsync(source.HoldRecordId, opening, ct);
            if (!UnsignedIntegerText.TryParse(definition.QuantityBaseUnits, out received) || liability is not { State: "Released" }
                || !UnsignedIntegerText.TryParse(liability.Amount, out delivered)) return await PriorClawbackAsync(request, receiptId, ct);
        }
        else return default;
        if (received == 0) return default;
        var allocations = await executor.QueryAsync<ExchangePurchaseAllocation>(SurrealQuery<ExchangePurchaseAllocation>.From()
            .Where(row => row.SourceId == source.Id), ct);
        var allocationId = CanonicalJson.Hash(new { Domain = "exchange-purchase-allocation-v1", request.LotId, request.SourceId });
        var allocation = allocations.FirstOrDefault(row => SurrealRecordGuid.BareId(row.Id) == allocationId);
        var clawedFromPurchase = allocations.Aggregate(BigInteger.Zero, (total, row) => total + Parse(row.ClawedAssetUnits));
        var consumed = BigInteger.Zero;
        var recoveredBefore = BigInteger.Zero;
        var clawedBefore = BigInteger.Zero;
        if (allocation is not null && (allocation.LotId != request.LotId || allocation.Version == long.MaxValue
            || !UnsignedIntegerText.TryParse(allocation.ConsumedSourceUnits, out consumed)
            || !UnsignedIntegerText.TryParse(allocation.RecoveredHoldUnits, out recoveredBefore)
            || !UnsignedIntegerText.TryParse(allocation.ClawedAssetUnits, out clawedBefore))) return default;
        var delta = BigInteger.Min(wanted, cap - consumed);
        if (delta <= 0) return await PriorClawbackAsync(request, receiptId, ct);
        // §amounts: the payment's own share of the purchase, rounded up, never past what was delivered and not yet clawed.
        var clawed = BigInteger.Min(CeilingDivide(received * delta, spent), delivered - clawedFromPurchase);
        var credited = clawed <= 0 ? BigInteger.Zero : BigInteger.Min(delta, CeilingDivide(clawed * spent, received));
        if (credited == 0) return await PriorClawbackAsync(request, receiptId, ct);
        var now = DateTimeOffset.UtcNow;
        // raw: the purchase lock, the lot's attribution cap and the clawback reservation commit before any rail effect.
        var statements = new List<SurrealQuery>
        {
            SurrealQuery.Of("BEGIN"),
            // lock: the source row serializes every clawback of this purchase, so the delivered cap read above holds at commit.
            SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<ExchangeHoldSource>(source.Id)
                .Where(row => row.Version == source.Version && row.State == "Completed" && row.IntentHash == source.IntentHash)
                .Set(row => row.Version, source.Version + 1).Build(), "exchange_clawback_source", "_exchange_clawback_source"),
            SurrealGeneratedStatementScope.RequireResult("_exchange_clawback_source", Conflict),
        };
        if (allocation is null)
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(new ExchangePurchaseAllocation
            {
                Id = allocationId, LotId = request.LotId, SourceId = source.Id, HoldRecordId = source.HoldRecordId ?? string.Empty,
                ConsumedSourceUnits = Format(credited), RecoveredHoldUnits = Format(clawed), ClawedAssetUnits = Format(clawed),
                Version = 1, CreatedAt = now, UpdatedAt = now,
            }), "exchange_clawback_allocation"));
        else
        {
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<ExchangePurchaseAllocation>(allocation.Id)
                .Where(row => row.Version == allocation.Version && row.ConsumedSourceUnits == allocation.ConsumedSourceUnits
                    && row.ClawedAssetUnits == allocation.ClawedAssetUnits)
                .Set(row => row.ConsumedSourceUnits, Format(consumed + credited)).Set(row => row.RecoveredHoldUnits, Format(recoveredBefore + clawed))
                .Set(row => row.ClawedAssetUnits, Format(clawedBefore + clawed))
                .Set(row => row.Version, allocation.Version + 1).Set(row => row.UpdatedAt, now).Build(),
                "exchange_clawback_allocation_update", "_exchange_clawback_allocation"));
            statements.Add(SurrealGeneratedStatementScope.RequireResult("_exchange_clawback_allocation", Conflict));
        }
        var result = new ExchangePurchaseRecoveryResult(receiptId, source.Id, Format(clawed), Format(credited),
            source.HoldRecordId ?? string.Empty, null, Clawback: true);
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(new HoldRecoveryReceipt
        {
            Id = receiptId, OperationId = request.ReversalId, Kind = ClawbackKind, ActorUserId = request.SubjectUserId.ToString("N"),
            RequestHash = CanonicalJson.Hash(request), AuthorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(request)),
            OriginalState = source.State, OriginalVersion = source.Version, OutcomeJson = JsonSerializer.Serialize(result), CreatedAt = now,
        }), "exchange_clawback_receipt"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var query = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(query, ct);
                response.EnsureAllOk();
                return response;
            }, ct);
            return new(result, false);
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(Conflict, StringComparison.Ordinal)
            || SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            var winner = await PriorClawbackAsync(request, receiptId, ct);
            return winner.Result is not null ? winner : new(null, true);
        }
    }

    /// <summary>The clawback already reserved at this receipt position by the same reversal, lot and purchase.</summary>
    private async Task<Attempt<ExchangePurchaseRecoveryResult>> PriorClawbackAsync(ExchangePurchaseRecovery request, string receiptId,
        CancellationToken ct)
    {
        var prior = await GetRecoveryAsync(receiptId, ct);
        var original = prior is { Kind: ClawbackKind } ? JsonSerializer.Deserialize<ExchangePurchaseRecovery>(prior.AuthorityJson) : null;
        var result = prior is null ? null : JsonSerializer.Deserialize<ExchangePurchaseRecoveryResult>(prior.OutcomeJson);
        return original is not null && result is { Clawback: true } && prior!.OperationId == request.ReversalId
            && original.ReversalId == request.ReversalId && original.LotId == request.LotId && original.SourceId == request.SourceId
            && original.SubjectUserId == request.SubjectUserId && result.SourceId == request.SourceId ? new(result, false) : default;
    }
}
