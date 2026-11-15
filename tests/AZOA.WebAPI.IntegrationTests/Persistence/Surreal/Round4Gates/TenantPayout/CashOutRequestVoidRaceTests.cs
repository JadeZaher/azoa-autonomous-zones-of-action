using AZOA.WebAPI.IntegrationTests.Persistence.Surreal.Round4Gates.Interleaving;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Fiat.OffRamp;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal.Round4Gates.TenantPayout;

public sealed class CashOutRequestVoidRaceTests : IAsyncLifetime
{
    private InterleavingDatabase _database = null!;
    private SurrealFiatPayoutLedger _ledger = null!;
    private readonly Guid _subject = Guid.NewGuid();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        _database = await InterleavingDatabase.CreateAsync("fiat_payout", "fiat_payout_request_fence");
        _ledger = new SurrealFiatPayoutLedger(_database.Executor);
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task Claim_racing_void_never_returns_absence_proof_for_an_admitted_payout()
    {
        for (var index = 0; index < 16; index++)
        {
            var payout = Payout("race-" + index);
            var claimTask = _ledger.ClaimAsync(payout);
            var voidTask = _ledger.VoidAsync(Fence(payout));
            await Task.WhenAll(claimTask, voidTask);
            var claim = await claimTask;
            var resolution = (await voidTask).Result!;
            resolution.Should().NotBeNull();
            if (resolution.Voided)
            {
                claim.IsError.Should().BeTrue();
                claim.Code.Should().Be("REQUEST_VOIDED");
                (await _ledger.GetAsync(payout.Id)).Result.Should().BeNull();
                (await _ledger.ClaimAsync(payout)).Code.Should().Be("REQUEST_VOIDED", "a delayed request cannot newly admit after void");
            }
            else
            {
                claim.IsError.Should().BeFalse();
                resolution.Row!.Id.Should().Be(payout.Id);
                resolution.Row.Status.Should().Be(FiatPayoutStatuses.Requested, "void never aborts a saga that may collect next");
            }
        }
    }

    [Fact]
    public async Task Lost_void_response_replays_the_same_permanent_tombstone()
    {
        var payout = Payout("lost-void");
        (await _ledger.VoidAsync(Fence(payout))).Result!.Voided.Should().BeTrue();
        (await _ledger.VoidAsync(Fence(payout))).Result!.Voided.Should().BeTrue();
        (await _ledger.ClaimAsync(payout)).Code.Should().Be("REQUEST_VOIDED");
        (await _ledger.GetAsync(payout.Id)).Result.Should().BeNull();
    }

    [Fact]
    public async Task A_different_funding_driver_cannot_reveal_or_void_another_drivers_key()
    {
        var payout = Payout("other-driver");
        await _ledger.ClaimAsync(payout);
        var other = Fence(payout);
        other.TenantId = SurrealLink.ToLink("user", Guid.NewGuid().ToString("N"));
        (await _ledger.VoidAsync(other)).Code.Should().Be("REQUEST_KEY_IN_USE");
        other.TenantId = null;
        (await _ledger.VoidAsync(other)).Code.Should().Be("REQUEST_KEY_IN_USE", "first-party reuse cannot reveal a tenant-driven key either");
        var absent = Payout("other-driver-absent");
        await _ledger.VoidAsync(Fence(absent));
        var otherAbsent = Fence(absent);
        otherAbsent.TenantId = other.TenantId;
        (await _ledger.VoidAsync(otherAbsent)).Code.Should().Be("REQUEST_KEY_IN_USE");
    }

    [Fact]
    public async Task Legacy_admitted_rows_without_a_fence_are_revealed_unchanged()
    {
        var payout = Payout("legacy");
        (await _database.Executor.ExecuteAsync(SurrealWriter.Create(payout))).EnsureAllOk();
        var revealed = (await _ledger.VoidAsync(Fence(payout))).Result!;
        revealed.Voided.Should().BeFalse();
        revealed.Row!.Status.Should().Be(FiatPayoutStatuses.Requested);
        (await _ledger.GetAsync(payout.Id)).Result!.Version.Should().Be(0);
    }

    private FiatPayout Payout(string key) => new()
    {
        Id = FiatPayout.RecordIdFor(_subject, key), SubjectUserId = SurrealLink.ToLink("user", _subject.ToString("N"))!,
        TenantId = SurrealLink.ToLink("user", _tenant.ToString("N")), RequestKey = key,
        ChainType = "Algorand", AssetId = "1", QuantityBaseUnits = "50000", AmountMinorUnits = 500,
        CurrencyCode = "USD", UnitsPerMinorUnit = "100", Status = FiatPayoutStatuses.Requested,
        Environment = "test", ChainNetwork = "Devnet", RequestedAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static FiatPayoutRequestFence Fence(FiatPayout payout) => new()
    {
        Id = payout.Id, SubjectUserId = payout.SubjectUserId, TenantId = payout.TenantId,
        RequestKey = payout.RequestKey, Environment = payout.Environment, ChainNetwork = payout.ChainNetwork,
        Verdict = "voided", CreatedAt = DateTimeOffset.UtcNow,
    };
}
