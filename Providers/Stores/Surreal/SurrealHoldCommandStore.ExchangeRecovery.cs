using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Hold;
using AZOA.WebAPI.Services.Reserve;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>Exact fiat-reversal recovery of a settled held purchase, splitting the liability on partial attribution.</summary>
/// <remarks>See Providers/Stores/Surreal/AGENTS.md §Hold command and exchange source transactions (purchase recovery).</remarks>
public sealed partial class SurrealHoldCommandStore
{
    private const int MaxRemainderHops = 32;
    private const string RecoveryKind = "FiatReversalRecovery";
    private const string ClawbackKind = "FiatReversalClawback";

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ExchangeHoldSource>> ListParticipantPurchasesAsync(Guid participant, int limit,
        CancellationToken ct = default)
    {
        var participantId = participant.ToString("N");
        return await executor.QueryAsync<ExchangeHoldSource>(SurrealQuery<ExchangeHoldSource>.From()
            .Where(row => row.ParticipantUserId == participantId && row.State == "Completed")
            .OrderBy(row => row.UpdatedAt).ThenBy(row => row.Id).Limit(Math.Clamp(limit, 1, 1000)), ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ExchangePurchaseRecoveryResult>> ListReversalRecoveriesAsync(string reversalId, Guid subjectUserId,
        CancellationToken ct = default)
        => (await ReversalReceiptsAsync(reversalId, subjectUserId, ct)).Select(entry => entry.Result).ToList();

    /// <inheritdoc/>
    public async Task<ExchangePurchaseLiability?> GetPurchaseLiabilityAsync(string sourceId, CancellationToken ct = default)
    {
        var source = await GetExchangeSourceAsync(sourceId, ct);
        if (ReadExchangeSource(source) is not { Intent.Opening: { } opening } || source!.State != "Completed" || source.HoldRecordId is null)
            return null;
        var liability = await CurrentLiabilityAsync(source.HoldRecordId, opening, ct);
        if (liability is null) return null;
        string? releasedTo = null;
        if (liability.State == "Released")
        {
            var recordId = SurrealRecordGuid.BareId(liability.Id);
            var releases = await executor.QueryAsync<HoldCommand>(SurrealQuery<HoldCommand>.From()
                .Where(row => row.HoldRecordId == recordId && row.Kind == "Release" && row.State == "Completed").Limit(2), ct);
            var plan = releases.Count == 1 && releases[0].RailPlanJson is { } json ? JsonSerializer.Deserialize<HoldRailPlan>(json) : null;
            releasedTo = plan?.RecipientWallet?.Address;
        }
        return new(SurrealRecordGuid.BareId(liability.Id), liability.State, liability.Amount, releasedTo);
    }

    /// <inheritdoc/>
    public async Task<ExchangePurchaseRecoveryResult?> RecoverExchangePurchaseAsync(ExchangePurchaseRecovery request,
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
            var receiptId = CanonicalJson.Hash(new { Domain = "exchange-purchase-recovery-v2", request.ReversalId, request.LotId,
                request.SourceId, RecoveredBefore = Format(already) });
            var outcome = await RecoverStepAsync(request, want - already, cap, receiptId, ct);
            if (!outcome.Contended) return outcome.Result;
        }
        return null;
    }

    /// <summary>One read-plan-commit recovery step at a fixed receipt position; a loser returns the winner's step.</summary>
    private async Task<Attempt<ExchangePurchaseRecoveryResult>> RecoverStepAsync(ExchangePurchaseRecovery request,
        BigInteger wanted, BigInteger cap, string receiptId, CancellationToken ct)
    {
        var source = await GetExchangeSourceAsync(request.SourceId, ct);
        var context = ReadExchangeSource(source);
        if (context is not { Intent.Opening: { Definition: { } purchasedDefinition } opening } || source!.State != "Completed"
            || source.HoldRecordId is null || context.Intent.ParticipantUserId != request.SubjectUserId) return default;
        var intent = context.Intent;
        var liability = await CurrentLiabilityAsync(source.HoldRecordId, opening, ct);
        if (liability?.DefinitionJson is null || liability.State != "Held" || liability.ActiveCommandId is not null
            || liability.EconomicSourceId != source.Id || liability.FunderUserId is not null || liability.Version is < 0 or long.MaxValue
            || SurrealRecordGuid.ParseOptional(liability.BeneficiaryUserId) != request.SubjectUserId
            || !UnsignedIntegerText.TryParse(intent.PlatformQuantityBaseUnits, out var spent) || spent == 0
            || !UnsignedIntegerText.TryParse(purchasedDefinition.QuantityBaseUnits, out var purchased) || purchased == 0
            || !UnsignedIntegerText.TryParse(liability.Amount, out var held) || held == 0) return await PriorStepAsync(request, receiptId, ct);
        var allocationId = CanonicalJson.Hash(new { Domain = "exchange-purchase-allocation-v1", request.LotId, request.SourceId });
        var allocation = await executor.QuerySingleAsync<ExchangePurchaseAllocation>(SurrealQuery<ExchangePurchaseAllocation>.Key(allocationId), ct);
        var consumed = BigInteger.Zero;
        var recoveredBefore = BigInteger.Zero;
        if (allocation is not null && (allocation.SourceId != source.Id || allocation.LotId != request.LotId || allocation.Version == long.MaxValue
            || !UnsignedIntegerText.TryParse(allocation.ConsumedSourceUnits, out consumed)
            || !UnsignedIntegerText.TryParse(allocation.RecoveredHoldUnits, out recoveredBefore))) return default;
        // The lot's attributed share of this purchase is the cap across every reversal of the lot.
        var delta = BigInteger.Min(wanted, cap - consumed);
        if (delta <= 0) return await PriorStepAsync(request, receiptId, ct);
        // §amounts: recovery rounds UP, capped at what is still held; credit is derived from what was actually taken.
        var share = BigInteger.Min(held, CeilingDivide(purchased * delta, spent));
        var credited = BigInteger.Min(delta, CeilingDivide(share * spent, purchased));
        if (share == 0 || credited == 0) return await PriorStepAsync(request, receiptId, ct);
        var reserve = await ExchangeReserveAsync(opening.Asset, ct);
        if (reserve is null) return default;
        var planned = HoldReserveMutationPlan.Create(reserve, liability.ReservePartition, ReserveDrawKind.CancelHold,
            Format(share), opening.Asset.ProtocolIdentity);
        if (planned.IsError || planned.Result is not { } mutation) return await PriorStepAsync(request, receiptId, ct);
        var mandate = liability.ActiveReleaseMandateId is { } mandateId
            ? await executor.QuerySingleAsync<HoldReleaseMandate>(SurrealQuery<HoldReleaseMandate>.Key(mandateId), ct) : null;
        if (liability.ActiveReleaseMandateId is not null && (mandate is null || mandate.Status != "Active"
            || mandate.Version is < 0 or long.MaxValue || mandate.ExchangeSourceJson is null)) return default;
        var requestHash = CanonicalJson.Hash(request);
        var now = DateTimeOffset.UtcNow;
        var liabilityId = SurrealRecordGuid.BareId(liability.Id);
        string? remainderId = null;
        // raw: reversal attribution cap, exact liability recovery or split, reserve return and immutable receipts commit together.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        if (share == held)
        {
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<HoldLedger>(liability.Id)
                .Where(row => row.Version == liability.Version && row.State == "Held" && row.ActiveCommandId == null
                    && row.EconomicSourceId == source.Id && row.DefinitionHash == liability.DefinitionHash
                    && row.ActiveReleaseMandateId == liability.ActiveReleaseMandateId)
                .Set(row => row.State, "Refunded").Set(row => row.Version, liability.Version + 1).Set(row => row.UpdatedAt, now)
                .Set(row => row.SettledAt, now).Set(row => row.SettlementReason, request.Reason)
                .Unset(row => row.ActiveReleaseMandateId).Build(), "exchange_recovery_liability", "_exchange_recovery_liability"));
            statements.Add(SurrealGeneratedStatementScope.RequireResult("_exchange_recovery_liability", Conflict));
            if (mandate is not null) statements.AddRange(CloseMandate(mandate, now,
                "A reversed payment recovered this purchase; its release permission is closed.", "exchange_recovery_mandate"));
        }
        else
        {
            var remainder = held - share;
            var childHoldId = $"{liability.HoldId}:remainder:{receiptId[..12]}";
            var commandId = CanonicalJson.Hash(new { Domain = "exchange-purchase-recovery-command-v1", ReceiptId = receiptId });
            var childDefinition = JsonSerializer.Deserialize<HoldDefinition>(liability.DefinitionJson)! with { QuantityBaseUnits = Format(remainder) };
            var childRow = HoldAggregateProjection.Create(opening, childHoldId, childDefinition, commandId, now);
            childRow.ParentHoldId = liability.HoldId;
            childRow.InheritsReserveCommitment = true;
            childRow.EconomicSourceId = source.Id;
            remainderId = SurrealRecordGuid.BareId(childRow.Id);
            var view = new HoldCommandView(commandId, 0, "Recover", "Completed", liabilityId, liability.HoldId, opening.Asset,
                Format(remainder), opening.ActorUserId, opening.OwnerUserId, null, request.SubjectUserId, null, request.Reason,
                remainderId, opening.CustodyTenantId);
            var commandReceipt = new HoldCommandReceipt { Id = commandId, HoldRecordId = liabilityId, IntentHash = requestHash,
                ActorUserId = opening.ActorUserId.ToString("N"), TenantId = null, OutcomeJson = JsonSerializer.Serialize(view), CreatedAt = now };
            if (mandate is not null)
            {
                var derived = DeriveRemainderMandate(mandate, childRow, remainder, commandId, commandReceipt.OutcomeJson, source, now);
                if (derived is null) return default;
                childRow.ActiveReleaseMandateId = derived.Id;
                statements.AddRange(CloseMandate(mandate, now,
                    "A partial reversal split this purchase; the remaining units keep a narrowed release permission.", "exchange_recovery_mandate"));
                statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(derived), "exchange_recovery_derived_mandate"));
            }
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<HoldLedger>(liability.Id)
                .Where(row => row.Version == liability.Version && row.State == "Held" && row.ActiveCommandId == null
                    && row.EconomicSourceId == source.Id && row.DefinitionHash == liability.DefinitionHash
                    && row.ActiveReleaseMandateId == liability.ActiveReleaseMandateId)
                .Set(row => row.State, "PartiallyRecovered").Set(row => row.ChildHoldId, childHoldId)
                .Set(row => row.Version, liability.Version + 1).Set(row => row.UpdatedAt, now).Set(row => row.SettledAt, now)
                .Set(row => row.SettlementReason, request.Reason).Unset(row => row.ActiveReleaseMandateId).Build(),
                "exchange_recovery_split", "_exchange_recovery_split"));
            statements.Add(SurrealGeneratedStatementScope.RequireResult("_exchange_recovery_split", Conflict));
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(childRow), "exchange_recovery_remainder"));
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(commandReceipt), "exchange_recovery_command"));
        }
        statements.AddRange(ReserveWrite(reserve, mutation, now, "exchange_recovery_reserve"));
        // The allocation CAS (or deterministic create) makes the consumed + delta <= cap check above hold at commit.
        if (allocation is null)
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(new ExchangePurchaseAllocation
            {
                Id = allocationId, LotId = request.LotId, SourceId = source.Id, HoldRecordId = source.HoldRecordId,
                ConsumedSourceUnits = Format(credited), RecoveredHoldUnits = Format(share), Version = 1, CreatedAt = now, UpdatedAt = now,
            }), "exchange_recovery_allocation"));
        else
        {
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<ExchangePurchaseAllocation>(allocation.Id)
                .Where(row => row.Version == allocation.Version && row.ConsumedSourceUnits == allocation.ConsumedSourceUnits
                    && row.RecoveredHoldUnits == allocation.RecoveredHoldUnits)
                .Set(row => row.ConsumedSourceUnits, Format(consumed + credited)).Set(row => row.RecoveredHoldUnits, Format(recoveredBefore + share))
                .Set(row => row.Version, allocation.Version + 1).Set(row => row.UpdatedAt, now).Build(),
                "exchange_recovery_allocation_update", "_exchange_recovery_allocation"));
            statements.Add(SurrealGeneratedStatementScope.RequireResult("_exchange_recovery_allocation", Conflict));
        }
        var result = new ExchangePurchaseRecoveryResult(receiptId, source.Id, Format(share), Format(credited), liabilityId, remainderId);
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(new HoldRecoveryReceipt
        {
            Id = receiptId, OperationId = request.ReversalId, Kind = RecoveryKind, ActorUserId = request.SubjectUserId.ToString("N"),
            RequestHash = requestHash, AuthorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(request)),
            OriginalState = liability.State, OriginalVersion = liability.Version, OutcomeJson = JsonSerializer.Serialize(result), CreatedAt = now,
        }), "exchange_recovery_receipt"));
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
            var winner = await PriorStepAsync(request, receiptId, ct);
            return winner.Result is not null ? winner : new(null, true);
        }
    }

    /// <summary>The step already committed at this receipt position by the same reversal, lot and purchase.</summary>
    private async Task<Attempt<ExchangePurchaseRecoveryResult>> PriorStepAsync(ExchangePurchaseRecovery request, string receiptId,
        CancellationToken ct)
    {
        var prior = await GetRecoveryAsync(receiptId, ct);
        var original = prior is { Kind: RecoveryKind } ? JsonSerializer.Deserialize<ExchangePurchaseRecovery>(prior.AuthorityJson) : null;
        var result = prior is null ? null : JsonSerializer.Deserialize<ExchangePurchaseRecoveryResult>(prior.OutcomeJson);
        return original is not null && result is not null && prior!.OperationId == request.ReversalId
            && original.ReversalId == request.ReversalId && original.LotId == request.LotId && original.SourceId == request.SourceId
            && original.SubjectUserId == request.SubjectUserId && result.SourceId == request.SourceId ? new(result, false) : default;
    }

    private async Task<IReadOnlyList<(ExchangePurchaseRecovery Request, ExchangePurchaseRecoveryResult Result)>> ReversalReceiptsAsync(
        string reversalId, Guid subjectUserId, CancellationToken ct)
    {
        var actor = subjectUserId.ToString("N");
        var rows = await executor.QueryAsync<HoldRecoveryReceipt>(SurrealQuery<HoldRecoveryReceipt>.From()
            .Where(row => row.OperationId == reversalId && (row.Kind == RecoveryKind || row.Kind == ClawbackKind)
                && row.ActorUserId == actor)
            .OrderBy(row => row.CreatedAt).ThenBy(row => row.Id).Limit(1000), ct);
        var entries = new List<(ExchangePurchaseRecovery, ExchangePurchaseRecoveryResult)>();
        foreach (var row in rows)
        {
            var request = JsonSerializer.Deserialize<ExchangePurchaseRecovery>(row.AuthorityJson);
            var result = JsonSerializer.Deserialize<ExchangePurchaseRecoveryResult>(row.OutcomeJson);
            if (request is not null && result is not null && request.ReversalId == reversalId && request.SubjectUserId == subjectUserId)
                entries.Add((request, result));
        }
        return entries;
    }

    /// <summary>Follows recovery remainders from the purchased liability to the one still carrying the purchase.</summary>
    private async Task<HoldLedger?> CurrentLiabilityAsync(string holdRecordId, HoldCommandIntent opening, CancellationToken ct)
    {
        var liability = await GetHoldAsync(holdRecordId, ct);
        for (var hop = 0; liability is { State: "PartiallyRecovered", ChildHoldId: { } child } && hop < MaxRemainderHops; hop++)
            liability = await GetHoldAsync(HoldCommandIdentity.Hold(opening, child), ct);
        return liability;
    }

    /// <summary>Narrows an unexercised exchange release permission to the exact remainder liability, preserving every other term.</summary>
    private static HoldReleaseMandate? DeriveRemainderMandate(HoldReleaseMandate parent, HoldLedger child, BigInteger remainder,
        string recoveryCommandId, string recoveryOutcome, ExchangeHoldSource source, DateTimeOffset now)
    {
        var terms = JsonSerializer.Deserialize<HoldReleaseMandateTerms>(parent.TermsJson);
        var quote = JsonSerializer.Deserialize<ValueCustodyQuote>(parent.OriginalQuoteJson);
        var exchange = JsonSerializer.Deserialize<HoldReleaseExchangeSource>(parent.ExchangeSourceJson!);
        if (terms is null || quote is null || exchange is null || exchange.SourceId != source.Id) return null;
        var childId = SurrealRecordGuid.BareId(child.Id);
        var narrowedTerms = terms with { HoldRecordId = childId, DefinitionHash = child.DefinitionHash!, QuantityBaseUnits = Format(remainder) };
        var narrowedQuote = quote with { Request = quote.Request with { QuantityBaseUnits = Format(remainder) }, QuoteId = string.Empty };
        narrowedQuote = narrowedQuote with { QuoteId = ValueCustodyIdentity.QuoteId(narrowedQuote) };
        var lineage = exchange.Lineage is { } earlier
            ? earlier with { RecoveryCommandId = recoveryCommandId, RecoveryOutcomeHash = CanonicalJson.HashCanonical(recoveryOutcome) }
            : new HoldReleaseLineage(source.HoldRecordId!, recoveryCommandId, CanonicalJson.HashCanonical(recoveryOutcome));
        var termsJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(narrowedTerms));
        return new HoldReleaseMandate
        {
            Id = CanonicalJson.Hash(new { Domain = "exchange-release-mandate-remainder-v1", ParentMandateId = parent.Id, ChildRecordId = childId }),
            ActorUserId = parent.ActorUserId, TenantId = parent.TenantId, HoldRecordId = childId,
            RequestHash = CanonicalJson.Hash(new { ParentRequestHash = parent.RequestHash, RecoveryCommandId = recoveryCommandId }),
            TermsJson = termsJson, TermsHash = CanonicalJson.HashCanonical(termsJson),
            OriginalAuthorityJson = parent.OriginalAuthorityJson, CurrentAuthorityJson = parent.CurrentAuthorityJson,
            CurrentAuthorizationId = parent.CurrentAuthorizationId,
            OriginalQuoteJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(narrowedQuote)),
            ExchangeSourceJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(exchange with { Lineage = lineage })),
            Detail = "Narrowed to the remainder of a partially recovered purchase.",
            NotBefore = parent.NotBefore, ExpiresAt = parent.ExpiresAt, NextAttemptAt = parent.NextAttemptAt,
            CreatedAt = now, UpdatedAt = now,
        };
    }

    private static BigInteger CeilingDivide(BigInteger numerator, BigInteger denominator)
        => (numerator + denominator - 1) / denominator;

    private static BigInteger Parse(string? value) => UnsignedIntegerText.TryParse(value, out var parsed) ? parsed : BigInteger.Zero;

    private static string Format(BigInteger value) => value.ToString(CultureInfo.InvariantCulture);
}
