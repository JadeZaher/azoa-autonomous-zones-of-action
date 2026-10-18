using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal.Round4Gates.Interleaving;

/// <summary>Concurrent KYC begin for one subject scope admits exactly one active attempt; see AGENTS.md §KYC begin.</summary>
public sealed class KycBeginInterleavingTests : IAsyncLifetime
{
    private const int Racers = 4;
    private readonly Guid _subject = Guid.NewGuid();
    private readonly Guid _tenant = Guid.NewGuid();
    private InterleavingDatabase _database = null!;
    private SurrealKycStore _store = null!;

    public async Task InitializeAsync()
    {
        _database = await InterleavingDatabase.CreateAsync("kyc_submission", "kyc_document", "kyc_provider_profile",
            "tenant_kyc_provider_selection");
        _store = new SurrealKycStore(_database.Executor);
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentBeginsForOneSubjectScopeAdmitOneAttemptAndOnlyItsDocuments(bool tenantScoped)
    {
        Guid? tenant = tenantScoped ? _tenant : null;
        if (tenantScoped) await SeedTenantProviderAsync();
        var attempts = Enumerable.Range(0, Racers).Select(_ => Attempt(tenant)).ToArray();

        var results = await Task.WhenAll(attempts.Select(attempt =>
            _store.CreateSubmissionAsync(attempt, [Document(attempt.Id)]))).WaitAsync(TimeSpan.FromSeconds(30));

        results.Where(result => !result.IsError).Should().ContainSingle("the attempt guard serializes one subject scope");
        results.Where(result => result.IsError).Should().OnlyContain(result => result.Code == AzoaErrorCodes.Conflict,
            "a loser is told the attempt changed, never a store fault");
        var winner = attempts[Array.FindIndex(results, result => !result.IsError)];
        var active = (await _store.GetActiveSubmissionAsync(_subject, tenant)).Result;
        active!.Id.Should().Be(winner.Id, "the loser re-reads the winner as the subject's one active attempt");
        (await _database.Executor.QueryAsync<KycSubmission>(SurrealQuery<KycSubmission>.From())).Should().ContainSingle();
        foreach (var attempt in attempts)
            (await _store.GetDocumentsBySubmissionAsync(Guid.ParseExact(attempt.Id, "N"))).Result
                .Should().HaveCount(attempt.Id == winner.Id ? 1 : 0, "a losing attempt's documents roll back with it");
    }

    private KycSubmission Attempt(Guid? tenant)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        return new KycSubmission
        {
            Id = id, UserId = _subject.ToString("N"), TenantId = tenant?.ToString("N"), Provider = KycProvider.MANUAL,
            ProviderKey = "manual", ProviderSelectionVersion = tenant is null ? 0 : 1, ProviderTrustRevision = tenant is null ? 0 : 1,
            ProviderSessionId = id, Status = KycStatus.PENDING, SubmittedAt = now, CreatedDate = now, ModifiedDate = now,
            ExpiresAt = now.AddHours(1),
        };
    }

    private static KycDocument Document(string submissionId) => new()
    {
        Id = Guid.NewGuid().ToString("N"), SubmissionId = submissionId, Type = KycDocumentType.PASSPORT,
        FileName = "passport.pdf", FileUrl = "https://documents.example/passport", CreatedDate = DateTimeOffset.UtcNow,
    };

    private async Task SeedTenantProviderAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var tenantLink = "user:" + _tenant.ToString("N");
        (await _database.Executor.ExecuteAsync(SurrealWriter.Create(new KycProviderProfile { Id = "manual", DisplayName = "Manual",
            AdapterKey = "manual", Enabled = true, PolicyVersion = "v1", AssuranceLevel = "manual", TrustRevision = 1,
            UpdatedByUserId = tenantLink, UpdatedAt = now }))).EnsureAllOk();
        (await _database.Executor.ExecuteAsync(SurrealWriter.Create(new TenantKycProviderSelection { Id = _tenant.ToString("N"),
            TenantId = tenantLink, ProviderKey = "manual", SelectionVersion = 1, UpdatedByUserId = tenantLink, UpdatedAt = now })))
            .EnsureAllOk();
    }
}
