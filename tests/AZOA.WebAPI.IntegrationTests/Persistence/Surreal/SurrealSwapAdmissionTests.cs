using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealSwapAdmissionTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private ISurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;
    private SurrealSwapOperationStore _store = null!;
    private SurrealDexQuoteStore _quotes = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "user", "wallet", "consent_grant", "dex_quote", "swap_operation", "swap_stage");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions
        {
            Endpoint = SurrealTestDefaults.Endpoint, Namespace = _namespace, Database = "test",
            User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password,
        });
        _executor = new DefaultSurrealExecutor(_connection);
        _store = new(_executor);
        _quotes = new(_executor);
    }

    public async Task DisposeAsync()
    {
        _connection?.Dispose();
        await SurrealTestSchema.DropAsync(_namespace);
    }

    [Fact]
    public async Task ConcurrentDistinctRequestsCanConsumeQuoteOnlyOnce()
    {
        var (quote, terms, authority) = await SeedAsync();
        var left = Operation(quote, terms, "left");
        var right = Operation(quote, terms, "right");
        var results = await Task.WhenAll(_store.AdmitAsync(left, authority), _store.AdmitAsync(right, authority));
        results.Where(row => row is not null).Should().ContainSingle();
        var winner = results.Single(row => row is not null)!;
        (await _quotes.GetAsync(quote.Id))!.ConsumedByOperationId.Should().Be(SurrealRecordGuid.BareId(winner.Id));
        (await _store.AdmitAsync(SurrealRecordGuid.BareId(winner.Id) == left.Id ? left : right, authority))!
            .IntentHash.Should().Be(winner.IntentHash);
    }

    [Fact]
    public async Task ExactReplayStillRequiresOriginalAuthenticatedActorAndTenantScope()
    {
        var (quote, terms, authority) = await SeedAsync(tenantDriven: true);
        var candidate = Operation(quote, terms, "scope-replay");
        (await _store.AdmitAsync(candidate, authority)).Should().NotBeNull();
        var stranger = authority with { Credential = authority.Credential with { ActorUserId = Guid.NewGuid() } };
        (await _store.AdmitAsync(candidate, stranger)).Should().BeNull();
        (await _store.CancelBeforePreparationAsync(candidate, Consent(stranger))).Should().BeNull();
        (await _store.AdmitAsync(candidate, authority with { Credential = authority.Credential with { TenantId = Guid.NewGuid() } })).Should().BeNull();
        (await _store.AdmitAsync(candidate, authority with { Credential = authority.Credential with { TenantId = null } })).Should().BeNull();
        (await _store.GetAsync(candidate.Id))!.State.Should().Be("Admitted");
        (await _store.CancelBeforePreparationAsync(candidate, Consent(authority, directOwner: true)))!.State.Should().Be("Cancelled");
    }

    [Fact]
    public async Task CancellationTombstoneWinsAgainstDelayedOriginalAdmission()
    {
        var (quote, terms, authority) = await SeedAsync();
        var candidate = Operation(quote, terms, "cancel-first");
        (await _store.CancelBeforePreparationAsync(candidate, Consent(authority)))!.State.Should().Be("Cancelled");
        (await _store.AdmitAsync(candidate, authority))!.State.Should().Be("Cancelled");
        (await _quotes.GetAsync(quote.Id))!.ConsumedByOperationId.Should().BeNull();
    }

    [Fact]
    public async Task CancelRacingAdmissionEndsUnsentWithoutChangingOriginalRequest()
    {
        var (quote, terms, authority) = await SeedAsync();
        var candidate = Operation(quote, terms, "race");
        await Task.WhenAll(_store.AdmitAsync(candidate, authority), _store.CancelBeforePreparationAsync(candidate, Consent(authority)));
        var saved = (await _store.GetAsync(candidate.Id))!;
        saved.State.Should().Be("Cancelled");
        saved.CurrentStageId.Should().BeNull();
        saved.IntentJson.Should().Be(candidate.IntentJson);
    }

    [Fact]
    public async Task OriginalOwnerCanCancelUnsentAfterWalletDeletionButCannotAdmitNewSpend()
    {
        var (quote, terms, authority) = await SeedAsync();
        var candidate = Operation(quote, terms, "owner-cancel");
        (await _store.AdmitAsync(candidate, authority)).Should().NotBeNull();
        (await _executor.ExecuteAsync(SurrealWriter.DeleteOnly<Wallet>(terms.Owner.WalletId.ToString("N")).Where(seed => true).Build())).EnsureAllOk();
        (await _store.CancelBeforePreparationAsync(candidate, Consent(authority)))!.State.Should().Be("Cancelled");
        var otherQuote = await SeedQuoteAsync(terms);
        (await _store.AdmitAsync(Operation(otherQuote, terms, "new-spend"), authority)).Should().BeNull();
        (await _quotes.GetAsync(otherQuote.Id))!.ConsumedByOperationId.Should().BeNull();
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("logout")]
    [InlineData("wallet-owner")]
    [InlineData("expired-token")]
    [InlineData("wrong-principal")]
    public async Task CurrentAuthorityRefusalRollsBackQuoteConsumption(string change)
    {
        var (quote, terms, authority) = await SeedAsync();
        switch (change)
        {
            case "disabled":
                (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(quote.ActorUserId).Where(seed => true)
                    .Set(row => row.IsActive, false).Build())).EnsureAllOk();
                break;
            case "logout":
                (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(quote.ActorUserId).Where(seed => true)
                    .Set(row => row.AuthNotBefore, DateTimeOffset.UtcNow).Build())).EnsureAllOk();
                break;
            case "wallet-owner":
                (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<Wallet>(terms.Owner.WalletId.ToString("N")).Where(seed => true)
                    .Set(row => row.UserId, SurrealLink.ToLink("user", Guid.NewGuid().ToString("N"))!).Build())).EnsureAllOk();
                break;
            case "expired-token": authority = authority with { Credential = authority.Credential with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) } }; break;
            default: authority = authority with { Credential = authority.Credential with { ActorUserId = Guid.NewGuid() } }; break;
        }
        var candidate = Operation(quote, terms, change);
        (await _store.AdmitAsync(candidate, authority)).Should().BeNull();
        (await _store.GetAsync(candidate.Id)).Should().BeNull();
        (await _quotes.GetAsync(quote.Id))!.ConsumedByOperationId.Should().BeNull();
    }

    [Fact]
    public async Task ReplayReadsOriginalBeforeCurrentCredentialExpiryAndQuoteExpiry()
    {
        var (quote, terms, authority) = await SeedAsync();
        var candidate = Operation(quote, terms, "replay");
        var first = await _store.AdmitAsync(candidate, authority);
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<DexQuoteRecord>(quote.Id).Where(seed => true)
            .Set(row => row.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)).Build())).EnsureAllOk();
        var replay = await _store.AdmitAsync(candidate,
            authority with { Credential = authority.Credential with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) } });
        replay!.IntentJson.Should().Be(first!.IntentJson);
        replay.Version.Should().Be(first.Version);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("scope")]
    [InlineData("participation")]
    public async Task TenantMustRetainExactExplicitLiveSigningGrant(string change)
    {
        var (quote, terms, authority) = await SeedAsync(true);
        var update = SurrealWriter.UpdateOnly<ConsentGrant>(authority.Grants.Single().Id.ToString("N"))
            .Where(row => row.Origin == "UserExplicit" && row.Scopes == "swap:sign" && row.RevokedAt == null);
        switch (change)
        {
            case "revoked": update = update.Set(row => row.RevokedAt, DateTimeOffset.UtcNow); break;
            case "expired": update = update.Set(row => row.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)); break;
            case "scope": update = update.Set(row => row.Scopes, "action:execute"); break;
            default: update = update.Set(row => row.Origin, "Participation"); break;
        }
        (await _executor.ExecuteAsync(update.Build())).EnsureAllOk();
        var candidate = Operation(quote, terms, change);
        (await _store.AdmitAsync(candidate, authority)).Should().BeNull();
        (await _quotes.GetAsync(quote.Id))!.ConsumedByOperationId.Should().BeNull();
        var owner = Consent(authority, directOwner: true);
        (await _store.CancelBeforePreparationAsync(candidate, owner))!.State.Should().Be("Cancelled");
    }

    [Fact]
    public async Task ValidTenantGrantCanAdmitAndChangedRequestCannotReplay()
    {
        var (quote, terms, authority) = await SeedAsync(true);
        var candidate = Operation(quote, terms, "tenant-valid");
        (await _store.AdmitAsync(candidate, authority)).Should().NotBeNull();
        var changed = JsonSerializer.Deserialize<SwapOperationRecord>(JsonSerializer.Serialize(candidate))!;
        var intent = JsonSerializer.Deserialize<SwapIntent>(changed.IntentJson)!;
        changed.RequestHash = CanonicalJson.Hash(new { DifferentConsent = true });
        changed.IntentJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(intent with { RequestHash = changed.RequestHash }));
        changed.IntentHash = CanonicalJson.HashCanonical(changed.IntentJson);
        (await _store.AdmitAsync(changed, authority)).Should().BeNull();
    }

    [Theory]
    [InlineData("wallet")]
    [InlineData("scope")]
    [InlineData("workflow")]
    [InlineData("grant-time")]
    public async Task SharedAuthorityMustMatchExactSwapWalletScopeAndWorkflow(string change)
    {
        var (quote, terms, authority) = await SeedAsync(tenantDriven: change == "grant-time");
        authority = change switch
        {
            "wallet" => authority with { Wallet = authority.Wallet with { Id = Guid.NewGuid() } },
            "scope" => authority with { RequiredScopes = [AzoaScopes.TokenCreateSign] },
            "grant-time" => authority with { Grants = [authority.Grants.Single() with { GrantedAt = DateTimeOffset.UtcNow }] },
            _ => authority with { Kind = "Workflow" },
        };
        var candidate = Operation(quote, terms, "shared-" + change);
        (await _store.AdmitAsync(candidate, authority)).Should().BeNull();
        (await _quotes.GetAsync(quote.Id))!.ConsumedByOperationId.Should().BeNull();
        (await _store.GetAsync(candidate.Id)).Should().BeNull();
    }

    private async Task<(DexQuoteRecord Quote, DexQuoteTerms Terms, SigningAdmissionAuthority Authority)> SeedAsync(bool tenantDriven = false)
    {
        var quote = SurrealDexQuoteStoreTests.Candidate();
        var terms = JsonSerializer.Deserialize<DexQuoteTerms>(quote.QuoteJson)!;
        var tenant = tenantDriven ? Guid.NewGuid() : (Guid?)null;
        if (tenant is not null)
        {
            terms = terms with { TenantId = tenant };
            quote.TenantId = tenant.Value.ToString("N");
            quote.QuoteJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(terms));
            quote.QuoteHash = CanonicalJson.HashCanonical(quote.QuoteJson);
            (await _executor.ExecuteAsync(SurrealWriter.Create(new User
            {
                Id = quote.TenantId, Username = quote.TenantId, Email = quote.TenantId + "@example.test",
                PasswordHash = "fixture-no-login", IsActive = true,
            }))).EnsureAllOk();
        }
        (await _executor.ExecuteAsync(SurrealWriter.Create(new User
        {
            Id = quote.ActorUserId, Username = quote.ActorUserId, Email = quote.ActorUserId + "@example.test",
            PasswordHash = "fixture-no-login", IsActive = true,
        }))).EnsureAllOk();
        (await _executor.ExecuteAsync(SurrealWriter.Create(new Wallet
        {
            Id = terms.Owner.WalletId.ToString("N"), UserId = SurrealLink.ToLink("user", quote.ActorUserId)!,
            Address = terms.Owner.Address, ChainType = terms.Network.ChainType, WalletType = Wallet.WalletTypeKind.External,
        }))).EnsureAllOk();
        (await _quotes.ClaimAsync(quote)).Should().NotBeNull();
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var credential = new SigningCredentialWitness(1, "Jwt", terms.ActorUserId, tenant, null, now,
            now.AddMinutes(11), null, null, [AzoaScopes.SwapSign], tenant is null,
            TokenUse: tenant is null ? AzoaClaims.TokenUseLogin : AzoaClaims.TokenUseChild);
        var authority = new SigningAdmissionAuthority(1, "DirectJwt", credential,
            new(terms.Owner.WalletId, terms.ActorUserId, terms.Owner.Address, terms.Network.ChainType), [AzoaScopes.SwapSign], []);
        if (tenant is not null)
        {
            var grant = new ConsentGrant
            {
                Id = Guid.NewGuid().ToString("N"), GrantorUserId = SurrealLink.ToLink("user", quote.ActorUserId)!,
                TenantId = SurrealLink.ToLink("user", quote.TenantId)!, Scopes = "swap:sign", Origin = "UserExplicit", GrantedAt = now,
            };
            (await _executor.ExecuteAsync(SurrealWriter.Create(grant))).EnsureAllOk();
            authority = authority with { Grants = [new(Guid.Parse(grant.Id), AzoaScopes.SwapSign, grant.Scopes, grant.GrantedAt, grant.ExpiresAt)] };
        }
        return (quote, terms, authority);
    }

    private static SigningConsentWitness Consent(SigningAdmissionAuthority authority, bool directOwner = false)
        => new(1, directOwner ? authority.Credential with
        {
            TenantId = null, TokenUse = AzoaClaims.TokenUseLogin, OwnerAuthority = true,
        } : authority.Credential, [AzoaScopes.SwapSign], directOwner ? [] : authority.Grants);

    private async Task<DexQuoteRecord> SeedQuoteAsync(DexQuoteTerms terms)
    {
        var quote = new DexQuoteRecord
        {
            Id = Guid.NewGuid().ToString("N"), ActorUserId = terms.ActorUserId.ToString("N"),
            TenantId = terms.TenantId?.ToString("N") ?? "", ExpiresAt = terms.ExpiresAt,
            CreatedAt = DateTimeOffset.UtcNow, QuoteJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(terms)),
            QuoteHash = CanonicalJson.Hash(terms),
        };
        (await _quotes.ClaimAsync(quote)).Should().NotBeNull();
        return quote;
    }

    private static SwapOperationRecord Operation(DexQuoteRecord quote, DexQuoteTerms terms, string requestKey)
    {
        var keyHash = CanonicalJson.Hash(new { Key = requestKey });
        var requestHash = CanonicalJson.Hash(new { quote.Id, quote.QuoteHash });
        var id = SwapOperationIdentity.Operation(terms.ActorUserId, terms.TenantId, keyHash);
        var intent = new SwapIntent(1, id, quote.Id, quote.QuoteHash, terms, requestHash);
        var json = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(intent));
        return new SwapOperationRecord
        {
            Id = id, ActorUserId = quote.ActorUserId, TenantId = quote.TenantId, RequestKeyHash = keyHash,
            RequestHash = requestHash, QuoteId = quote.Id, QuoteHash = quote.QuoteHash,
            IntentHash = CanonicalJson.HashCanonical(json), IntentJson = json,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, NextAttemptAt = DateTimeOffset.UtcNow,
        };
    }
}
