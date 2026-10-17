// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.Fiat.OffRamp;

/// <summary>Durable states of a tenant payout debit. Mirrors the POCO allow-list.</summary>
public static class TenantPayoutDebitStates
{
    public const string Reserved = "Reserved";
    public const string Finalized = "Finalized";
    public const string Released = "Released";
}

/// <summary>Durable states of a tenant payout credit. Mirrors the POCO allow-list.</summary>
public static class TenantPayoutCreditStates
{
    public const string Pending = "Pending";
    public const string Available = "Available";
}

/// <summary>A float's three counters after a transition.</summary>
public sealed record TenantPayoutCounters(long Credited, long Reserved, long Finalized);

/// <summary>The one entry row a float transition is justified by.</summary>
public abstract record TenantPayoutEntryChange
{
    /// <summary>A new debit (the coverage reservation).</summary>
    public sealed record CreateDebit(TenantPayoutDebit Debit) : TenantPayoutEntryChange;

    /// <summary>A debit state/reversal change, compare-and-swapped on version and state.</summary>
    public sealed record UpdateDebit(string Id, long ExpectedVersion, string FromState, string ToState, long Reversed)
        : TenantPayoutEntryChange;

    /// <summary>A credit state/reversal change, compare-and-swapped on version and state.</summary>
    public sealed record UpdateCredit(string Id, long ExpectedVersion, string FromState, string ToState, long Reversed)
        : TenantPayoutEntryChange;
}

/// <summary>A float CAS plus its justifying entry change, committed together or not at all.</summary>
public sealed record TenantPayoutTransition(TenantPayoutFloat Observed, TenantPayoutCounters Next, TenantPayoutEntryChange Entry);

/// <summary>Float, credits and debits read inside one transaction.</summary>
public sealed record TenantPayoutSnapshot(
    TenantPayoutFloat Float, IReadOnlyList<TenantPayoutCredit> Credits, IReadOnlyList<TenantPayoutDebit> Debits);

/// <summary>Persistence for D-8 tenant payout floats; see <c>Services/Fiat/OffRamp/AGENTS.md</c> §tenant-funded.</summary>
public interface ITenantPayoutLedger
{
    /// <summary>The float for (tenant, currency, environment), created empty if absent.</summary>
    Task<TenantPayoutFloat> GetOrCreateFloatAsync(Guid tenantId, string currencyCode, string environment, CancellationToken ct = default);

    /// <summary>One float by record id; null when absent.</summary>
    Task<TenantPayoutFloat?> GetFloatAsync(string floatId, CancellationToken ct = default);

    /// <summary>Every float a tenant holds (all currencies and environments).</summary>
    Task<IReadOnlyList<TenantPayoutFloat>> ListFloatsAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Creates the credit only if absent; a losing racer gets the winner's row.</summary>
    Task<TenantPayoutCredit> RecordCreditAsync(TenantPayoutCredit candidate, CancellationToken ct = default);

    /// <summary>One credit by record id; null when absent.</summary>
    Task<TenantPayoutCredit?> GetCreditAsync(string creditId, CancellationToken ct = default);

    /// <summary>The credit funded by one payment, or null.</summary>
    Task<TenantPayoutCredit?> GetCreditByPaymentAsync(string paymentIntentId, CancellationToken ct = default);

    /// <summary>Pending credits whose maturity has passed, oldest first.</summary>
    Task<IReadOnlyList<TenantPayoutCredit>> ListMaturedPendingAsync(DateTimeOffset now, int limit, CancellationToken ct = default);

    /// <summary>One debit by payout id; null when absent.</summary>
    Task<TenantPayoutDebit?> GetDebitAsync(string payoutId, CancellationToken ct = default);

    /// <summary>Reserved debits created before <paramref name="before"/>, oldest first.</summary>
    Task<IReadOnlyList<TenantPayoutDebit>> ListStaleReservedAsync(DateTimeOffset before, int limit, CancellationToken ct = default);

    /// <summary>Commits the float CAS and the entry change atomically; false when either predicate failed.</summary>
    Task<bool> CommitAsync(TenantPayoutTransition transition, CancellationToken ct = default);

    /// <summary>The float and every entry behind it, read in one transaction.</summary>
    Task<TenantPayoutSnapshot?> ReadSnapshotAsync(string floatId, CancellationToken ct = default);
}

/// <summary>SurrealDB-backed tenant payout ledger. Colocated with the saga, like <see cref="SurrealFiatPayoutLedger"/>.</summary>
public sealed class SurrealTenantPayoutLedger(ISurrealExecutor executor, TimeProvider clock) : ITenantPayoutLedger
{
    private const string ChangedMarker = "TENANT_PAYOUT_CHANGED";

    /// <inheritdoc />
    public async Task<TenantPayoutFloat> GetOrCreateFloatAsync(Guid tenantId, string currencyCode, string environment, CancellationToken ct = default)
    {
        var id = TenantPayoutFloat.RecordIdFor(tenantId, currencyCode, environment);
        if (await GetFloatAsync(id, ct) is { } existing)
            return existing;
        var now = clock.GetUtcNow();
        var candidate = new TenantPayoutFloat
        {
            Id = id,
            TenantId = SurrealLink.ToLink(User.SchemaNameConst, tenantId.ToString("N"))!,
            CurrencyCode = currencyCode.Trim().ToUpperInvariant(),
            Environment = environment.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        try
        {
            (await executor.ExecuteAsync(SurrealWriter.Create(candidate), ct)).EnsureAllOk();
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)) { }
        return await GetFloatAsync(id, ct) ?? throw new InvalidOperationException($"Tenant payout float '{id}' could not be created.");
    }

    /// <inheritdoc />
    public Task<TenantPayoutFloat?> GetFloatAsync(string floatId, CancellationToken ct = default)
        => executor.QuerySingleAsync<TenantPayoutFloat>(SurrealQuery<TenantPayoutFloat>.Key(floatId), ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<TenantPayoutFloat>> ListFloatsAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenant = SurrealLink.ToLink(User.SchemaNameConst, tenantId.ToString("N"));
        return await executor.QueryAsync<TenantPayoutFloat>(SurrealQuery<TenantPayoutFloat>.From()
            .Where(row => row.TenantId == tenant).OrderBy(row => row.CurrencyCode).ThenBy(row => row.Environment).Limit(100), ct);
    }

    /// <inheritdoc />
    public async Task<TenantPayoutCredit> RecordCreditAsync(TenantPayoutCredit candidate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        try
        {
            (await executor.ExecuteAsync(SurrealWriter.Create(candidate), ct)).EnsureAllOk();
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)) { }
        return await GetCreditAsync(candidate.Id, ct) ?? throw new InvalidOperationException($"Tenant payout credit '{candidate.Id}' could not be recorded.");
    }

    /// <inheritdoc />
    public Task<TenantPayoutCredit?> GetCreditAsync(string creditId, CancellationToken ct = default)
        => executor.QuerySingleAsync<TenantPayoutCredit>(SurrealQuery<TenantPayoutCredit>.Key(creditId), ct);

    /// <inheritdoc />
    public async Task<TenantPayoutCredit?> GetCreditByPaymentAsync(string paymentIntentId, CancellationToken ct = default)
        => (await executor.QueryAsync<TenantPayoutCredit>(SurrealQuery<TenantPayoutCredit>.From()
            .Where(row => row.PaymentIntentId == paymentIntentId).Limit(1), ct)).SingleOrDefault();

    /// <inheritdoc />
    public async Task<IReadOnlyList<TenantPayoutCredit>> ListMaturedPendingAsync(DateTimeOffset now, int limit, CancellationToken ct = default)
        => await executor.QueryAsync<TenantPayoutCredit>(SurrealQuery<TenantPayoutCredit>.From()
            .Where(row => row.State == TenantPayoutCreditStates.Pending && row.AvailableAt != null && row.AvailableAt <= now)
            .OrderBy(row => row.AvailableAt).Limit(Math.Clamp(limit, 1, 200)), ct);

    /// <inheritdoc />
    public Task<TenantPayoutDebit?> GetDebitAsync(string payoutId, CancellationToken ct = default)
        => executor.QuerySingleAsync<TenantPayoutDebit>(SurrealQuery<TenantPayoutDebit>.Key(payoutId), ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<TenantPayoutDebit>> ListStaleReservedAsync(DateTimeOffset before, int limit, CancellationToken ct = default)
        => await executor.QueryAsync<TenantPayoutDebit>(SurrealQuery<TenantPayoutDebit>.From()
            .Where(row => row.State == TenantPayoutDebitStates.Reserved && row.CreatedAt < before)
            .OrderBy(row => row.CreatedAt).Limit(Math.Clamp(limit, 1, 200)), ct);

    /// <inheritdoc />
    public async Task<bool> CommitAsync(TenantPayoutTransition transition, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transition);
        var observed = transition.Observed;
        var next = transition.Next;
        var now = clock.GetUtcNow();

        // raw: the float counters and the entry that justifies them commit atomically (D-8 exactly-once accounting).
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        // lock: the float version CAS serializes every transition of this float.
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<TenantPayoutFloat>(observed.Id)
            .Where(row => row.Version == observed.Version)
            .Set(row => row.CreditedMinorUnits, next.Credited)
            .Set(row => row.ReservedMinorUnits, next.Reserved)
            .Set(row => row.FinalizedMinorUnits, next.Finalized)
            .Set(row => row.Version, observed.Version + 1)
            .Set(row => row.UpdatedAt, now).Build(), "tpf", "_tpf"));
        statements.Add(SurrealGeneratedStatementScope.RequireResult("_tpf", ChangedMarker));
        switch (transition.Entry)
        {
            case TenantPayoutEntryChange.CreateDebit create:
                statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(create.Debit), "tpd"));
                break;
            case TenantPayoutEntryChange.UpdateDebit debit:
                statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<TenantPayoutDebit>(debit.Id)
                    .Where(row => row.Version == debit.ExpectedVersion && row.State == debit.FromState)
                    .Set(row => row.State, debit.ToState)
                    .Set(row => row.ReversedMinorUnits, debit.Reversed)
                    .Set(row => row.Version, debit.ExpectedVersion + 1)
                    .Set(row => row.UpdatedAt, now).Build(), "tpd", "_tpd"));
                statements.Add(SurrealGeneratedStatementScope.RequireResult("_tpd", ChangedMarker));
                break;
            case TenantPayoutEntryChange.UpdateCredit credit:
                statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<TenantPayoutCredit>(credit.Id)
                    .Where(row => row.Version == credit.ExpectedVersion && row.State == credit.FromState)
                    .Set(row => row.State, credit.ToState)
                    .Set(row => row.ReversedMinorUnits, credit.Reversed)
                    .Set(row => row.Version, credit.ExpectedVersion + 1)
                    .Set(row => row.UpdatedAt, now).Build(), "tpc", "_tpc"));
                statements.Add(SurrealGeneratedStatementScope.RequireResult("_tpc", ChangedMarker));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(transition), "Unknown tenant payout entry change.");
        }
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
            return true;
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(ChangedMarker, StringComparison.Ordinal)
            || SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<TenantPayoutSnapshot?> ReadSnapshotAsync(string floatId, CancellationToken ct = default)
    {
        // raw: the counters and the entries are read in one transaction so the consistency check compares one instant.
        var query = SurrealQuery.Combine(
            SurrealQuery.Of("BEGIN"),
            SurrealGeneratedStatementScope.Apply(SurrealQuery<TenantPayoutFloat>.Key(floatId), "snf"),
            SurrealGeneratedStatementScope.Apply(SurrealQuery<TenantPayoutCredit>.From().Where(row => row.FloatId == floatId), "snc"),
            SurrealGeneratedStatementScope.Apply(SurrealQuery<TenantPayoutDebit>.From().Where(row => row.FloatId == floatId), "snd"),
            SurrealQuery.Of("COMMIT"));
        var response = await executor.ExecuteAsync(query, ct);
        response.EnsureAllOk();
        var row = response.GetValues<TenantPayoutFloat>(1).SingleOrDefault();
        return row is null ? null : new TenantPayoutSnapshot(
            row, response.GetValues<TenantPayoutCredit>(2).ToList(), response.GetValues<TenantPayoutDebit>(3).ToList());
    }
}
