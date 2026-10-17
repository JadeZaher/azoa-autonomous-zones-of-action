using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Fungible;
using AZOA.WebAPI.Services.Governance;
using AZOA.WebAPI.Services.Signing;
using Moq;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Admin;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Interfaces.Managers;
using UserRow = AZOA.WebAPI.Persistence.SurrealDb.Models.User;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealFungibleProjectionTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private ISurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;
    private SurrealFungibleCreationStore _store = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "user", "holon", "wallet", "consent_grant", "fungible_creation_operation", "fungible_creation_authorization",
            "fungible_creation_projection", "fungible_asset_authority", "fungible_creation_attachment", "fungible_creation_cancellation", "platform_reserve_ledger",
            "platform_token", "admin_bootstrap_state", "node_governance_parameters", "node_governance_policy_lock");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions { Endpoint = SurrealTestDefaults.Endpoint,
            Namespace = _namespace, Database = "test", User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password });
        _executor = new DefaultSurrealExecutor(_connection);
        _store = new(_executor);
    }

    public async Task DisposeAsync()
    {
        try { await SurrealTestSchema.DropAsync(_namespace); }
        finally { _connection?.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubmittedRecoveryRebroadcastsOnlySavedWireAndProjectsOnceAfterLostResponse(bool loseResponse)
    {
        var (row, _, _, actor, _) = await SeedAsync();
        var intent = JsonSerializer.Deserialize<FungibleCreationIntent>(row.IntentJson)!;
        var prepared = new PreparedFungibleCreation(FungibleCreationIdentity.PreparationId(intent), "original-tx", "original-signed-wire", "genesis", intent);
        await Execute(SurrealWriter.UpdateOnly<FungibleCreationOperation>(row.Id).Where(seed => true).Set(value => value.State, "Submitted")
            .Set(value => value.PreparedJson, JsonSerializer.Serialize(prepared)).Unset(value => value.ObservationJson).Unset(value => value.AssetId).Build());
        await Execute(SurrealWriter.UpdateOnly<UserRow>(actor.ToString("N")).Where(seed => true).Set(value => value.IsActive, false).Build());
        var module = new Mock<IFungibleCreationModule>(MockBehavior.Strict);
        module.SetupSequence(value => value.ObserveFungibleCreationAsync(It.IsAny<PreparedFungibleCreation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<FungibleCreationObservation>.Success(new("Pending", "original-tx", "genesis")))
            .ReturnsAsync(AZOAResult<FungibleCreationObservation>.Success(new("Confirmed", "original-tx", "genesis", "42", "100", "verified-chain-proof")));
        module.Setup(value => value.SubmitFungibleCreationAsync(It.IsAny<PreparedFungibleCreation>(), It.IsAny<CancellationToken>()))
            .Returns((PreparedFungibleCreation wire, CancellationToken _) =>
            {
                JsonSerializer.Serialize(wire).Should().Be(JsonSerializer.Serialize(prepared));
                return loseResponse ? Task.FromException(new HttpRequestException("Lost submit response")) : Task.CompletedTask;
            });
        var service = RecoveryService(module.Object);
        if (loseResponse) await FluentActions.Awaiting(() => service.RefreshAsync(row.Id, actor, null)).Should().ThrowAsync<HttpRequestException>();
        else (await service.RefreshAsync(row.Id, actor, null)).Result!.State.Should().Be("Submitted");
        (await _store.GetAsync(row.Id))!.State.Should().Be("Submitted");
        var recovered = await service.RefreshAsync(row.Id, actor, null);
        recovered.Result!.State.Should().Be("Completed");
        recovered.Result.AssetId.Should().Be("42");
        (await service.RefreshAsync(row.Id, actor, null)).Result!.State.Should().Be("Completed");
        module.Verify(value => value.SubmitFungibleCreationAsync(It.IsAny<PreparedFungibleCreation>(), It.IsAny<CancellationToken>()), Times.Once);
        module.Verify(value => value.PrepareFungibleCreationAsync(It.IsAny<FungibleCreationIntent>(), It.IsAny<CancellationToken>()), Times.Never);
        (await _executor.QueryAsync<FungibleAssetAuthority>(SurrealQuery<FungibleAssetAuthority>.From())).Should().ContainSingle();
    }

    [Theory]
    [InlineData("Admitted")]
    [InlineData("Preparing")]
    [InlineData("Prepared")]
    public async Task RestrictedRecoveryNeverSignsOrFirstSendsAnUnsubmittedStage(string state)
    {
        var (row, _, _, actor, _) = await SeedAsync();
        await Execute(SurrealWriter.UpdateOnly<FungibleCreationOperation>(row.Id).Where(seed => true).Set(value => value.State, state)
            .Unset(value => value.ObservationJson).Unset(value => value.AssetId).Build());
        var module = new Mock<IFungibleCreationModule>(MockBehavior.Strict);
        (await RecoveryService(module.Object).RefreshAsync(row.Id, actor, null)).Result!.State.Should().Be(state);
        module.VerifyNoOtherCalls();
        (await _executor.QueryAsync<FungibleAssetAuthority>(SurrealQuery<FungibleAssetAuthority>.From())).Should().BeEmpty();
    }

    private FungibleCreationService RecoveryService(IFungibleCreationModule module)
    {
        var provider = new Mock<IBlockchainProvider>();
        provider.Setup(value => value.TryGetModule<IFungibleCreationModule>(out It.Ref<IFungibleCreationModule?>.IsAny))
            .Returns((out IFungibleCreationModule? selected) => { selected = module; return true; });
        var providers = new Mock<IBlockchainProviderFactory>();
        providers.Setup(value => value.GetProvider("Algorand", ChainNetwork.Devnet)).Returns(provider.Object);
        // Only the chain edge is faked; stores are real and value access refuses loudly (recovery must not consult it).
        return new(_store, providers.Object, new RecoveryMustNotConsultValueAccess(), NodeGovernanceGuard.Unrestricted, _executor,
            new SigningAdmissionAuthorityFactory(new SurrealUserStore(_executor), new SurrealWalletStore(_executor), _executor));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImmutableDefinitionClaimChecksOperatorRevisionInTheCreateTransaction(bool revoke)
    {
        var (candidate, _, _) = await SeedGenesisAsync(confirmed: false, seedDefinition: false);
        var command = JsonSerializer.Deserialize<FungibleCreationCommand>(candidate.RequestJson)!;
        if (revoke) await Execute(SurrealWriter.UpdateOnly<AdminBootstrapState>("local").Where(seed => true).Set(row => row.SessionRevision, 2L).Build());
        var definition = new PlatformToken { Id = command.GenesisAuthority!.PlatformTokenId, Chain = "Algorand", Network = "Devnet",
            Name = "Unit", UnitName = "UNT", Decimals = 2, TotalSupply = "1000", ReserveAddress = "reserve",
            GenesisOperationKey = command.RequestKey, GenesisUserId = command.ActorUserId.ToString("N"),
            ClaimedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var store = new SurrealPlatformTokenStore(_executor);
        var claimed = await store.ClaimIfAbsentAsync(definition, credential: command.GenesisAuthority.Credential);
        claimed.IsError.Should().Be(revoke);
        var rows = await _executor.QueryAsync<PlatformToken>(SurrealQuery<PlatformToken>.From());
        rows.Count.Should().Be(revoke ? 0 : 1);
        if (!revoke)
        {
            (await store.HasVerifiedIssuanceAsync(rows.Single())).Should().BeFalse();
            var replay = await store.ClaimIfAbsentAsync(definition, credential: command.GenesisAuthority.Credential);
            replay.Result!.Created.Should().BeFalse();
        }
    }

    [Fact]
    public async Task GenesisAdmissionRequiresExactRecentOperatorRevisionAndDoesNotActivate()
    {
        var (candidate, _, _) = await SeedGenesisAsync(confirmed: false);
        (await _store.ClaimAsync(candidate))!.State.Should().Be("AwaitingConsent");
        var platform = (await _executor.QueryAsync<PlatformToken>(SurrealQuery<PlatformToken>.From())).Single();
        platform.Status.Should().Be("Pending");
        platform.AssetId.Should().BeNull();
        platform.CreationOperationId.Should().Be(candidate.Id);
        (await _executor.QueryAsync<FungibleAssetAuthority>(SurrealQuery<FungibleAssetAuthority>.From())).Should().BeEmpty();
    }

    [Theory]
    [InlineData("session-revoked")]
    [InlineData("expired-recent-window")]
    [InlineData("different-reserve")]
    [InlineData("forged-owner")]
    public async Task GenesisAdmissionRejectsChangedOperatorOrDefinition(string change)
    {
        var (candidate, _, _) = await SeedGenesisAsync(confirmed: false);
        if (change == "session-revoked") await Execute(SurrealWriter.UpdateOnly<AdminBootstrapState>("local").Where(seed => true).Set(row => row.SessionRevision, 2L).Build());
        else if (change == "different-reserve")
        {
            var definition = (await _executor.QueryAsync<PlatformToken>(SurrealQuery<PlatformToken>.From())).Single();
            await Execute(SurrealWriter.UpdateOnly<PlatformToken>(definition.Id).Where(seed => true).Set(row => row.ReserveAddress, "another-reserve").Build());
        }
        else
        {
            var command = JsonSerializer.Deserialize<FungibleCreationCommand>(candidate.RequestJson)!;
            var original = command.GenesisAuthority!;
            var changed = change == "forged-owner" ? original.Credential with { OwnerAuthority = true }
                : original.Credential with { IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-11) };
            command = command with { GenesisAuthority = original with { Credential = changed } };
            candidate.RequestJson = JsonSerializer.Serialize(command);
            candidate.RequestHash = CanonicalJson.Hash(command);
        }
        (await _store.ClaimAsync(candidate)).Should().BeNull();
        (await _executor.QueryAsync<FungibleCreationOperation>(SurrealQuery<FungibleCreationOperation>.From())).Should().BeEmpty();
        (await _executor.QueryAsync<PlatformToken>(SurrealQuery<PlatformToken>.From())).Single().CreationOperationId.Should().BeNull();
    }

    [Fact]
    public async Task FinalizedGenesisActivatesWithAuthorityAndReserveAfterOperatorSessionRevocation()
    {
        var (row, authority, reserve) = await SeedGenesisAsync(confirmed: true);
        await Execute(SurrealWriter.UpdateOnly<AdminBootstrapState>("local").Where(seed => true).Set(value => value.SessionRevision, 2L).Build());
        await Execute(SurrealWriter.UpdateOnly<UserRow>(NodeOperatorIdentity.UserId.ToString("N")).Where(seed => true).Set(value => value.IsActive, false).Build());
        var completions = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => _store.CompleteAsync(row, authority, reserve)))
            .WaitAsync(TimeSpan.FromSeconds(20));
        completions.Count(value => value).Should().Be(1);
        (await _store.GetAsync(row.Id))!.State.Should().Be("Completed");
        var platform = (await _executor.QueryAsync<PlatformToken>(SurrealQuery<PlatformToken>.From())).Single();
        platform.Status.Should().Be("Active");
        platform.AssetId.Should().Be("42");
        platform.CreationOperationId.Should().Be(row.Id);
        (await new SurrealPlatformTokenStore(_executor).HasVerifiedIssuanceAsync(platform)).Should().BeTrue();
        (await _executor.QueryAsync<FungibleAssetAuthority>(SurrealQuery<FungibleAssetAuthority>.From())).Should().ContainSingle();
        (await _executor.QueryAsync<PlatformReserveLedger>(SurrealQuery<PlatformReserveLedger>.From())).Single().AssetId.Should().Be("42");
    }

    [Fact]
    public async Task ReplacementArchivesExactAbsenceAndRequiresANewQuoteWithoutChangingTheOriginalRequest()
    {
        var (row, _, _, issuer, _) = await SeedAsync();
        var command = JsonSerializer.Deserialize<FungibleCreationCommand>(row.RequestJson)!;
        var intent = JsonSerializer.Deserialize<FungibleCreationIntent>(row.IntentJson)!;
        await Execute(SurrealWriter.Create(new Wallet { Id = command.WalletId.ToString("N"), UserId = $"user:{issuer:N}",
            ChainType = "Algorand", Address = "creator", CreatedDate = DateTimeOffset.UtcNow }));
        var prepared = JsonSerializer.Serialize(new PreparedFungibleCreation("candidate", "expired-tx", "retained-wire", "genesis", intent));
        var proof = JsonSerializer.Serialize(new FungibleCreationObservation("ExpiredAbsent", "expired-tx", "genesis", EvidenceJson: "complete-final-history"));
        await Execute(SurrealWriter.UpdateOnly<FungibleCreationOperation>(row.Id).Where(seed => true).Set(value => value.State, "FailedNoEffect")
            .Set(value => value.PreparedJson, prepared).Set(value => value.ObservationJson, proof).Unset(value => value.AssetId).Build());
        row = (await _store.GetAsync(row.Id))!;
        var nextIntent = intent with { Generation = 1, Quote = null };
        var authorization = new FungibleCreationAuthorization { Id = Guid.NewGuid().ToString("N"), OperationId = row.Id,
            ActorId = row.ActorUserId, ExpectedVersion = row.Version, RequestHash = "exact-replacement-request", IntentHash = FungibleCreationIdentity.IntentHash(nextIntent),
            AuthorityJson = JsonSerializer.Serialize(new SigningAdmissionAuthority(1, "DirectJwt", Consent(issuer).Credential,
                new(command.WalletId, issuer, "creator", "Algorand"), [AzoaScopes.TokenCreateSign], [])), CreatedAt = DateTimeOffset.UtcNow };
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => _store.ReplaceAsync(row, authorization)))
            .WaitAsync(TimeSpan.FromSeconds(20));
        results.Should().OnlyContain(result => result != null && result.Generation == 1 && result.State == "AwaitingConsent");
        var replacement = (await _store.GetAsync(row.Id))!;
        replacement.RequestJson.Should().Be(row.RequestJson);
        replacement.RequestHash.Should().Be(row.RequestHash);
        replacement.PreparedJson.Should().BeNull();
        replacement.OfferedQuoteJson.Should().BeNull();
        JsonSerializer.Deserialize<FungibleCreationIntent>(replacement.IntentJson)!.Quote.Should().BeNull();
        var history = JsonSerializer.Deserialize<List<FungibleCreationGeneration>>(replacement.HistoryJson)!;
        history.Should().ContainSingle();
        history[0].PreparedJson.Should().Be(prepared);
        history[0].ObservationJson.Should().Be(proof);
        (await _executor.QueryAsync<FungibleCreationAuthorization>(SurrealQuery<FungibleCreationAuthorization>.From())).Should().ContainSingle();
        var cancelled = await _store.CancelAsync(row.Id, new(Guid.NewGuid(), replacement.Version), Consent(issuer).Credential);
        cancelled!.HistoryJson.Should().Be(replacement.HistoryJson);
        cancelled.State.Should().Be("Cancelled");
    }

    [Fact]
    public async Task ProvenAbsentGenerationCanBeCancelledWithoutErasingItsSignedAudit()
    {
        var (row, _, _, issuer, _) = await SeedAsync();
        var intent = JsonSerializer.Deserialize<FungibleCreationIntent>(row.IntentJson)!;
        var prepared = JsonSerializer.Serialize(new PreparedFungibleCreation("candidate", "original-tx", "retained-wire", "genesis", intent));
        var proof = JsonSerializer.Serialize(new FungibleCreationObservation("ExpiredAbsent", "original-tx", "genesis", EvidenceJson: "complete-indexer-history"));
        await Execute(SurrealWriter.UpdateOnly<FungibleCreationOperation>(row.Id).Where(seed => true).Set(value => value.State, "FailedNoEffect")
            .Set(value => value.PreparedJson, prepared).Set(value => value.ObservationJson, proof).Unset(value => value.AssetId).Build());
        var request = new FungibleContinuationRequest(Guid.NewGuid(), row.Version);
        var cancelled = await _store.CancelAsync(row.Id, request, Consent(issuer).Credential);
        cancelled!.State.Should().Be("Cancelled");
        cancelled.PreparedJson.Should().Be(prepared);
        cancelled.ObservationJson.Should().Be(proof);
        (await _store.CancelAsync(row.Id, request, Consent(issuer).Credential))!.Version.Should().Be(cancelled.Version);
        (await _executor.QueryAsync<FungibleCreationCancellation>(SurrealQuery<FungibleCreationCancellation>.From())).Should().ContainSingle();
    }

    [Theory]
    [InlineData("Submitted", "ExpiredAbsent", "original-tx")]
    [InlineData("NeedsOperator", "Unknown", "original-tx")]
    [InlineData("FailedNoEffect", "ExpiredAbsent", "another-tx")]
    public async Task CancellationCannotTreatAmbiguousSubmissionOrUnrelatedProofAsNoEffect(string state, string verdict, string proofTransaction)
    {
        var (row, _, _, issuer, _) = await SeedAsync();
        var intent = JsonSerializer.Deserialize<FungibleCreationIntent>(row.IntentJson)!;
        await Execute(SurrealWriter.UpdateOnly<FungibleCreationOperation>(row.Id).Where(seed => true).Set(value => value.State, state)
            .Set(value => value.PreparedJson, JsonSerializer.Serialize(new PreparedFungibleCreation("candidate", "original-tx", "retained-wire", "genesis", intent)))
            .Set(value => value.ObservationJson, JsonSerializer.Serialize(new FungibleCreationObservation(verdict, proofTransaction, "genesis")))
            .Unset(value => value.AssetId).Build());
        (await _store.CancelAsync(row.Id, new(Guid.NewGuid(), row.Version), Consent(issuer).Credential)).Should().BeNull();
        (await _store.GetAsync(row.Id))!.State.Should().Be(state);
    }

    [Fact]
    public async Task TransferredHolonCannotHideFinalizedIssuanceAndNewOwnerCanAcceptExactAssociation()
    {
        var (row, authority, holon, issuer, owner) = await SeedAsync();
        await Execute(SurrealWriter.UpdateOnly<SurrealHolonStore.HolonPoco>(holon.ToString("N")).Where(seed => true)
            .Set(value => value.UserId, $"user:{owner:N}").Build());
        (await _store.CompleteAsync(row, authority, null)).Should().BeTrue();
        var projected = (await _store.GetAsync(row.Id))!;
        projected.State.Should().Be("AttachmentPending");
        (await _executor.QuerySingleAsync<FungibleAssetAuthority>(SurrealQuery<FungibleAssetAuthority>.Key(authority.Id)))!.AssetId.Should().Be("42");
        (await _store.GetAttachmentAsync(row.Id, issuer, null)).Should().BeNull();
        var disclosed = (await _store.GetAttachmentAsync(row.Id, owner, null))!;
        disclosed.AssetId.Should().Be("42");
        var request = new FungibleAttachmentRequest(Guid.NewGuid(), disclosed.Version, disclosed.ProjectionAuthorityId);
        var consent = Consent(owner);
        var calls = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _store.AttachAsync(row.Id, request, consent)))
            .WaitAsync(TimeSpan.FromSeconds(20));
        calls.Should().OnlyContain(value => value != null && value.State == "Completed");
        var receipts = await _executor.QueryAsync<FungibleCreationAttachment>(SurrealQuery<FungibleCreationAttachment>.From());
        receipts.Should().ContainSingle();
        (await _executor.QuerySingleAsync<SurrealHolonStore.HolonPoco>(SurrealQuery<SurrealHolonStore.HolonPoco>.Key(holon.ToString("N"))))!.TokenId.Should().Be("42");
        await Execute(SurrealWriter.UpdateOnly<SurrealHolonStore.HolonPoco>(holon.ToString("N")).Where(seed => true)
            .Set(value => value.UserId, $"user:{issuer:N}").Build());
        (await _store.ReplayAttachmentAsync(row.Id, request, owner, null))!.State.Should().Be("Completed");
        (await _store.ReplayAttachmentAsync(row.Id, request with { ProjectionAuthorityId = "another" }, owner, null)).Should().BeNull();
        (await _store.GetAsync(row.Id))!.Version.Should().Be(projected.Version + 1);
    }

    [Theory]
    [InlineData("rebound")]
    [InlineData("owner-changed")]
    [InlineData("cross-tenant")]
    [InlineData("wrong-authority")]
    public async Task StaleAttachmentConsentCannotOverwriteAnotherBindingOrAuthority(string change)
    {
        var (row, authority, holon, issuer, owner) = await SeedAsync();
        await Execute(SurrealWriter.UpdateOnly<SurrealHolonStore.HolonPoco>(holon.ToString("N")).Where(seed => true)
            .Set(value => value.UserId, $"user:{owner:N}").Build());
        (await _store.CompleteAsync(row, authority, null)).Should().BeTrue();
        var view = (await _store.GetAttachmentAsync(row.Id, owner, null))!;
        var request = new FungibleAttachmentRequest(Guid.NewGuid(), view.Version, authority.Id);
        var consent = Consent(owner);
        if (change == "rebound") await Execute(SurrealWriter.UpdateOnly<SurrealHolonStore.HolonPoco>(holon.ToString("N")).Where(seed => true).Set(value => value.TokenId, "99").Build());
        if (change == "owner-changed") await Execute(SurrealWriter.UpdateOnly<SurrealHolonStore.HolonPoco>(holon.ToString("N")).Where(seed => true).Set(value => value.UserId, $"user:{issuer:N}").Build());
        if (change == "cross-tenant")
        {
            var tenant = Guid.NewGuid();
            consent = consent with { Credential = consent.Credential with { TenantId = tenant, TokenUse = AzoaClaims.TokenUseChild, OwnerAuthority = false } };
            (await _store.GetAttachmentAsync(row.Id, owner, tenant)).Should().BeNull();
        }
        if (change == "wrong-authority") request = request with { ProjectionAuthorityId = "unrelated" };
        (await _store.AttachAsync(row.Id, request, consent)).Should().BeNull();
        (await _store.GetAsync(row.Id))!.State.Should().Be("AttachmentPending");
        (await _executor.QueryAsync<FungibleCreationAttachment>(SurrealQuery<FungibleCreationAttachment>.From())).Should().BeEmpty();
    }

    [Fact]
    public async Task OriginalOwnerProjectionIsAtomicAndDoesNotNeedRenewedSigningCredentials()
    {
        var (row, authority, holon, issuer, _) = await SeedAsync();
        await Execute(SurrealWriter.UpdateOnly<UserRow>(issuer.ToString("N")).Where(seed => true).Set(value => value.IsActive, false).Build());
        (await _store.CompleteAsync(row, authority, null)).Should().BeTrue();
        (await _store.GetAsync(row.Id))!.State.Should().Be("Completed");
        (await _executor.QuerySingleAsync<SurrealHolonStore.HolonPoco>(SurrealQuery<SurrealHolonStore.HolonPoco>.Key(holon.ToString("N"))))!.TokenId.Should().Be("42");
        (await _store.CompleteAsync(row, authority, null)).Should().BeFalse();
        (await _executor.QueryAsync<FungibleAssetAuthority>(SurrealQuery<FungibleAssetAuthority>.From())).Should().ContainSingle();
    }

    private async Task<(FungibleCreationOperation, FungibleAssetAuthority, Guid, Guid, Guid)> SeedAsync()
    {
        var issuer = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var holon = Guid.NewGuid();
        foreach (var actor in new[] { issuer, owner }) await Execute(SurrealWriter.Create(new UserRow
        { Id = actor.ToString("N"), Username = actor.ToString("N"), IsActive = true, CreatedDate = DateTimeOffset.UtcNow, Email = Guid.NewGuid().ToString("N") + "@example.test", PasswordHash = "fixture-no-login" }));
        await Execute(SurrealWriter.Create(new SurrealHolonStore.HolonPoco { Id = holon.ToString("N"), UserId = $"user:{issuer:N}",
            Name = "Original requested attachment", ProviderName = "SurrealDB", CreatedDate = DateTimeOffset.UtcNow, IsActive = true }));
        var id = FungibleCreationIdentity.OperationId(issuer, null, "creation");
        var intent = new FungibleCreationIntent(id, 0, issuer, null, "Algorand", ChainNetwork.Devnet, "creator",
            SigningContext.ForUser(issuer, Guid.NewGuid()) with { Scope = AzoaScopes.TokenCreateSign }, "Asset", "AST", "1000", 0, false, "", "", "", "");
        var quote = new FungibleCreationQuote("", FungibleCreationIdentity.IntentHash(intent), "genesis", "1000", "100000", "ALGO", 6, "creator", DateTimeOffset.UtcNow.AddMinutes(-1));
        intent = intent with { Quote = quote with { QuoteId = FungibleCreationIdentity.QuoteId(quote) } };
        var command = new FungibleCreationCommand(issuer, null, "creation", Guid.NewGuid(), "creator", false, "CallerWallet", holon, intent);
        var observation = new FungibleCreationObservation("Confirmed", "tx", "genesis", "42", "100", "{\"asset-index\":42}");
        var row = new FungibleCreationOperation { Id = id, ActorUserId = issuer.ToString("N"), RequestHash = CanonicalJson.Hash(command),
            RequestJson = JsonSerializer.Serialize(command), IntentJson = JsonSerializer.Serialize(intent), State = "Confirmed", AssetId = "42",
            ObservationJson = JsonSerializer.Serialize(observation), AcceptedAt = DateTimeOffset.UtcNow.AddMinutes(-2), QuoteExpiresAt = quote.ExpiresAt,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var authority = new FungibleAssetAuthority { Id = Guid.NewGuid().ToString("N"), OperationId = id, IssuerUserId = issuer.ToString("N"),
            ChainType = "Algorand", Network = "Devnet", ProtocolIdentity = "genesis", AssetId = "42", CreatorAddress = "creator", CreationTransactionId = "tx",
            IntentHash = CanonicalJson.Hash(intent), IntentJson = row.IntentJson, EvidenceJson = observation.EvidenceJson!, CreatedAt = DateTimeOffset.UtcNow };
        await Execute(SurrealWriter.Create(row));
        return (row, authority, holon, issuer, owner);
    }

    private static SigningConsentWitness Consent(Guid actor) => new(1,
        new(1, "Jwt", actor, null, null, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10), null, null,
            [], true, TokenUse: AzoaClaims.TokenUseLogin), [AzoaScopes.DappDevelop], []);

    private async Task<(FungibleCreationOperation, FungibleAssetAuthority, PlatformReserveLedger)> SeedGenesisAsync(bool confirmed, bool seedDefinition = true)
    {
        var actor = NodeOperatorIdentity.UserId;
        await Execute(SurrealWriter.Create(new UserRow { Id = actor.ToString("N"), Username = "node-operator", IsActive = true, CreatedDate = DateTimeOffset.UtcNow, Email = Guid.NewGuid().ToString("N") + "@example.test", PasswordHash = "fixture-no-login" }));
        await Execute(SurrealWriter.Create(new AdminBootstrapState { Id = "local", UserId = $"user:{actor:N}", CredentialRevision = 1, SessionRevision = 1, ActivatedAt = DateTimeOffset.UtcNow }));
        const string key = "original-genesis";
        var id = FungibleCreationIdentity.OperationId(actor, null, key);
        var intent = new FungibleCreationIntent(id, 0, actor, null, "Algorand", ChainNetwork.Devnet, "reserve",
            SigningContext.PlatformFor("Algorand") with { Scope = AzoaScopes.TokenCreateSign }, "Unit", "UNT", "1000", 2, false,
            "reserve", "reserve", "reserve", "reserve");
        var quote = new FungibleCreationQuote("", FungibleCreationIdentity.IntentHash(intent), "genesis", "1000", "100000", "ALGO", 6, "reserve", DateTimeOffset.UtcNow.AddMinutes(-1));
        if (confirmed) intent = intent with { Quote = quote with { QuoteId = FungibleCreationIdentity.QuoteId(quote) } };
        var credential = new SigningCredentialWitness(1, "Jwt", actor, null, null, DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(20), null, null, [], false, TokenUse: AzoaClaims.TokenUseNodeOperator,
            OperatorCredentialRevision: 1, OperatorSessionRevision: 1);
        var definitionId = PlatformToken.RecordIdFor("Algorand", "Devnet");
        var purpose = new PlatformGenesisAuthority(1, credential, definitionId, key, PlatformGenesisIdentity.DefinitionHash(intent, key), "genesis");
        var command = new FungibleCreationCommand(actor, null, key, Guid.Empty, "reserve", false, "PlatformReserve", null, intent, GenesisAuthority: purpose);
        if (seedDefinition) await Execute(SurrealWriter.Create(new PlatformToken { Id = definitionId, Chain = "Algorand", Network = "Devnet", Name = "Unit", UnitName = "UNT",
            Decimals = 2, TotalSupply = "1000", ReserveAddress = "reserve", GenesisOperationKey = key, GenesisUserId = $"user:{actor:N}",
            ClaimedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow }));
        var observation = new FungibleCreationObservation("Confirmed", "tx", "genesis", "42", "100", "verified-creation");
        var row = new FungibleCreationOperation { Id = id, ActorUserId = actor.ToString("N"), RequestHash = CanonicalJson.Hash(command), RequestJson = JsonSerializer.Serialize(command),
            IntentJson = JsonSerializer.Serialize(intent), State = confirmed ? "Confirmed" : "AwaitingConsent", AssetId = confirmed ? "42" : null,
            ObservationJson = confirmed ? JsonSerializer.Serialize(observation) : null, QuoteExpiresAt = quote.ExpiresAt, AcceptedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        if (confirmed) await Execute(SurrealWriter.Create(row));
        var authority = new FungibleAssetAuthority { Id = Guid.NewGuid().ToString("N"), OperationId = id, IssuerUserId = actor.ToString("N"), ChainType = "Algorand",
            Network = "Devnet", ProtocolIdentity = "genesis", AssetId = "42", CreatorAddress = "reserve", CreationTransactionId = "tx", IntentJson = row.IntentJson,
            IntentHash = CanonicalJson.Hash(intent), EvidenceJson = observation.EvidenceJson!, CreatedAt = DateTimeOffset.UtcNow };
        var reserve = ReserveAssetRegistrationPlan.Create(new("Algorand", "42", "UNT", 2, new System.Numerics.BigInteger(1000), null),
            new("Algorand", "reserve", true), "Devnet", "genesis", DateTimeOffset.UtcNow);
        return (row, authority, reserve);
    }

    private async Task Execute(SurrealQuery query) => (await _executor.ExecuteAsync(query)).EnsureAllOk();

    private sealed class RecoveryMustNotConsultValueAccess : IValueAccessService
    {
        public Task<AZOA.WebAPI.Models.Kyc.ValueAccessDecision> GetDecisionAsync(Guid participantId, Guid? tenantId = null, CancellationToken ct = default)
            => throw new InvalidOperationException("Submitted-stage recovery must not depend on the actor's current value access.");

        public Task<AZOAResult<bool>> RequireValueAccessAsync(Guid participantId, Guid? tenantId = null, CancellationToken ct = default)
            => throw new InvalidOperationException("Submitted-stage recovery must not depend on the actor's current value access.");
    }
}
