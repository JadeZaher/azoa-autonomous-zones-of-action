using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>Real SurrealDB journal semantics: offer consumption, authority fences, stage epochs and recovery leases.</summary>
public sealed class SurrealWttOperationStoreTests : IAsyncLifetime
{
    private readonly string space = $"test{Guid.NewGuid():N}";
    private ISurrealConnection connection = null!;
    private ISurrealExecutor executor = null!;
    private SurrealWttOperationStore store = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(space, "user", "wallet", "consent_grant", "wtt_offer", "wtt_operation", "wtt_operation_stage");
        connection = new SurrealDbNetConnection(new SurrealConnectionOptions
        {
            Endpoint = SurrealTestDefaults.Endpoint, Namespace = space, Database = "test",
            User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password,
        });
        executor = new DefaultSurrealExecutor(connection);
        store = new(executor);
    }

    public async Task DisposeAsync()
    {
        connection?.Dispose();
        await SurrealTestSchema.DropAsync(space);
    }

    [Fact]
    public async Task ConcurrentAdmissionsConsumeTheOfferOnceAndAnotherRequestCannotReuseIt()
    {
        var seed = await SeedAsync("concurrent");
        var results = await Task.WhenAll(store.AdmitAsync(seed.Candidate, seed.Source), store.AdmitAsync(seed.Candidate, seed.Source));
        Assert.All(results, row => Assert.Equal(seed.Candidate.IntentHash, row!.IntentHash));
        Assert.Equal(seed.Candidate.Id, (await store.GetOfferAsync(seed.Offer.Id))!.ConsumedByOperationId);
        var other = Candidate(seed.Offer, seed.Intent with { OperationId = Operation(seed.Intent.ActorUserId, null, "other") }, "other");
        Assert.Null(await store.AdmitAsync(other, seed.Source));
        Assert.Null(await store.GetAsync(other.Id));
    }

    [Theory]
    [InlineData("expired-offer")]
    [InlineData("wallet-owner")]
    [InlineData("logout")]
    public async Task ChangedOfferValidityOrCurrentAuthorityRollsBackAdmission(string change)
    {
        var seed = await SeedAsync(change, expired: change == "expired-offer");
        if (change == "wallet-owner")
            (await executor.ExecuteAsync(SurrealWriter.UpdateOnly<Wallet>(seed.SourceWallet.ToString("N"))
                .Set(row => row.UserId, SurrealLink.ToLink("user", Guid.NewGuid().ToString("N"))!).Build())).EnsureAllOk();
        if (change == "logout")
            (await executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(seed.Intent.ActorUserId.ToString("N"))
                .Set(row => row.AuthNotBefore, DateTimeOffset.UtcNow).Build())).EnsureAllOk();
        Assert.Null(await store.AdmitAsync(seed.Candidate, seed.Source));
        Assert.Null(await store.GetAsync(seed.Candidate.Id));
        Assert.Null((await store.GetOfferAsync(seed.Offer.Id))!.ConsumedByOperationId);
    }

    [Fact]
    public async Task CancellationTombstoneWinsAgainstADelayedAdmission()
    {
        var seed = await SeedAsync("tombstone");
        Assert.Equal("Cancelled", (await store.CancelAsync(seed.Candidate, seed.Cancel))!.State);
        Assert.Equal("Cancelled", (await store.AdmitAsync(seed.Candidate, seed.Source))!.State);
        Assert.Null((await store.GetOfferAsync(seed.Offer.Id))!.ConsumedByOperationId);
    }

    [Fact]
    public async Task AuthorizedStageCannotBeCancelledAndOnlyTerminalEvidenceReleasesTheNextClaim()
    {
        var seed = await SeedAsync("epochs");
        var operation = (await store.AdmitAsync(seed.Candidate, seed.Source))!;
        var stage = Stage(seed, "Transfer", 0, null);
        var claimed = (await store.ClaimStageAsync(seed.Candidate.Id, operation.Version, stage, seed.Source))!;
        Assert.Null(await store.ClaimStageAsync(seed.Candidate.Id, operation.Version, Stage(seed, "Transfer", 1, null), seed.Source));
        operation = (await store.GetAsync(seed.Candidate.Id))!;
        var prepared = await store.SavePreparedAsync(seed.Candidate.Id, operation.Version, stage.Id, claimed.Version, Prepared("Transfer"));
        Assert.Equal("Prepared", prepared!.State);
        operation = (await store.GetAsync(seed.Candidate.Id))!;
        Assert.Null(await store.AuthorizeAsync(seed.Candidate.Id, operation.Version - 1, stage.Id, prepared.Version, seed.Source));
        var authorized = (await store.AuthorizeAsync(seed.Candidate.Id, operation.Version, stage.Id, prepared.Version, seed.Source))!;
        Assert.Equal("SubmissionAuthorized", authorized.State);
        Assert.NotNull(authorized.AuthorizedAt);

        Assert.Null(await store.CancelAsync(seed.Candidate, seed.Cancel));
        operation = (await store.GetAsync(seed.Candidate.Id))!;
        Assert.Equal("Active", operation.State);
        Assert.Null(await store.ClaimStageAsync(seed.Candidate.Id, operation.Version, Stage(seed, "Attest", 0, stage.Id), seed.Source));

        var pending = (await store.RecordObservationAsync(seed.Candidate.Id, operation.Version, stage.Id, authorized.Version, Observation("Pending"), "Pending"))!;
        Assert.Equal("SubmissionAuthorized", pending.State);
        Assert.Equal(operation.Version, (await store.GetAsync(seed.Candidate.Id))!.Version);
        var confirmed = (await store.RecordObservationAsync(seed.Candidate.Id, operation.Version, stage.Id, pending.Version, Observation("Confirmed"), "Confirmed"))!;
        Assert.Equal("Confirmed", confirmed.State);
        operation = (await store.GetAsync(seed.Candidate.Id))!;
        Assert.Equal("Ready", operation.State);
        Assert.NotNull(await store.ClaimStageAsync(seed.Candidate.Id, operation.Version, Stage(seed, "Attest", 0, stage.Id), seed.Source));
    }

    [Fact]
    public async Task OnlyAnUnsignedUndisclosedStageCanBeRetired()
    {
        var seed = await SeedAsync("retire");
        var operation = (await store.AdmitAsync(seed.Candidate, seed.Source))!;
        var first = Stage(seed, "Transfer", 0, null);
        var claimed = (await store.ClaimStageAsync(seed.Candidate.Id, operation.Version, first, seed.Source))!;
        operation = (await store.GetAsync(seed.Candidate.Id))!;
        Assert.Equal("Retired", (await store.RetireStageAsync(seed.Candidate.Id, operation.Version, first.Id, claimed.Version, "expired"))!.State);
        operation = (await store.GetAsync(seed.Candidate.Id))!;
        Assert.Equal("Ready", operation.State);
        var second = Stage(seed, "Transfer", 1, first.Id);
        claimed = (await store.ClaimStageAsync(seed.Candidate.Id, operation.Version, second, seed.Source))!;
        operation = (await store.GetAsync(seed.Candidate.Id))!;
        var prepared = (await store.SavePreparedAsync(seed.Candidate.Id, operation.Version, second.Id, claimed.Version, Prepared("Transfer")))!;
        operation = (await store.GetAsync(seed.Candidate.Id))!;
        Assert.Null(await store.RetireStageAsync(seed.Candidate.Id, operation.Version, second.Id, prepared.Version, "too late"));
    }

    [Fact]
    public async Task ParticipantExposureNeedsCurrentConsentAndBlocksCancellation()
    {
        var seed = await SeedAsync("exposure");
        var operation = (await store.AdmitAsync(seed.Candidate, seed.Source))!;
        var stage = Stage(seed, "RecipientOptIn", 0, null, participant: "RECIPIENTALGORAND");
        var claimed = (await store.ClaimStageAsync(seed.Candidate.Id, operation.Version, stage, seed.Participant))!;
        operation = (await store.GetAsync(seed.Candidate.Id))!;
        var message = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new { unsigned = "opt-in" }));
        Assert.Null(await store.ExposeAsync(seed.Candidate.Id, operation.Version, stage.Id, claimed.Version, message, seed.Source));
        var exposed = (await store.ExposeAsync(seed.Candidate.Id, operation.Version, stage.Id, claimed.Version, message, seed.Participant))!;
        Assert.NotNull(exposed.ExposedAt);
        Assert.Equal(message, (await store.ExposeAsync(seed.Candidate.Id, operation.Version, stage.Id, claimed.Version, message, seed.Participant))!.ParticipantJson);
        Assert.Null(await store.CancelAsync(seed.Candidate, seed.Cancel));
        operation = (await store.GetAsync(seed.Candidate.Id))!;
        var signed = (await store.SaveParticipantSignatureAsync(seed.Candidate.Id, operation.Version, stage.Id, exposed.Version, Prepared("RecipientOptIn")))!;
        operation = (await store.GetAsync(seed.Candidate.Id))!;
        Assert.Equal("SubmissionAuthorized", (await store.AuthorizeAsync(seed.Candidate.Id, operation.Version, stage.Id, signed.Version, seed.Participant))!.State);
    }

    [Fact]
    public async Task EvidenceIsSetOnceAndRecoveryLeasesAreExclusive()
    {
        var seed = await SeedAsync("evidence");
        var operation = (await store.AdmitAsync(seed.Candidate, seed.Source))!;
        var updated = (await store.UpdateAsync(seed.Candidate.Id, operation.Version, new(State: "AwaitingProof", TransferPublicationJson: "{\"a\":1}")))!;
        Assert.Null(await store.UpdateAsync(seed.Candidate.Id, updated.Version, new(TransferPublicationJson: "{\"a\":2}")));
        Assert.Null(await store.UpdateAsync(seed.Candidate.Id, operation.Version, new(Detail: "stale")));
        var leases = await Task.WhenAll(store.ClaimRecoveryAsync(seed.Candidate.Id, TimeSpan.FromMinutes(1)),
            store.ClaimRecoveryAsync(seed.Candidate.Id, TimeSpan.FromMinutes(1)));
        Assert.Single(leases, value => value);
        Assert.Equal(updated.Version, (await store.GetAsync(seed.Candidate.Id))!.Version);
    }

    [Fact]
    public async Task ConfirmedTransferIsFencedFromCancellationAndCompletionMovesToProofAtomically()
    {
        var legacy = await SeedAsync("legacy-ready");
        var stage = await AuthorizedTransferAsync(legacy);
        var operation = (await store.GetAsync(legacy.Candidate.Id))!;
        Assert.NotNull(await store.RecordObservationAsync(legacy.Candidate.Id, operation.Version, stage.Id, stage.Version, Observation("Confirmed"), "Confirmed"));
        Assert.Equal("Ready", (await store.GetAsync(legacy.Candidate.Id))!.State);
        Assert.Null(await store.CancelAsync(legacy.Candidate, legacy.Cancel));

        var atomic = await SeedAsync("atomic-completion");
        stage = await AuthorizedTransferAsync(atomic);
        operation = (await store.GetAsync(atomic.Candidate.Id))!;
        var publication = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new { sequence = "42" }));
        Assert.NotNull(await store.RecordObservationAsync(atomic.Candidate.Id, operation.Version, stage.Id, stage.Version, Observation("Confirmed"),
            "Confirmed", new WttSourceCompletion(publication, null)));
        operation = (await store.GetAsync(atomic.Candidate.Id))!;
        Assert.Equal("AwaitingProof", operation.State);
        Assert.Equal(publication, operation.TransferPublicationJson);
        Assert.Null(await store.CancelAsync(atomic.Candidate, atomic.Cancel));
    }

    [Fact]
    public async Task EvmCancellationBindsToTheProbedVersionAndNeverCrossesAPreparingOrSignedStage()
    {
        var seed = await SeedAsync("evm-epoch", chain: "Ethereum");
        var operation = (await store.AdmitAsync(seed.Candidate, seed.Source))!;
        var stage = Stage(seed, "Transfer", 0, null);
        var claimed = (await store.ClaimStageAsync(seed.Candidate.Id, operation.Version, stage, seed.Source))!;
        operation = (await store.GetAsync(seed.Candidate.Id))!;
        Assert.Null(await store.CancelAsync(seed.Candidate, seed.Cancel));
        var probed = operation.Version;
        var preparing = (await store.BeginPreparationAsync(seed.Candidate.Id, operation.Version, stage.Id, claimed.Version))!;
        Assert.Equal("Preparing", preparing.State);
        Assert.Null(await store.BeginPreparationAsync(seed.Candidate.Id, operation.Version, stage.Id, claimed.Version));
        Assert.Null(await store.CancelAsync(seed.Candidate, seed.Cancel, probed));
        operation = (await store.GetAsync(seed.Candidate.Id))!;
        Assert.Null(await store.CancelAsync(seed.Candidate, seed.Cancel, operation.Version));

        Assert.Equal("Retired", (await store.RetireStageAsync(seed.Candidate.Id, operation.Version, stage.Id, preparing.Version, "no reservation"))!.State);
        operation = (await store.GetAsync(seed.Candidate.Id))!;
        var next = Stage(seed, "Transfer", 1, stage.Id);
        var nextClaim = (await store.ClaimStageAsync(seed.Candidate.Id, operation.Version, next, seed.Source))!;
        operation = (await store.GetAsync(seed.Candidate.Id))!;
        var signing = (await store.BeginPreparationAsync(seed.Candidate.Id, operation.Version, next.Id, nextClaim.Version))!;
        operation = (await store.GetAsync(seed.Candidate.Id))!;
        Assert.Equal("Prepared", (await store.SavePreparedAsync(seed.Candidate.Id, operation.Version, next.Id, signing.Version, Prepared("Transfer")))!.State);
        operation = (await store.GetAsync(seed.Candidate.Id))!;
        Assert.Null(await store.CancelAsync(seed.Candidate, seed.Cancel, operation.Version));
        Assert.Equal("Active", (await store.GetAsync(seed.Candidate.Id))!.State);

        var planned = await SeedAsync("evm-planned", chain: "Ethereum");
        operation = (await store.AdmitAsync(planned.Candidate, planned.Source))!;
        Assert.NotNull(await store.ClaimStageAsync(planned.Candidate.Id, operation.Version, Stage(planned, "Transfer", 0, null), planned.Source));
        operation = (await store.GetAsync(planned.Candidate.Id))!;
        Assert.Equal("Cancelled", (await store.CancelAsync(planned.Candidate, planned.Cancel, operation.Version))!.State);
    }

    [Fact]
    public async Task TenantDrivenJournalIsIsolatedFromAnotherTenantButTheDirectOwnerMayCancelUnsent()
    {
        var seed = await SeedAsync("tenant-isolation", tenantDriven: true);
        var admitted = (await store.AdmitAsync(seed.Candidate, seed.Source))!;
        Assert.Equal(seed.Intent.TenantId?.ToString("N"), admitted.TenantId);
        var foreign = seed.Source.Signing! with { Credential = seed.Source.Signing.Credential with { TenantId = Guid.NewGuid() } };
        Assert.Null(await store.AdmitAsync(seed.Candidate, seed.Source with { Signing = foreign }));
        Assert.Empty(await store.ListAsync(seed.Candidate.ActorUserId, Guid.NewGuid().ToString("N"), null, 10));
        Assert.Single(await store.ListAsync(seed.Candidate.ActorUserId, admitted.TenantId, null, 10));
        var otherTenantCancel = seed.Cancel.Consent! with { Credential = seed.Cancel.Consent.Credential with { TenantId = Guid.NewGuid() } };
        Assert.Null(await store.CancelAsync(seed.Candidate, seed.Cancel with { Consent = otherTenantCancel }));
        var owner = seed.Cancel.Consent with
        {
            Credential = seed.Cancel.Consent.Credential with { TenantId = null, TokenUse = AzoaClaims.TokenUseLogin, OwnerAuthority = true },
            Grants = [],
        };
        Assert.Equal("Cancelled", (await store.CancelAsync(seed.Candidate, seed.Cancel with { Consent = owner }))!.State);
    }

    private async Task<WttOperationStage> AuthorizedTransferAsync(Seed seed)
    {
        var operation = (await store.AdmitAsync(seed.Candidate, seed.Source))!;
        var stage = Stage(seed, "Transfer", 0, null);
        var claimed = (await store.ClaimStageAsync(seed.Candidate.Id, operation.Version, stage, seed.Source))!;
        operation = (await store.GetAsync(seed.Candidate.Id))!;
        var prepared = (await store.SavePreparedAsync(seed.Candidate.Id, operation.Version, stage.Id, claimed.Version, Prepared("Transfer")))!;
        operation = (await store.GetAsync(seed.Candidate.Id))!;
        return (await store.AuthorizeAsync(seed.Candidate.Id, operation.Version, stage.Id, prepared.Version, seed.Source))!;
    }

    private sealed record Seed(WttOffer Offer, WttOperation Candidate, WttOperationIntent Intent, Guid SourceWallet,
        WttStageAuthority Source, WttStageAuthority Participant, WttStageAuthority Cancel);

    private async Task<Seed> SeedAsync(string key, bool expired = false, string chain = "Algorand", bool tenantDriven = false)
    {
        var actor = Guid.NewGuid();
        var wallet = Guid.NewGuid();
        Guid? tenant = tenantDriven ? Guid.NewGuid() : null;
        var address = chain == "Ethereum" ? "0x1111111111111111111111111111111111111111" : "SOURCEALGORAND";
        foreach (var user in tenant is { } tenantId ? new[] { actor, tenantId } : new[] { actor })
            (await executor.ExecuteAsync(SurrealWriter.Create(new User
            {
                Id = user.ToString("N"), Username = user.ToString("N"), Email = user.ToString("N") + "@example.test",
                PasswordHash = "fixture-no-login", IsActive = true,
            }))).EnsureAllOk();
        (await executor.ExecuteAsync(SurrealWriter.Create(new Wallet
        {
            Id = wallet.ToString("N"), UserId = SurrealLink.ToLink("user", actor.ToString("N"))!, Address = address,
            ChainType = chain, WalletType = Wallet.WalletTypeKind.Platform,
        }))).EnsureAllOk();
        var intent = Intent(actor, tenant, wallet, address, chain, Operation(actor, tenant, key));
        var now = DateTimeOffset.UtcNow;
        var quote = new WttStageQuote(new string('q', 64), CanonicalJson.Hash(intent), intent.Source.ProtocolIdentity, "Source",
            [new("Transfer", "100", "0", "0", intent.SourceAuthority.Address)], "{}", now.AddSeconds(30));
        var quoteJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(quote));
        var offer = new WttOffer
        {
            Id = Guid.NewGuid().ToString("N"), ActorUserId = actor.ToString("N"), TenantId = tenant?.ToString("N") ?? "", OperationId = intent.OperationId,
            RequestKeyHash = WttOperationIdentity.RequestKeyHash(key), IntentJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(intent)),
            IntentHash = CanonicalJson.Hash(intent), SourceQuoteJson = quoteJson, TargetInspectionJson = "{}",
            OfferHash = new string('f', 64), CreatedAt = expired ? now.AddMinutes(-2) : now, ExpiresAt = expired ? now.AddMinutes(-1) : now.AddSeconds(30),
        };
        Assert.NotNull(await store.CreateOfferAsync(offer));
        var credential = new SigningCredentialWitness(1, "Jwt", actor, tenant, null, now.AddMinutes(-1), now.AddMinutes(10), null, null,
            [AzoaScopes.WttSourceSign, AzoaScopes.WttParticipantSign], tenant is null,
            TokenUse: tenant is null ? AzoaClaims.TokenUseLogin : AzoaClaims.TokenUseChild);
        IReadOnlyList<SigningGrantWitness> sourceGrants = [], participantGrants = [];
        if (tenant is { } grantTenant)
        {
            var grant = new ConsentGrant
            {
                Id = Guid.NewGuid().ToString("N"), GrantorUserId = SurrealLink.ToLink("user", actor.ToString("N"))!,
                TenantId = SurrealLink.ToLink("user", grantTenant.ToString("N"))!, Scopes = "wtt:source:sign,wtt:participant:sign",
                Origin = "UserExplicit", GrantedAt = now.AddMinutes(-1),
            };
            (await executor.ExecuteAsync(SurrealWriter.Create(grant))).EnsureAllOk();
            sourceGrants = [new(Guid.Parse(grant.Id), AzoaScopes.WttSourceSign, grant.Scopes, grant.GrantedAt, grant.ExpiresAt)];
            participantGrants = [new(Guid.Parse(grant.Id), AzoaScopes.WttParticipantSign, grant.Scopes, grant.GrantedAt, grant.ExpiresAt)];
        }
        var signing = new SigningAdmissionAuthority(1, "DirectJwt", credential, new(wallet, actor, address, chain),
            [AzoaScopes.WttSourceSign], sourceGrants);
        return new(offer, Candidate(offer, intent, key), intent, wallet, new(AzoaScopes.WttSourceSign, signing, null),
            new(AzoaScopes.WttParticipantSign, null, new(1, credential, [AzoaScopes.WttParticipantSign], participantGrants)),
            new(AzoaScopes.WttSourceSign, null, new(1, credential, [AzoaScopes.WttSourceSign], sourceGrants)));
    }

    private static string Operation(Guid actor, Guid? tenant, string key)
        => WttOperationIdentity.Operation(actor, tenant, WttOperationIdentity.RequestKeyHash(key));

    private static WttOperation Candidate(WttOffer offer, WttOperationIntent intent, string key)
    {
        var intentJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(intent));
        return new WttOperation
        {
            Id = intent.OperationId, ActorUserId = offer.ActorUserId, TenantId = offer.TenantId, RequestKeyHash = WttOperationIdentity.RequestKeyHash(key),
            OfferId = offer.Id, OfferHash = offer.OfferHash, IntentJson = intentJson, IntentHash = CanonicalJson.HashCanonical(intentJson),
            SourceQuoteJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(
                JsonSerializer.Deserialize<WttStageQuote>(offer.SourceQuoteJson)! with { IntentHash = CanonicalJson.HashCanonical(intentJson) })),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, NextAttemptAt = DateTimeOffset.UtcNow,
        };
    }

    private static WttOperationIntent Intent(Guid actor, Guid? tenant, Guid wallet, string address, string chain, string operationId)
    {
        var protocolChain = chain == "Ethereum" ? (ushort)2 : (ushort)8;
        var source = new WttNetworkDescriptor(chain, ChainNetwork.Devnet, chain.ToLowerInvariant() + ":local", "Devnet", protocolChain, "1", "2",
            new string('c', 64), new(protocolChain, new string('1', 64), new string('2', 64), new string('3', 64)), new string('a', 64));
        var target = new WttNetworkDescriptor("Solana", ChainNetwork.Devnet, "solana:local", "Devnet", 1, "core", "token", new string('d', 64),
            new(1, new string('4', 64), new string('5', 64), new string('6', 64)), new string('b', 64));
        var context = new SigningContext(actor, wallet, false, tenant ?? Guid.Empty, tenant is null ? Guid.Empty : actor,
            AzoaScopes.WttSourceSign, chain);
        return new(1, operationId, actor, tenant, source, target,
            new("31566704", false, 6, protocolChain, new string('9', 64), 6, new string('0', 64), new string('0', 64), "asa"),
            new(address, context), new("TARGETSOLANA", context with { Scope = AzoaScopes.WttTargetSign, ChainType = "Solana" }),
            "RECIPIENTSOLANA", new string('e', 64), "wrapped", 6, new("1000", "1000", "0", "1000", "0", "1000"), 7, 32, false, false,
            new("1000", "0", "0"), new("1000", "0", "0"));
    }

    private static WttOperationStage Stage(Seed seed, string name, int generation, string? previous, string? participant = null)
    {
        var quote = seed.Candidate.SourceQuoteJson;
        var phase = participant is null ? "Source" : "Target";
        return new WttOperationStage
        {
            Id = WttOperationIdentity.Stage(seed.Candidate.Id, phase, name, generation), OperationId = seed.Candidate.Id,
            IntentHash = seed.Candidate.IntentHash, Phase = phase, Stage = name, Generation = generation, PreviousStageId = previous,
            QuoteJson = quote, QuoteHash = CanonicalJson.HashCanonical(quote), ParticipantAddress = participant,
        };
    }

    private static string Prepared(string stage)
        => CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new WttStagePreparation(stage, 0, "tx-" + stage, "d2lyZQ==",
            "algorand:local", "{}", new(stage, "100", "0", "0"))));

    private static string Observation(string verdict)
        => CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new WttStageObservation(verdict, "tx-Transfer", "algorand:local")));
}
