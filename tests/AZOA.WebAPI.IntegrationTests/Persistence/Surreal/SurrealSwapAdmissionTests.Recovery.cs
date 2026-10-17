using AZOA.WebAPI.Helpers;
using FluentAssertions;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealSwapAdmissionTests
{
    [Fact]
    public async Task RecoveryClaimsAdvanceBeforeProviderFailureAndDoNotStarveRowsBeyondTheBatch()
    {
        var (quote, terms, _) = await SeedAsync();
        for (var index = 0; index < 55; index++)
        {
            var operation = Operation(quote, terms, "recovery-fairness-" + index);
            operation.State = "NeedsOperator";
            operation.NextAttemptAt = DateTimeOffset.UtcNow.AddHours(-2).AddSeconds(index);
            (await _executor.ExecuteAsync(SurrealWriter.Create(operation))).EnsureAllOk();
        }
        var first = await _store.ReadRecoveryDueAsync();
        first.Should().HaveCount(50);
        foreach (var operation in first)
        {
            var claimed = await _store.ClaimRecoveryAsync(SurrealRecordGuid.BareId(operation.Id), operation.Version);
            claimed.Should().NotBeNull();
            claimed!.NextAttemptAt.Should().BeAfter(DateTimeOffset.UtcNow);
        }
        var remaining = await _store.ReadRecoveryDueAsync();
        remaining.Should().HaveCount(5);
        remaining.Select(row => row.Id).Should().NotIntersectWith(first.Select(row => row.Id));
        var contender = remaining[0];
        var claims = await Task.WhenAll(
            _store.ClaimRecoveryAsync(SurrealRecordGuid.BareId(contender.Id), contender.Version),
            _store.ClaimRecoveryAsync(SurrealRecordGuid.BareId(contender.Id), contender.Version));
        claims.Where(row => row is not null).Should().ContainSingle();
    }

    [Fact]
    public async Task HistoryCursorUsesTheOperationEpochAndClearsOnlyAtTheEndOfAPass()
    {
        var (quote, terms, authority) = await SeedAsync();
        var operation = (await _store.AdmitAsync(Operation(quote, terms, "recovery-cursor"), authority))!;
        var id = SurrealRecordGuid.BareId(operation.Id);
        var cursor = new string('a', 64);
        (await _store.SaveRecoveryCursorAsync(id, operation.Version, cursor)).Should().BeTrue();
        (await _store.SaveRecoveryCursorAsync(id, operation.Version, null)).Should().BeFalse();
        var advanced = (await _store.GetAsync(id))!;
        advanced.RecoveryHistoryCursor.Should().Be(cursor);
        (await _store.SaveRecoveryCursorAsync(id, advanced.Version, null)).Should().BeTrue();
        (await _store.GetAsync(id))!.RecoveryHistoryCursor.Should().BeNull();
    }
}
