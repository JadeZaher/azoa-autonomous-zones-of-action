using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealPersonOperationClaimTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private readonly string _scope = Guid.NewGuid().ToString("N");
    private readonly string _operation = "allocation:" + Guid.NewGuid().ToString("N");
    private ISurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;
    private SurrealPersonOperationClaimStore _store = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "person_operation_claim", "user");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions { Endpoint = SurrealTestDefaults.Endpoint,
            Namespace = _namespace, Database = "test", User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password });
        _executor = new DefaultSurrealExecutor(_connection);
        _store = new(_executor);
    }

    public async Task DisposeAsync() { _connection.Dispose(); await SurrealTestSchema.DropAsync(_namespace); }

    [Theory]
    [InlineData("scope")]
    [InlineData("operation")]
    [InlineData("person")]
    public async Task Missing_domain_parts_return_validation_failure_without_claiming(string missing)
    {
        var candidate = Candidate("first");
        if (missing == "scope") candidate.UniquenessScope = " ";
        if (missing == "operation") candidate.OperationKey = null!;
        if (missing == "person") candidate.PersonKey = string.Empty;
        (await _store.TryClaimAsync(candidate)).IsError.Should().BeTrue();
        (await _store.ReleaseAsync(candidate.UniquenessScope, candidate.OperationKey, candidate.PersonKey, "first", DateTimeOffset.UtcNow)).IsError.Should().BeTrue();
        (await _store.GetAsync(candidate.UniquenessScope, candidate.OperationKey, candidate.PersonKey)).IsError.Should().BeTrue();
        (await Read()).Should().BeNull();
    }

    [Fact]
    public async Task Concurrent_first_claims_return_one_incumbent_without_overwriting_its_tenant_or_decision()
    {
        var candidates = Enumerable.Range(0, 8).Select(index => Candidate(Guid.NewGuid().ToString("N"), "option-" + index)).ToArray();
        foreach (var row in candidates) row.TenantId = Guid.NewGuid().ToString("N");
        var results = await Task.WhenAll(candidates.Select(candidate => _store.TryClaimAsync(candidate)));
        results.Should().OnlyContain(result => !result.IsError);
        var saved = await Read();
        var winner = candidates.Single(candidate => candidate.UserId == saved!.UserId);
        results.Should().OnlyContain(result => result.Result!.UserId == winner.UserId);
        saved!.DecisionOption.Should().Be(winner.DecisionOption);
        SurrealLink.FromLink(saved.TenantId).Should().Be(winner.TenantId);
        saved.Version.Should().Be(1);
    }

    [Fact]
    public async Task Same_holder_revote_preserves_claim_time_and_first_cutoff()
    {
        var first = Candidate("first", "yes");
        first.ModifiedDate = DateTimeOffset.UtcNow.AddMinutes(-2);
        (await _store.TryClaimAsync(first)).IsError.Should().BeFalse();
        var before = await Read();
        var changed = Candidate("first", "no");
        changed.DecisionClosesAt = first.DecisionClosesAt!.Value.AddDays(1);
        (await _store.TryClaimAsync(changed)).Result!.DecisionOption.Should().Be("no");
        var after = await Read();
        after!.ClaimedAt.Should().Be(before!.ClaimedAt);
        after.DecisionClosesAt.Should().Be(before.DecisionClosesAt);
        after.Version.Should().Be(2);
        (await _store.GetAsync(_scope, _operation, "person")).Result!.Version.Should().Be(2);
    }

    [Fact]
    public async Task Release_and_no_decision_reclaim_preserve_cutoff_and_prior_decision()
    {
        var first = Candidate("first", "yes");
        await _store.TryClaimAsync(first);
        (await _store.ReleaseAsync(_scope, _operation, "person", "wrong", DateTimeOffset.UtcNow)).Result.Should().BeFalse();
        (await _store.ReleaseAsync(_scope, _operation, "person", "first", DateTimeOffset.UtcNow)).Result.Should().BeTrue();
        (await _store.ReleaseAsync(_scope, _operation, "person", "first", DateTimeOffset.UtcNow)).Result.Should().BeFalse();
        var claimant = Candidate("second");
        var result = await _store.TryClaimAsync(claimant);
        result.IsError.Should().BeFalse();
        result.Result!.UserId.Should().Be("second");
        result.Result.DecisionOption.Should().Be("yes");
        result.Result.DecisionClosesAt.Should().Be(first.DecisionClosesAt);
        result.Result.ReleasedAt.Should().BeNull();
        result.Result.TenantId.Should().BeNull();
        result.Result.Version.Should().Be(3);
    }

    [Fact]
    public async Task Database_cutoff_refuses_extension_and_release_even_with_old_application_timestamp()
    {
        var closed = Candidate("first", "yes");
        closed.Version = 4;
        closed.DecisionClosesAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await Execute(SurrealWriter.Create(closed));
        var candidate = Candidate("first", "no");
        candidate.ModifiedDate = DateTimeOffset.UtcNow.AddHours(-1);
        var refused = await _store.TryClaimAsync(candidate);
        refused.Code.Should().Be(PersonOperationClaimFence.Closed);
        (await _store.ReleaseAsync(_scope, _operation, "person", "first", candidate.ModifiedDate.Value)).Result.Should().BeFalse();
        (await Read())!.Version.Should().Be(4);
        (await Read())!.DecisionOption.Should().Be("yes");
    }

    [Fact]
    public async Task Expired_first_decision_does_not_create_a_claim()
    {
        var candidate = Candidate("first", "yes");
        candidate.DecisionClosesAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        (await _store.TryClaimAsync(candidate)).Code.Should().Be(PersonOperationClaimFence.Closed);
        (await Read()).Should().BeNull();
    }

    [Fact]
    public async Task Legacy_absent_version_is_adopted_without_replacing_incumbent_identity()
    {
        var legacy = Candidate("first");
        legacy.ClaimedAt = DateTimeOffset.UtcNow.AddDays(-1);
        await Execute(SurrealWriter.Create(legacy));
        var result = await _store.TryClaimAsync(Candidate("first", "yes"));
        result.IsError.Should().BeFalse();
        result.Result!.Version.Should().Be(1);
        result.Result.ClaimedAt.Should().Be(legacy.ClaimedAt);
        result.Result.DecisionOption.Should().Be("yes");
    }

    [Fact]
    public async Task Literal_null_version_is_not_adopted_as_absent_legacy_state()
    {
        await SurrealTestSchema.BootstrapWithExtraAsync(_namespace,
            "DEFINE FIELD OVERWRITE version ON TABLE person_operation_claim TYPE option<int | null>;", "person_operation_claim");
        var legacy = Candidate("first");
        await Execute(SurrealWriter.Create(legacy));
        await Execute(SurrealQuery.Of("UPDATE ONLY type::record('person_operation_claim', $_id) SET version = NULL").WithParam("_id", legacy.Id));
        (await _store.TryClaimAsync(Candidate("first", "yes"))).Code.Should().Be(PersonOperationClaimFence.Invalid);
        (await _store.ReleaseAsync(_scope, _operation, "person", "first", DateTimeOffset.UtcNow)).Code.Should().Be(PersonOperationClaimFence.Invalid);
        (await Read())!.DecisionOption.Should().BeNull();
    }

    [Fact]
    public async Task Stale_snapshot_guard_refuses_mutation_and_current_retry_succeeds()
    {
        await _store.TryClaimAsync(Candidate("first", "yes"));
        var stale = (await Read())!;
        await _store.TryClaimAsync(Candidate("first", "no"));
        var guard = PersonOperationClaimFence.Create(stale, stale, false);
        var mutation = PersonOperationClaimFence.Update(stale).Set(row => row.DecisionOption, "stale").Build();
        var attempt = async () => await Execute(SurrealQuery.Combine([SurrealQuery.Of("BEGIN"), ..guard, mutation, SurrealQuery.Of("COMMIT")]));
        await attempt.Should().ThrowAsync<SurrealStatementException>().WithMessage("*PERSON_CLAIM_STATE_CHANGED*");
        (await Read())!.DecisionOption.Should().Be("no");
        (await _store.TryClaimAsync(Candidate("first", "yes"))).Result!.Version.Should().Be(3);
    }

    [Fact]
    public async Task Concurrent_release_and_reclaim_never_releases_the_new_holder()
    {
        await _store.TryClaimAsync(Candidate("first"));
        var release = _store.ReleaseAsync(_scope, _operation, "person", "first", DateTimeOffset.UtcNow);
        var claim = _store.TryClaimAsync(Candidate("second"));
        await Task.WhenAll(release, claim);
        release.Result.IsError.Should().BeFalse();
        claim.Result.IsError.Should().BeFalse();
        var settled = await _store.TryClaimAsync(Candidate("second"));
        settled.Result!.UserId.Should().Be("second");
        (await _store.ReleaseAsync(_scope, _operation, "person", "first", DateTimeOffset.UtcNow)).Result.Should().BeFalse();
        (await Read())!.ReleasedAt.Should().BeNull();
    }

    [Fact]
    public async Task No_decision_claim_does_not_pin_close_until_first_decision_and_never_close_sentinel_projects_null()
    {
        var candidate = Candidate("first");
        candidate.DecisionClosesAt = DateTimeOffset.UtcNow.AddDays(1);
        await _store.TryClaimAsync(candidate);
        (await Read())!.DecisionClosesAt.Should().BeNull();
        var decision = Candidate("first", "yes");
        decision.DecisionClosesAt = null;
        var saved = await _store.TryClaimAsync(decision);
        saved.Result!.DecisionClosesAt.Should().BeNull();
        PersonOperationClaim.IsNeverCloses((await Read())!.DecisionClosesAt).Should().BeTrue();
    }

    [Fact]
    public async Task Empty_CAS_responses_never_report_claim_or_release_success()
    {
        await _store.TryClaimAsync(Candidate("first", "yes"));
        var executor = new EmptyMutationExecutor(_executor);
        var store = new SurrealPersonOperationClaimStore(executor);
        (await store.TryClaimAsync(Candidate("first", "no"))).Code.Should().Be("Conflict");
        (await store.ReleaseAsync(_scope, _operation, "person", "first", DateTimeOffset.UtcNow)).Code.Should().Be("Conflict");
        executor.Attempts.Should().Be(16);
        (await Read())!.DecisionOption.Should().Be("yes");
        (await Read())!.ReleasedAt.Should().BeNull();
    }

    private sealed class EmptyMutationExecutor(ISurrealExecutor inner) : ISurrealExecutor
    {
        public int Attempts { get; private set; }
        public Task<IReadOnlyList<T>> QueryAsync<T>(SurrealQuery query, CancellationToken ct = default) => inner.QueryAsync<T>(query, ct);
        public Task<T?> QuerySingleAsync<T>(SurrealQuery query, CancellationToken ct = default) where T : class => inner.QuerySingleAsync<T>(query, ct);
        public Task<SurrealResponse> ExecuteAsync(SurrealQuery query, CancellationToken ct = default)
        {
            Attempts++;
            return inner.ExecuteAsync(SurrealQuery.Combine(Enumerable.Range(0, 32).Select(_ => SurrealQuery.Of("RETURN NONE")).ToArray()), ct);
        }
    }

    private PersonOperationClaim Candidate(string holder, string? decision = null) => new()
    {
        Id = PersonOperationClaim.RecordIdFor(_scope, _operation, "person"), UniquenessScope = _scope,
        OperationKey = _operation, PersonKey = "person", UserId = holder, DecisionOption = decision,
        DecisionClosesAt = decision is null ? null : DateTimeOffset.UtcNow.AddHours(1)
    };
    private Task<PersonOperationClaim?> Read() => _executor.QuerySingleAsync<PersonOperationClaim>(
        SurrealQuery<PersonOperationClaim>.Key(PersonOperationClaim.RecordIdFor(_scope, _operation, "person")));
    private async Task Execute(SurrealQuery query) { var result = await _executor.ExecuteAsync(query); result.EnsureAllOk(); }
}
