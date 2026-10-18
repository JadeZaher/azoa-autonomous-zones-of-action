using System.Text.Json;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using FluentAssertions;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;
using RunRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionRun;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>
/// Two-transaction races the governance runtime verdict depends on
/// (conductor/tracks/platform-stub-closeout_20261015/round4-governance-runtime-verdict.md).
/// Each asserts the outcome of every serial order, never which order won.
/// </summary>
public sealed partial class SurrealGovernanceTests
{
    [Fact]
    public async Task Concurrent_cancellation_and_vote_commit_in_one_serial_order()
    {
        await SeedProposal();
        var receipt = await Command(_voter);

        var vote = _store.VoteAsync(_proposal, Claim("yes"), _approval, _identity, receipt, default);
        var cancel = Contended(CancelRun(receipt.RunId));
        await Task.WhenAll(vote, cancel);
        var refused = (await vote).IsError;

        (await _executor.QuerySingleAsync<RunRow>(SurrealQuery<RunRow>.Key(receipt.RunId)))!.Status
            .Should().Be(RunRow.ActionRunStatusKind.Cancelled);
        await ShouldBeAllOrNothing(refused, receipt);
        // A committed vote survives the cancellation as a replay; a refused one can never commit later.
        (await _store.VoteAsync(_proposal, Claim("yes"), _approval, _identity, receipt, default)).IsError
            .Should().Be(refused);
        await ShouldBeAllOrNothing(refused, receipt);
    }

    [Fact]
    public async Task Concurrent_cancellation_and_creation_commit_in_one_serial_order()
    {
        var receipt = await Command(_tenant);

        var create = _store.CreateAsync(_proposal, Projection(), receipt, default);
        var cancel = Contended(CancelRun(receipt.RunId));
        await Task.WhenAll(create, cancel);
        var refused = (await create).IsError;

        var proposal = await _store.GetAsync(_proposal.Id, default);
        var saved = await _store.ReceiptAsync(receipt.Id, default);
        (proposal is null).Should().Be(saved is null, "the proposal, its projection and the receipt are one admission");
        (proposal is null).Should().Be(refused);
        (await _store.CreateAsync(_proposal, Projection(), receipt, default)).IsError.Should().Be(refused,
            "after cancellation only the already-committed receipt can be replayed");
    }

    [Fact]
    public async Task Concurrent_participation_revocation_and_vote_commit_in_one_serial_order()
    {
        await SeedProposal();
        await Execute(SurrealWriter.UpdateOnly<User>(_voter).Where(seed => true).Unset(row => row.OwnerTenantId).Build());
        await Save(new ConsentGrant { Id = "participation", GrantorUserId = "user:" + _voter, TenantId = "user:" + _tenant,
            Origin = "Participation", ParticipationRef = "project", Scopes = "wallet:read", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        var receipt = await Command(_voter);

        var vote = _store.VoteAsync(_proposal, Claim("yes"), _approval, _identity, receipt, default);
        var revoke = Contended(SurrealWriter.UpdateOnly<ConsentGrant>("participation").Where(seed => true)
            .Set(row => row.RevokedAt, DateTimeOffset.UtcNow).Build());
        await Task.WhenAll(vote, revoke);
        var refused = (await vote).IsError;

        await ShouldBeAllOrNothing(refused, receipt);
        (await _store.VoteAsync(_proposal, Claim("no"), _approval, _identity, await Command(_voter), default)).IsError
            .Should().BeTrue("membership ended with the revocation, whichever transaction committed first");
        (await _store.DecisionsAsync(_proposal, default)).Should().OnlyContain(decision => decision.DecisionOption == "yes");
    }

    [Fact]
    public async Task Vote_and_finalization_racing_after_cutoff_leave_the_tally_equal_to_the_ledger()
    {
        _proposal.ClosesAt = DateTimeOffset.UtcNow.AddSeconds(2);
        await SeedProposal();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var voting = Task.Run(async () =>
        {
            var option = "yes";
            while (!stop.IsCancellationRequested)
            {
                await _store.VoteAsync(_proposal, Claim(option), _approval, _identity, await Command(_voter), default);
                option = option == "yes" ? "no" : "yes";
                await Task.Delay(50);
            }
        });
        GovernanceCommandReceipt? finalized = null;
        while (finalized is null && !stop.IsCancellationRequested)
        {
            var decisions = await _store.DecisionsAsync(_proposal, default);
            var counts = new Dictionary<string, int>
            {
                ["yes"] = decisions.Count(decision => decision.DecisionOption == "yes"),
                ["no"] = decisions.Count(decision => decision.DecisionOption == "no"),
            };
            var attempt = await Command(_tenant);
            attempt.OutputJson = JsonSerializer.Serialize(new { counts, totalVotes = counts.Values.Sum() });
            var result = await _store.FinalizeAsync(_proposal, counts, attempt, default);
            if (!result.IsError) finalized = attempt;
            else await Task.Delay(100);
        }
        await stop.CancelAsync();
        await voting;

        finalized.Should().NotBeNull("finalization must succeed once the database clock passes the cutoff");
        var tally = JsonSerializer.Deserialize<JsonElement>(finalized!.OutputJson).GetProperty("counts");
        var ledger = await _store.DecisionsAsync(_proposal, default);
        foreach (var option in new[] { "yes", "no" })
            tally.GetProperty(option).GetInt32().Should().Be(ledger.Count(decision => decision.DecisionOption == option),
                "no vote can commit between the finalizer's in-transaction ledger check and its snapshot");
        (await _store.VoteAsync(_proposal, Claim("yes"), _approval, _identity, await Command(_voter), default)).IsError.Should().BeTrue();
        (await _store.GetAsync(_proposal.Id, default))!.FinalResultJson.Should().Be(finalized.OutputJson);
    }

    [Fact]
    public async Task Membership_change_during_creation_refuses_a_stale_electorate()
    {
        var receipt = await Command(_tenant);

        var create = _store.CreateAsync(_proposal, Projection(), receipt, default);
        var offboard = Contended(SurrealWriter.UpdateOnly<User>(_voter).Where(seed => true).Unset(row => row.OwnerTenantId).Build());
        await Task.WhenAll(create, offboard);
        var refused = (await create).IsError;

        var proposal = await _store.GetAsync(_proposal.Id, default);
        (proposal is null).Should().Be(refused);
        if (proposal is not null)
        {
            JsonSerializer.Deserialize<string[]>(proposal.ElectorateJson).Should().Contain(_voter,
                "a committed creation froze the electorate it observed");
            (await _store.VoteAsync(proposal, Claim("yes"), _approval, _identity, await Command(_voter), default)).IsError
                .Should().BeTrue("a frozen electorate entry is not live membership");
        }
        else (await _store.ReceiptAsync(receipt.Id, default)).Should().BeNull();
    }

    private async Task ShouldBeAllOrNothing(bool refused, GovernanceCommandReceipt receipt)
    {
        var decisions = await _store.DecisionsAsync(_proposal, default);
        var saved = await _store.ReceiptAsync(receipt.Id, default);
        if (refused)
        {
            decisions.Should().BeEmpty();
            saved.Should().BeNull();
        }
        else
        {
            decisions.Should().ContainSingle();
            saved.Should().NotBeNull();
        }
    }

    private static SurrealQuery CancelRun(string runId) => SurrealWriter.UpdateOnly<RunRow>(runId)
        .Where(row => row.Status == RunRow.ActionRunStatusKind.Running)
        .Set(row => row.Status, RunRow.ActionRunStatusKind.Cancelled).Build();

    /// <summary>The competing writer retries a transient 3.x conflict exactly as production writers do.</summary>
    private Task Contended(SurrealQuery statement) => SurrealTransientConflict.RetryOnConflictAsync(async () =>
    {
        var result = await _executor.ExecuteAsync(statement);
        result.EnsureAllOk();
        return result;
    }, default);
}
