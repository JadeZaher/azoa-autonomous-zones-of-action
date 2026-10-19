using AZOA.WebAPI.Core;
using AZOA.WebAPI.IntegrationTests.Persistence.Surreal.Round4Gates.Interleaving;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Kyc;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>Real-DB hosted binding preserves provider representations and exact lease/attempt authority.</summary>
public sealed class SurrealKycIdentityStoreTests : IAsyncLifetime
{
    private InterleavingDatabase _database = null!;
    private SurrealKycIdentityStore _store = null!;

    public async Task InitializeAsync()
    {
        _database = await InterleavingDatabase.CreateAsync("user", "kyc_submission", "hosted_session", "kyc_identity", "kyc_authority_guard");
        _store = new(_database.Executor);
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_lease_binds_lowercase_provider_key_and_uppercase_provider_enum(bool tenantScoped)
    {
        var (candidate, expected, lease) = await SeedAsync(tenantScoped);

        var result = await _store.BindVerifiedUserAsync(candidate, expected, lease);

        result.IsError.Should().BeFalse("the exact live lease and active attempt authorize binding; code={0}", result.Code);
        result.Result.Should().NotBeNull();
        var saved = (await _database.Executor.QuerySingleAsync<KycIdentity>(SurrealQuery<KycIdentity>.Key(candidate.Id)))!;
        saved.ProviderKey.Should().Be("identomat");
        saved.Provider.Should().Be(KycProvider.IDENTOMAT);
        saved.UserIds.Should().Equal(expected.UserId!);
        saved.TenantId.Should().Be(SurrealLink.ToLink("user", expected.TenantId));
        saved.FirstName.Should().BeNull();
        saved.LastName.Should().BeNull();
        saved.Birthday.Should().BeNull();
        saved.Citizenship.Should().BeNull();
        saved.RetentionExpiresAt.Should().BeNull();
        var attempt = (await _database.Executor.QuerySingleAsync<KycSubmission>(SurrealQuery<KycSubmission>.Key(expected.Id)))!;
        attempt.Status.Should().Be(KycStatus.PENDING, "binding alone never approves an attempt");
        attempt.ProviderSelectionVersion.Should().Be(expected.ProviderSelectionVersion);
        attempt.ProviderTrustRevision.Should().Be(expected.ProviderTrustRevision);
    }

    [Theory]
    [InlineData("lease")]
    [InlineData("provider")]
    [InlineData("selection")]
    [InlineData("trust")]
    public async Task Changed_lease_or_attempt_authority_refuses_binding_and_rolls_back_the_evidence_guard(string changed)
    {
        var (candidate, expected, lease) = await SeedAsync(tenantScoped: true);
        switch (changed)
        {
            case "lease": lease.ResumeLeaseToken = Guid.NewGuid().ToString("N"); break;
            case "provider": expected.ProviderKey = candidate.ProviderKey = "another-provider"; break;
            case "selection": expected.ProviderSelectionVersion++; break;
            case "trust": expected.ProviderTrustRevision++; break;
        }

        var result = await _store.BindVerifiedUserAsync(candidate, expected, lease);

        result.IsError.Should().BeTrue();
        result.Code.Should().Be(AzoaErrorCodes.Conflict);
        (await _database.Executor.QueryAsync<KycIdentity>(SurrealQuery<KycIdentity>.From())).Should().BeEmpty();
        (await _database.Executor.QueryAsync<KycAuthorityGuard>(SurrealQuery<KycAuthorityGuard>.From())).Should().BeEmpty();
    }

    private async Task<(KycIdentity Candidate, KycSubmission Expected, HostedSession Lease)> SeedAsync(bool tenantScoped)
    {
        var now = DateTimeOffset.UtcNow;
        var user = Guid.NewGuid().ToString("N");
        var tenant = tenantScoped ? Guid.NewGuid().ToString("N") : null;
        foreach (var id in new[] { user, tenant }.OfType<string>())
            (await _database.Executor.ExecuteAsync(SurrealWriter.Create(new User
            {
                Id = id, Username = id, Email = id + "@example.test", PasswordHash = "fixture-only", IsActive = true,
            }))).EnsureAllOk();
        var lease = new HostedSession
        {
            Id = Guid.NewGuid().ToString("N"), FlowKind = HostedSessionFlowKinds.Kyc, ProviderKey = "identomat",
            AdmissionKey = Guid.NewGuid().ToString("N"), State = HostedSessionStates.Resuming,
            SubjectUserId = SurrealLink.ToLink("user", user), TenantId = SurrealLink.ToLink("user", tenant),
            ResumeLeaseToken = Guid.NewGuid().ToString("N"), ResumeLeaseExpiresAt = now.AddMinutes(10), CreatedDate = now,
        };
        var expected = new KycSubmission
        {
            Id = Guid.NewGuid().ToString("N"), UserId = SurrealLink.ToLink("user", user), TenantId = SurrealLink.ToLink("user", tenant),
            Provider = KycProvider.IDENTOMAT, ProviderKey = "identomat", ProviderSessionId = lease.Id,
            Status = KycStatus.PENDING, ExpiresAt = now.AddMinutes(10), ProviderSelectionVersion = 3, ProviderTrustRevision = 7,
            SubmittedAt = now, CreatedDate = now,
        };
        (await _database.Executor.ExecuteAsync(SurrealWriter.Create(lease))).EnsureAllOk();
        (await _database.Executor.ExecuteAsync(SurrealWriter.Create(expected))).EnsureAllOk();
        expected.UserId = user;
        expected.TenantId = tenant;
        var candidate = new KycIdentity
        {
            Id = Guid.NewGuid().ToString("N"), TenantScope = tenant ?? KycPersonKeyDeriver.GlobalScopeSentinel, TenantId = tenant,
            PersonKey = Guid.NewGuid().ToString("N"), GlobalPersonKey = Guid.NewGuid().ToString("N"),
            UserIds = [user], Provider = KycProvider.IDENTOMAT, ProviderKey = "identomat",
        };
        return (candidate, expected, lease);
    }
}
