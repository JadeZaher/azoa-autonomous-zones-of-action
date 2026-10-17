using System.Text.Json;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Fiat.OffRamp;
using SurrealForge.Client;

namespace AZOA.WebAPI.Tests.TestSupport;

/// <summary>
/// In-memory <see cref="ITenantPayoutLedger"/> with the production commit contract: the float version CAS
/// and the entry CAS (create-if-absent, or version + from-state) apply together under one lock or not at all.
/// <see cref="BeforeCommit"/> lets a test park a contender after its reads, so two transitions really race.
/// </summary>
public sealed class InMemoryTenantPayoutLedger(TimeProvider clock) : ITenantPayoutLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TenantPayoutFloat> _floats = new();
    private readonly Dictionary<string, TenantPayoutCredit> _credits = new();
    private readonly Dictionary<string, TenantPayoutDebit> _debits = new();

    /// <summary>Awaited by every commit after its caller's reads and before the atomic apply.</summary>
    public Func<Task>? BeforeCommit { get; set; }

    public int Commits { get; private set; }
    public int LostCommits { get; private set; }

    public IReadOnlyList<TenantPayoutDebit> Debits { get { lock (_gate) return _debits.Values.Select(Clone).ToList(); } }
    public IReadOnlyList<TenantPayoutCredit> Credits { get { lock (_gate) return _credits.Values.Select(Clone).ToList(); } }

    /// <summary>Seeds an Available credit and the matching counter, as a matured top-up would leave them.</summary>
    public TenantPayoutFloat SeedAvailable(Guid tenant, long amount, string currency = "USD", string environment = "test")
    {
        var row = GetOrCreateFloatAsync(tenant, currency, environment).GetAwaiter().GetResult();
        lock (_gate)
        {
            var credit = new TenantPayoutCredit
            {
                Id = Guid.NewGuid().ToString("N"), IntentId = Guid.NewGuid().ToString("N"), FloatId = row.Id,
                TenantId = row.TenantId, CurrencyCode = row.CurrencyCode, Environment = row.Environment,
                PaymentIntentId = "pi_seed_" + Guid.NewGuid().ToString("N"), GrossMinorUnits = amount, NetMinorUnits = amount,
                State = TenantPayoutCreditStates.Available, CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow(),
            };
            _credits[credit.Id] = credit;
            var stored = _floats[row.Id];
            stored.CreditedMinorUnits += amount;
            stored.Version++;
            return Clone(stored);
        }
    }

    public Task<TenantPayoutFloat> GetOrCreateFloatAsync(Guid tenantId, string currencyCode, string environment, CancellationToken ct = default)
    {
        var id = TenantPayoutFloat.RecordIdFor(tenantId, currencyCode, environment);
        lock (_gate)
        {
            if (!_floats.TryGetValue(id, out var row))
            {
                row = new TenantPayoutFloat
                {
                    Id = id, TenantId = SurrealLink.ToLink("user", tenantId.ToString("N"))!,
                    CurrencyCode = currencyCode.ToUpperInvariant(), Environment = environment,
                    CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow(),
                };
                _floats[id] = row;
            }
            return Task.FromResult(Clone(row));
        }
    }

    public Task<TenantPayoutFloat?> GetFloatAsync(string floatId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_floats.TryGetValue(floatId, out var row) ? Clone(row) : null);
    }

    public Task<IReadOnlyList<TenantPayoutFloat>> ListFloatsAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenant = SurrealLink.ToLink("user", tenantId.ToString("N"));
        lock (_gate) return Task.FromResult<IReadOnlyList<TenantPayoutFloat>>(_floats.Values.Where(f => f.TenantId == tenant).Select(Clone).ToList());
    }

    public Task<TenantPayoutCredit> RecordCreditAsync(TenantPayoutCredit candidate, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _credits.TryAdd(candidate.Id, Clone(candidate));
            return Task.FromResult(Clone(_credits[candidate.Id]));
        }
    }

    public Task<TenantPayoutCredit?> GetCreditAsync(string creditId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_credits.TryGetValue(creditId, out var row) ? Clone(row) : null);
    }

    public Task<TenantPayoutCredit?> GetCreditByPaymentAsync(string paymentIntentId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_credits.Values.Where(c => c.PaymentIntentId == paymentIntentId).Select(Clone).FirstOrDefault());
    }

    public Task<IReadOnlyList<TenantPayoutCredit>> ListMaturedPendingAsync(DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<TenantPayoutCredit>>(_credits.Values
                .Where(c => c.State == TenantPayoutCreditStates.Pending && c.AvailableAt is { } at && at <= now)
                .OrderBy(c => c.AvailableAt).Take(limit).Select(Clone).ToList());
    }

    public Task<TenantPayoutDebit?> GetDebitAsync(string payoutId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_debits.TryGetValue(payoutId, out var row) ? Clone(row) : null);
    }

    public Task<IReadOnlyList<TenantPayoutDebit>> ListStaleReservedAsync(DateTimeOffset before, int limit, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<TenantPayoutDebit>>(_debits.Values
                .Where(d => d.State == TenantPayoutDebitStates.Reserved && d.CreatedAt < before)
                .OrderBy(d => d.CreatedAt).Take(limit).Select(Clone).ToList());
    }

    public async Task<bool> CommitAsync(TenantPayoutTransition transition, CancellationToken ct = default)
    {
        if (BeforeCommit is { } hold)
            await hold();
        lock (_gate)
        {
            if (!_floats.TryGetValue(transition.Observed.Id, out var row) || row.Version != transition.Observed.Version)
                return Lost();
            switch (transition.Entry)
            {
                case TenantPayoutEntryChange.CreateDebit create:
                    if (_debits.ContainsKey(create.Debit.Id))
                        return Lost();
                    _debits[create.Debit.Id] = Clone(create.Debit);
                    break;
                case TenantPayoutEntryChange.UpdateDebit change:
                    if (!_debits.TryGetValue(change.Id, out var debit) || debit.Version != change.ExpectedVersion || debit.State != change.FromState)
                        return Lost();
                    debit.State = change.ToState;
                    debit.ReversedMinorUnits = change.Reversed;
                    debit.Version++;
                    break;
                case TenantPayoutEntryChange.UpdateCredit change:
                    if (!_credits.TryGetValue(change.Id, out var credit) || credit.Version != change.ExpectedVersion || credit.State != change.FromState)
                        return Lost();
                    credit.State = change.ToState;
                    credit.ReversedMinorUnits = change.Reversed;
                    credit.Version++;
                    break;
            }
            row.CreditedMinorUnits = transition.Next.Credited;
            row.ReservedMinorUnits = transition.Next.Reserved;
            row.FinalizedMinorUnits = transition.Next.Finalized;
            row.Version++;
            Commits++;
            return true;
        }
    }

    public Task<TenantPayoutSnapshot?> ReadSnapshotAsync(string floatId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_floats.TryGetValue(floatId, out var row)
                ? new TenantPayoutSnapshot(Clone(row),
                    _credits.Values.Where(c => c.FloatId == floatId).Select(Clone).ToList(),
                    _debits.Values.Where(d => d.FloatId == floatId).Select(Clone).ToList())
                : null);
    }

    private bool Lost()
    {
        LostCommits++;
        return false;
    }

    private static T Clone<T>(T row) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(row))!;
}
