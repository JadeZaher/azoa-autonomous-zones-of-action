using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>Bounded optimistic re-planning and winner replay shared by every contended hold and exchange transaction.</summary>
/// <remarks>See Providers/Stores/Surreal/AGENTS.md §Hold command and exchange source transactions.</remarks>
public sealed partial class SurrealHoldCommandStore
{
    /// <summary>Re-reads and re-plans after a moved counter row at most this many times before reporting no outcome.</summary>
    private const int MaxReplans = 4;

    /// <summary>One read-plan-commit pass: its outcome, and whether a counted row moved with no receipt proving a winner.</summary>
    private readonly record struct Attempt<T>(T? Result, bool Contended) where T : class;

    /// <summary>A terminal exchange transaction opened by <see cref="SourceTransition"/> and the receipt it commits.</summary>
    private sealed record TerminalPlan(List<SurrealQuery> Statements, ExchangeHoldSettlementReceipt Receipt);

    /// <summary>Plans from a fresh read each pass; a loser or an inapplicable pass returns the winner's receipt for the same terminal state.</summary>
    private async Task<ExchangeHoldSettlementReceipt?> TerminalAsync(string sourceId, string terminalState,
        Func<ExchangeHoldSource, CancellationToken, Task<TerminalPlan?>> plan, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxReplans; attempt++)
        {
            var source = await GetExchangeSourceAsync(sourceId, ct);
            if (source is null) return null;
            var prior = await SettlementReceiptAsync(sourceId, ct);
            if (prior is not null) return source.State == terminalState && SameExchangeSettlement(prior, source) ? prior : null;
            var planned = await plan(source, ct);
            if (planned is null) return await WinnerAsync(sourceId, terminalState, ct);
            if (await CommitTerminalAsync(planned, ct) is { } committed) return committed;
        }
        return await WinnerAsync(sourceId, terminalState, ct);
    }

    private async Task<ExchangeHoldSettlementReceipt?> CommitTerminalAsync(TerminalPlan planned, CancellationToken ct)
    {
        var statements = planned.Statements;
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(planned.Receipt), "exchange_terminal_receipt"));
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
            return planned.Receipt;
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(Conflict, StringComparison.Ordinal)
            || SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            return null;
        }
    }

    private async Task<ExchangeHoldSettlementReceipt?> WinnerAsync(string sourceId, string terminalState, CancellationToken ct)
    {
        var receipt = await SettlementReceiptAsync(sourceId, ct);
        var source = receipt is null ? null : await GetExchangeSourceAsync(sourceId, ct);
        return receipt is not null && source is not null && source.State == terminalState && SameExchangeSettlement(receipt, source)
            ? receipt : null;
    }

    private Task<ExchangeHoldSettlementReceipt?> SettlementReceiptAsync(string sourceId, CancellationToken ct)
        => executor.QuerySingleAsync<ExchangeHoldSettlementReceipt>(SurrealQuery<ExchangeHoldSettlementReceipt>.Key(sourceId), ct);

    /// <summary>The one terminal receipt matches the source's own recorded evidence and liability.</summary>
    private static bool SameExchangeSettlement(ExchangeHoldSettlementReceipt receipt, ExchangeHoldSource source)
    {
        if (source.SettlementEvidenceJson is not { } evidenceJson || receipt.Id != source.Id
            || receipt.SourceIntentHash != source.IntentHash || receipt.EvidenceJson != evidenceJson
            || JsonSerializer.Deserialize<ExchangeWireObservation>(evidenceJson)?.PreparedHash != receipt.PreparedHash) return false;
        return string.IsNullOrEmpty(source.HoldRecordId) ? string.IsNullOrEmpty(receipt.HoldRecordId)
            : !string.IsNullOrEmpty(receipt.HoldRecordId)
                && SurrealRecordGuid.BareId(receipt.HoldRecordId) == SurrealRecordGuid.BareId(source.HoldRecordId);
    }

    /// <summary>Cancels one exact Active release mandate inside the surrounding transaction.</summary>
    private static IReadOnlyList<SurrealQuery> CloseMandate(HoldReleaseMandate mandate, DateTimeOffset now, string detail, string scope)
        =>
        [
            SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<HoldReleaseMandate>(mandate.Id)
                .Where(row => row.Version == mandate.Version && row.Status == "Active" && row.TermsHash == mandate.TermsHash
                    && row.HoldRecordId == mandate.HoldRecordId)
                .Set(row => row.Status, "Cancelled").Set(row => row.Version, mandate.Version + 1).Set(row => row.UpdatedAt, now)
                .Set(row => row.Detail, detail).Build(), scope, "_" + scope),
            SurrealGeneratedStatementScope.RequireResult("_" + scope, Conflict),
        ];
}
