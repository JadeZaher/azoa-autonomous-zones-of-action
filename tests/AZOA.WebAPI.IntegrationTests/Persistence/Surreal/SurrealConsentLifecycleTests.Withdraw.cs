using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models;
using FluentAssertions;
using SurrealForge.Client.Query;
using Grant = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentGrant;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealConsentLifecycleTests
{
    [Fact]
    public async Task Withdrawal_competes_with_admission_and_returns_the_authoritative_winner()
    {
        var command = new ConsentLifecycleCommand(Guid.NewGuid(), _tenant, [AzoaScopes.KycRead], GrantOrigin.UserExplicit, null, null);
        var admission = _store.AdmitUserExplicitAsync(command, Authority());
        var withdrawal = _store.WithdrawAsync(command, Authority());
        await Task.WhenAll(admission, withdrawal);
        var outcome = (await withdrawal)!;
        outcome.Should().NotBeNull();
        var grants = await _executor.QueryAsync<Grant>(SurrealQuery<Grant>.From());
        if (outcome.Withdrawn)
        {
            (await admission).Should().BeNull(); grants.Should().BeEmpty(); outcome.Receipt.Should().BeNull();
        }
        else
        {
            grants.Should().ContainSingle(); outcome.Receipt!.GrantId.Should().Be((await admission)!.GrantId);
        }
        (await _store.WithdrawAsync(command, Authority())).Should().BeEquivalentTo(outcome);
        (await _store.WithdrawAsync(command with { Scopes = [AzoaScopes.KycSubmit] }, Authority())).Should().BeNull();
    }

    [Fact]
    public async Task Rejected_scope_and_expired_requests_can_be_withdrawn_without_grants()
    {
        foreach (var command in new[] {
            new ConsentLifecycleCommand(Guid.NewGuid(), _tenant, ["bogus:scope"], GrantOrigin.UserExplicit, null, null),
            new ConsentLifecycleCommand(Guid.NewGuid(), _tenant, [AzoaScopes.Operator], GrantOrigin.UserExplicit, null, null),
            new ConsentLifecycleCommand(Guid.NewGuid(), _tenant, [AzoaScopes.KycRead], GrantOrigin.UserExplicit, null, DateTimeOffset.UtcNow.AddDays(-1)) })
        {
            (await _store.AdmitUserExplicitAsync(command, Authority())).Should().BeNull();
            (await _store.WithdrawAsync(command, Authority()))!.Withdrawn.Should().BeTrue();
            (await _store.AdmitUserExplicitAsync(command, Authority())).Should().BeNull();
        }
        (await _executor.QueryAsync<Grant>(SurrealQuery<Grant>.From())).Should().BeEmpty();
    }
}
