using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Fiat;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>
/// The request-key void's fence on the real store (Providers/Fiat/AGENTS.md §request-void): the
/// tombstone is the admission row, and the void's abandon CAS and begin's attach CAS exclude each other.
/// </summary>
public sealed class SurrealHostedSessionVoidTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private ISurrealConnection _connection = null!;
    private SurrealHostedSessionStore _store = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "hosted_session", "hosted_session_provider_alias");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions
        {
            Endpoint = SurrealTestDefaults.Endpoint, Namespace = _namespace, Database = "test",
            User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password,
        });
        _store = new SurrealHostedSessionStore(new DefaultSurrealExecutor(_connection));
    }

    public async Task DisposeAsync()
    {
        _connection?.Dispose();
        await SurrealTestSchema.DropAsync(_namespace);
    }

    [Fact]
    public async Task A_void_tombstone_is_the_admission_row_every_later_claim_of_the_key_gets()
    {
        var key = AdmissionKey();
        var tombstone = Tombstone(key);
        (await _store.ClaimAdmissionAsync(tombstone)).Result!.Id.Should().Be(tombstone.Id);

        var claimed = (await _store.ClaimAdmissionAsync(Begin(key))).Result!;

        claimed.Id.Should().Be(tombstone.Id, "a later begin must find the void, not open a claim of its own");
        FiatCheckoutVoid.IsVoided(claimed).Should().BeTrue();
        FiatCheckoutVoid.IsTombstone(claimed).Should().BeTrue();
    }

    [Fact]
    public async Task A_void_and_a_begin_claiming_one_unclaimed_key_at_once_end_on_one_row()
    {
        for (var round = 0; round < 10; round++)
        {
            var key = AdmissionKey();
            var voiding = Task.Run(() => _store.ClaimAdmissionAsync(Tombstone(key)));
            var beginning = Task.Run(() => _store.ClaimAdmissionAsync(Begin(key)));
            await Task.WhenAll(voiding, beginning);

            voiding.Result.Result!.Id.Should().Be(beginning.Result.Result!.Id, $"round {round}: the unique admission index admits one row");
        }
    }

    [Fact]
    public async Task The_void_abandon_and_the_begin_attach_exclude_each_other_on_the_state_version()
    {
        // The void lands first: the attach of the create already in flight must lose.
        var (voidFirst, voidFirstFence) = await FencedClaimAsync();
        (await AbandonAsync(voidFirst, voidFirstFence.StateVersion)).Should().NotBeNull();
        (await AttachAsync(voidFirst, voidFirstFence)).Should().BeNull("a voided claim can never carry a page");

        // The attach lands first: the void decided on a stale version and must lose, then re-decide.
        var (attachFirst, attachFirstFence) = await FencedClaimAsync();
        (await AttachAsync(attachFirst, attachFirstFence)).Should().NotBeNull();
        (await AbandonAsync(attachFirst, attachFirstFence.StateVersion)).Should().BeNull("the page it did not see is now attached");
        (await _store.GetByIdAsync(attachFirst)).Result!.State.Should().Be(HostedSessionStates.AwaitingSubject);
    }

    [Fact]
    public async Task A_void_abandon_racing_a_begin_attach_lands_exactly_one_and_never_errors()
    {
        for (var round = 0; round < 10; round++)
        {
            var (id, fence) = await FencedClaimAsync();
            var abandoning = Task.Run(() => AbandonAsync(id, fence.StateVersion));
            var attaching = Task.Run(() => AttachAsync(id, fence));
            await Task.WhenAll(abandoning, attaching);

            (abandoning.Result is null).Should().NotBe(attaching.Result is null, $"round {round}: exactly one CAS lands");
        }
    }

    private async Task<(Guid Id, HostedSession Fence)> FencedClaimAsync()
    {
        var candidate = Begin(AdmissionKey());
        var id = Guid.ParseExact(candidate.Id, "N");
        await _store.ClaimAdmissionAsync(candidate);
        var fence = (await _store.TryBeginCreationAsync(id, candidate.CreationToken!, DateTimeOffset.UtcNow)).Result!;
        return (id, fence);
    }

    private async Task<HostedSession?> AbandonAsync(Guid id, long version) =>
        (await _store.TryTransitionAsync(id, version, HostedSessionStates.Abandoned,
            FiatCheckoutVoid.VoidedOutcome, FiatCheckoutVoid.VoidedAfterAdmission, null)).Result;

    private async Task<HostedSession?> AttachAsync(Guid id, HostedSession fence) =>
        (await _store.TryAttachProviderSessionAsync(id, fence.StateVersion, $"cs_test_{id:N}", null,
            "https://checkout.stripe.example/page", DateTimeOffset.UtcNow.AddMinutes(35), fence.CreationToken!)).Result;

    private static string AdmissionKey() => $"{Guid.NewGuid():N}:self:{Guid.NewGuid():N}";

    private static HostedSession Begin(string key) => new()
    {
        Id = Guid.NewGuid().ToString("N"), AdmissionKey = key,
        FlowKind = HostedSessionFlowKinds.FiatCheckout, ProviderKey = FiatRailIds.StripeProviderKey,
        State = HostedSessionStates.Claimed, CreationToken = Guid.NewGuid().ToString("N"),
        CreationClaimedAt = DateTimeOffset.UtcNow, CreatedDate = DateTimeOffset.UtcNow, ModifiedDate = DateTimeOffset.UtcNow,
    };

    private static HostedSession Tombstone(string key) => new()
    {
        Id = Guid.NewGuid().ToString("N"), AdmissionKey = key,
        FlowKind = HostedSessionFlowKinds.FiatCheckout, ProviderKey = FiatRailIds.StripeProviderKey,
        State = HostedSessionStates.Abandoned, AuthoritativeOutcome = FiatCheckoutVoid.VoidedOutcome,
        OutcomeDetail = FiatCheckoutVoid.VoidedBeforeAdmission,
        CreatedDate = DateTimeOffset.UtcNow, ModifiedDate = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow,
    };
}
