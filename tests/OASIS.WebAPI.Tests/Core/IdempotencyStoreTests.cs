using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OASIS.WebAPI.Core.Idempotency;
using OASIS.WebAPI.Data;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Idempotency;
using OASIS.WebAPI.Tests.TestSupport;

namespace OASIS.WebAPI.Tests.Core;

/// <summary>
/// Verifies the REAL <see cref="IdempotencyStore"/> against SQLite, which
/// actually enforces the UNIQUE constraint on <see cref="IdempotencyRecord.Key"/>.
/// EF-InMemory does not enforce unique indexes, so the
/// catch-<see cref="DbUpdateException"/>-then-reread path of
/// <see cref="IdempotencyStore.TryClaimAsync"/> would never fire and the
/// concurrency defence would be untested. The shared-cache in-memory mode of
/// <see cref="SqliteTestContext"/> gives every context its own connection but
/// one shared physical UNIQUE constraint — see that type for the rationale.
///
/// <para><see cref="IdempotencyStore"/> now injects an
/// <see cref="IServiceScopeFactory"/> and resolves a FRESH scoped
/// <see cref="OASISDbContext"/> per operation (so the claim INSERT is never
/// batched with unrelated tracked entities from a shared request context). Each
/// test therefore constructs the store over a real DI container whose
/// <see cref="OASISDbContext"/> registration produces a SQLite-backed context
/// bound to the SAME shared physical database — see
/// <see cref="CreateScopeFactory"/>.</para>
/// </summary>
public class IdempotencyStoreTests : IDisposable
{
    private readonly SqliteTestContext _db = SqliteTestContext.SharedCacheInMemory();
    // ConcurrentBag: the concurrency test builds providers from many threads.
    private readonly ConcurrentBag<ServiceProvider> _providers = new();
    private readonly ConcurrentBag<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (var sp in _providers)
            sp.Dispose();
        foreach (var d in _disposables)
            d.Dispose();
        _db.Dispose();
    }

    private OASISDbContext CreateContext() => _db.NewContext();

    /// <summary>
    /// Builds a real DI scope factory whose <see cref="OASISDbContext"/>
    /// resolution returns a fresh SQLite-backed context bound to the SAME
    /// shared physical DB this test owns. Each <c>CreateScope()</c> the store
    /// makes yields an isolated context (own connection) over the one shared
    /// UNIQUE constraint — exactly the production isolation model, exercised
    /// against a constraint-enforcing engine.
    /// </summary>
    private IServiceScopeFactory CreateScopeFactory()
    {
        var services = new ServiceCollection();
        // Scoped, transient-DbContext-per-scope: returns _db.NewContext() so
        // every resolved context attaches to the same shared-cache DB but with
        // its own connection/change-tracker (models a fresh request scope).
        services.AddScoped<OASISDbContext>(_ => _db.NewContext());
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    private IdempotencyStore CreateStore() => new(CreateScopeFactory());

    [Fact]
    public async Task TryClaimAsync_FirstCall_Wins_AndRecordIsInProgress()
    {
        var store = CreateStore();

        var claim = await store.TryClaimAsync("key-a", "bridge_redeem", CancellationToken.None);

        claim.Won.Should().BeTrue();
        claim.Record.Should().NotBeNull();
        claim.Record.Key.Should().Be("key-a");
        claim.Record.OperationType.Should().Be("bridge_redeem");
        claim.Record.State.Should().Be(IdempotencyState.InProgress);

        // Persisted and readable through a fresh store (same shared DB).
        var fetched = await CreateStore().GetAsync("key-a", CancellationToken.None);
        fetched.Should().NotBeNull();
        fetched!.State.Should().Be(IdempotencyState.InProgress);
    }

    // Exercises the real catch-DbUpdateException-then-reread path against the
    // real UNIQUE constraint — the heart of exactly-once.
    [Fact]
    public async Task TryClaimAsync_SecondCall_SameKey_LosesAndReturnsExistingRecord()
    {
        var first = await CreateStore()
            .TryClaimAsync("key-dup", "faucet_dispense", CancellationToken.None);
        first.Won.Should().BeTrue();

        var second = await CreateStore()
            .TryClaimAsync("key-dup", "faucet_dispense", CancellationToken.None);

        second.Won.Should().BeFalse();
        second.Record.Should().NotBeNull();
        second.Record.Key.Should().Be("key-dup");
        second.Record.State.Should().Be(IdempotencyState.InProgress);
    }

    // N parallel claims for the SAME key over independent scopes/connections
    // sharing one shared-cache DB ⇒ exactly one winner — the primitive-level
    // TOCTOU defence proof, now against the fresh-scope design.
    [Fact]
    public async Task TryClaimAsync_Concurrent_SameKey_ExactlyOneWinner()
    {
        const int parallelism = 24;
        const string key = "race-key";

        var results = new ConcurrentBag<IdempotencyClaim>();

        var tasks = Enumerable.Range(0, parallelism).Select(_ => Task.Run(async () =>
        {
            var store = CreateStore();
            var claim = await store.TryClaimAsync(key, "bridge_redeem", CancellationToken.None);
            results.Add(claim);
        })).ToArray();

        await Task.WhenAll(tasks);

        results.Should().HaveCount(parallelism);

        var winners = results.Where(r => r.Won).ToList();
        var losers = results.Where(r => !r.Won).ToList();

        winners.Should().HaveCount(1, "exactly one concurrent caller may win the claim");
        losers.Should().HaveCount(parallelism - 1);

        // Every loser observes the same winning record (same key, InProgress).
        losers.Should().OnlyContain(r => r.Record.Key == key);
        losers.Should().OnlyContain(r => r.Record.State == IdempotencyState.InProgress);

        // Exactly one physical row exists.
        var count = await CountRecords(key);
        count.Should().Be(1);
    }

    private async Task<int> CountRecords(string key)
    {
        await using var ctx = CreateContext();
        return await ctx.IdempotencyRecords.CountAsync(r => r.Key == key);
    }

    // ─────────────────────────────────────────────────────────────────────
    // (d) Complete then Get ⇒ Completed + payload persisted; a later
    //     duplicate claim returns the Completed record (replay).
    // ─────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task CompleteAsync_ThenGet_PersistsCompletedAndPayload_AndDuplicateClaimReplays()
    {
        const string key = "complete-key";

        var claim = await CreateStore()
            .TryClaimAsync(key, "bridge_redeem", CancellationToken.None);
        claim.Won.Should().BeTrue();

        await CreateStore()
            .CompleteAsync(key, "{\"txHash\":\"0xabc\"}", CancellationToken.None);

        var got = await CreateStore().GetAsync(key, CancellationToken.None);
        got.Should().NotBeNull();
        got!.State.Should().Be(IdempotencyState.Completed);
        got.ResultPayload.Should().Be("{\"txHash\":\"0xabc\"}");
        got.Error.Should().BeNull();

        // Replay: duplicate caller does NOT win and sees the cached result.
        var replay = await CreateStore()
            .TryClaimAsync(key, "bridge_redeem", CancellationToken.None);
        replay.Won.Should().BeFalse();
        replay.Record.State.Should().Be(IdempotencyState.Completed);
        replay.Record.ResultPayload.Should().Be("{\"txHash\":\"0xabc\"}");
    }

    [Fact]
    public async Task FailAsync_PersistsFailedStateAndError()
    {
        const string key = "fail-key";

        await CreateStore()
            .TryClaimAsync(key, "faucet_dispense", CancellationToken.None);

        await CreateStore()
            .FailAsync(key, "insufficient funds", CancellationToken.None);

        var got = await CreateStore().GetAsync(key, CancellationToken.None);
        got.Should().NotBeNull();
        got!.State.Should().Be(IdempotencyState.Failed);
        got.Error.Should().Be("insufficient funds");
    }

    [Fact]
    public async Task CompleteAsync_WithoutPriorClaim_Throws()
    {
        var store = CreateStore();

        var act = async () => await store.CompleteAsync(
            "never-claimed", "payload", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task FailAsync_WithoutPriorClaim_Throws()
    {
        var store = CreateStore();

        var act = async () => await store.FailAsync(
            "never-claimed", "err", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TryClaimAsync_NullOrEmptyKey_ThrowsArgumentException(string? key)
    {
        var store = CreateStore();

        var act = async () => await store.TryClaimAsync(
            key!, "op", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TryClaimAsync_DistinctKeys_AreIndependent()
    {
        var a = await CreateStore()
            .TryClaimAsync("indep-1", "op", CancellationToken.None);
        var b = await CreateStore()
            .TryClaimAsync("indep-2", "op", CancellationToken.None);

        a.Won.Should().BeTrue();
        b.Won.Should().BeTrue();

        a.Record.Key.Should().Be("indep-1");
        b.Record.Key.Should().Be("indep-2");
    }

    // ─────────────────────────────────────────────────────────────────────
    // HIGH-1 regression: a NON-unique DbUpdateException must be RETHROWN, not
    // swallowed as Won=false. We force a genuine, non-unique persistence error
    // by registering a context whose SaveChanges fails for a reason that is
    // NOT a UNIQUE-constraint violation (a NOT NULL violation on a required
    // column). The old "infer duplicate from a follow-up read" logic would
    // have masked this as an idempotent replay; the positive 23505/SQLite
    // detection rethrows it.
    // ─────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TryClaimAsync_NonUniqueDbUpdateException_IsRethrown_NotSwallowedAsLoser()
    {
        // Scope factory whose context forces a NOT NULL constraint failure on
        // insert (OperationType is required by the model). A NOT NULL violation
        // surfaces as a DbUpdateException whose inner SqliteException has
        // primary code 19 (SQLITE_CONSTRAINT) but extended code 1299
        // (SQLITE_CONSTRAINT_NOTNULL) — which is NEITHER 2067
        // (SQLITE_CONSTRAINT_UNIQUE) NOR 1555 (SQLITE_CONSTRAINT_PRIMARYKEY).
        // IsUniqueViolation matches ONLY the extended unique/PK codes (and, in
        // production, PostgreSQL SQLSTATE 23505), so this genuine non-unique
        // error must propagate rather than be masked as Won=false. (The old
        // code keyed off the bare primary code / a follow-up read and would
        // have swallowed this.)
        var store = new IdempotencyStore(new NotNullViolatingScopeFactory(_db, _disposables));

        var act = async () => await store.TryClaimAsync(
            "non-unique-err", "op", CancellationToken.None);

        // MUST surface the real DB error, NOT a fabricated Won=false replay.
        await act.Should().ThrowAsync<DbUpdateException>();

        // And nothing must have been persisted under that key (no phantom row
        // that a later caller would replay).
        var leaked = await CreateStore().GetAsync("non-unique-err", CancellationToken.None);
        leaked.Should().BeNull(
            "a failed non-unique insert must not leave a claim row to replay");
    }

    // ─────────────────────────────────────────────────────────────────────
    // HIGH-1 (b): a duplicate request whose token is cancelled must STILL
    // resolve to the winning record (the idempotent replay), NOT rethrow a
    // raw DbUpdateException. The removed `when (!ct.IsCancellationRequested)`
    // filter previously leaked the raw DB error on this race.
    // ─────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TryClaimAsync_Duplicate_WithCancelledToken_StillReturnsWinningRecord()
    {
        const string key = "cancel-dup-key";

        var first = await CreateStore()
            .TryClaimAsync(key, "bridge_redeem", CancellationToken.None);
        first.Won.Should().BeTrue();

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // token already cancelled when the duplicate arrives

        var second = await CreateStore()
            .TryClaimAsync(key, "bridge_redeem", cts.Token);

        second.Won.Should().BeFalse(
            "a duplicate must resolve to the idempotent replay even when the " +
            "request token is cancelled — never surface the raw DB error");
        second.Record.Should().NotBeNull();
        second.Record.Key.Should().Be(key);
        second.Record.State.Should().Be(IdempotencyState.InProgress);
    }

    // The VAA replay-ledger UNIQUE Digest constraint is real on the relational
    // engine — a second insert with the same Digest must raise DbUpdateException.
    [Fact]
    public async Task ConsumedVaaRecord_DuplicateDigest_SecondSave_ThrowsDbUpdateException()
    {
        const string digest = "0xdeadbeefcafef00d";

        await using (var ctx = CreateContext())
        {
            ctx.ConsumedVaas.Add(new ConsumedVaaRecord
            {
                Digest = digest,
                EmitterChainId = 1,
                EmitterAddress = "0xemitter",
                Sequence = 42,
                BridgeTransactionId = "bridge-1"
            });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = CreateContext())
        {
            ctx.ConsumedVaas.Add(new ConsumedVaaRecord
            {
                Digest = digest, // same digest ⇒ replay
                EmitterChainId = 1,
                EmitterAddress = "0xemitter",
                Sequence = 42,
                BridgeTransactionId = "bridge-2"
            });

            var act = async () => await ctx.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>();
        }

        // Exactly one row survived (replay rejected).
        await using (var ctx = CreateContext())
        {
            (await ctx.ConsumedVaas.CountAsync(v => v.Digest == digest))
                .Should().Be(1);
        }
    }

    /// <summary>
    /// A scope factory whose resolved <see cref="OASISDbContext"/> is wrapped
    /// so the IdempotencyRecord INSERT triggers a NON-unique persistence
    /// failure (a NOT NULL violation), proving <see cref="IdempotencyStore"/>
    /// rethrows genuine DB errors instead of masking them as Won=false.
    /// </summary>
    private sealed class NotNullViolatingScopeFactory : IServiceScopeFactory
    {
        // An "anchor" context kept alive for the test's lifetime so its SQLite
        // connection (bound to the shared in-memory DB with the SQLite model
        // mapping) stays OPEN. EF's UseSqlite(DbConnection) does NOT own an
        // externally-supplied connection, so the per-scope violating context
        // can dispose freely while the anchor still owns/closes the connection
        // at test teardown.
        private readonly OASISDbContext _anchor;

        public NotNullViolatingScopeFactory(SqliteTestContext shared, ConcurrentBag<IDisposable> disposables)
        {
            _anchor = shared.NewContext();
            _anchor.Database.OpenConnection(); // pin the connection open
            disposables.Add(_anchor);
        }

        public IServiceScope CreateScope() => new Scope(_anchor.Database.GetDbConnection());

        private sealed class Scope : IServiceScope, IServiceProvider
        {
            private readonly NotNullViolatingDbContext _ctx;
            public Scope(System.Data.Common.DbConnection conn) => _ctx = new NotNullViolatingDbContext(conn);
            public IServiceProvider ServiceProvider => this;
            public object? GetService(Type serviceType)
                => serviceType == typeof(OASISDbContext) ? _ctx : null;
            public void Dispose() => _ctx.Dispose();
        }
    }

    /// <summary>
    /// Forces a NOT NULL constraint violation on the next IdempotencyRecord
    /// insert by nulling the required <c>OperationType</c> after the store has
    /// added the entity but before SaveChanges runs. The resulting failure is
    /// a <see cref="DbUpdateException"/> whose inner SQLite error is a NOT NULL
    /// constraint failure (extended code 1299), NOT a unique/primary-key
    /// violation — so the store's unique-violation detection rejects it and
    /// must rethrow rather than swallow it as Won=false.
    /// </summary>
    private sealed class NotNullViolatingDbContext : OASISDbContext
    {
        // Bind to the SHARED open connection (UseSqlite(DbConnection) does not
        // own it) so the SQLite model mapping / shared DB is reused without
        // closing the anchor's connection.
        public NotNullViolatingDbContext(System.Data.Common.DbConnection sharedConnection)
            : base(new DbContextOptionsBuilder<OASISDbContext>()
                .UseSqlite(sharedConnection)
                .Options)
        {
        }

        // No model override needed: the vestigial xmin/Version concurrency
        // token was removed (api-safety-hardening §4 item 1) and the model now
        // maps cleanly on SQLite as-is — identical to SqliteTestDbContext.

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            // Null the NOT NULL OperationType on any pending IdempotencyRecord
            // insert ⇒ SQLite raises a NOT NULL CONSTRAINT error, which is a
            // DbUpdateException that is NOT a unique/primary-key violation.
            foreach (var entry in ChangeTracker.Entries<IdempotencyRecord>())
            {
                if (entry.State == EntityState.Added)
                    entry.Entity.OperationType = null!;
            }
            return base.SaveChangesAsync(cancellationToken);
        }
    }
}
