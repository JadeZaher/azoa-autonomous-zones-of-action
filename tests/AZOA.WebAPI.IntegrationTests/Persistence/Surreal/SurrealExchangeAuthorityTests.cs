using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Signing;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using UserRow = AZOA.WebAPI.Persistence.SurrealDb.Models.User;
using WalletRow = AZOA.WebAPI.Persistence.SurrealDb.Models.Wallet;
using GrantRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentGrant;
using KeyRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ApiKey;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealExchangeAuthorityTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private ISurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "user", "wallet", "consent_grant", "api_key");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions { Endpoint = SurrealTestDefaults.Endpoint,
            Namespace = _namespace, Database = "test", User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password });
        _executor = new DefaultSurrealExecutor(_connection);
    }

    public async Task DisposeAsync()
    {
        try { await SurrealTestSchema.DropAsync(_namespace); }
        finally { _connection?.Dispose(); }
    }

    [Fact]
    public async Task ActualTenantAndDistinctParticipantAuthorizeTogetherWithoutImpersonation()
    {
        var authority = await SeedAsync();
        authority.Tenant.Credential.ActorUserId.Should().NotBe(authority.ParticipantUserId);
        await ApplyAsync(authority);
        (await WalletAsync(authority))!.Label.Should().Be("authorized-effect");
    }

    [Theory]
    [InlineData("key")]
    [InlineData("grant")]
    [InlineData("participant-watermark")]
    [InlineData("tenant-watermark")]
    [InlineData("wallet-owner")]
    [InlineData("grant-origin")]
    [InlineData("grant-expiry")]
    public async Task CapturedWitnessCannotAuthorizeAfterEitherSideChanges(string change)
    {
        var authority = await SeedAsync();
        var now = DateTimeOffset.UtcNow;
        if (change == "key") await Execute(SurrealWriter.UpdateOnly<KeyRow>(authority.Tenant.Credential.ApiKeyId!.Value.ToString("N")).Set(row => row.RevokedAt, now).Build());
        else if (change == "grant") await Execute(SurrealWriter.UpdateOnly<GrantRow>(authority.Grant.Id.ToString("N")).Set(row => row.RevokedAt, now).Build());
        else if (change == "participant-watermark") await Execute(SurrealWriter.UpdateOnly<UserRow>(authority.ParticipantUserId.ToString("N")).Set(row => row.AuthNotBefore, now).Build());
        else if (change == "tenant-watermark") await Execute(SurrealWriter.UpdateOnly<UserRow>(authority.Tenant.Credential.ActorUserId.ToString("N")).Set(row => row.AuthNotBefore, now).Build());
        else if (change == "wallet-owner") await Execute(SurrealWriter.UpdateOnly<WalletRow>(authority.Wallet.Id.ToString("N")).Set(row => row.UserId, $"user:{authority.Tenant.Credential.ActorUserId:N}").Build());
        else if (change == "grant-origin") await Execute(SurrealWriter.UpdateOnly<GrantRow>(authority.Grant.Id.ToString("N")).Set(row => row.Origin, "Participation").Build());
        else
        {
            var expired = now.AddMinutes(-1);
            await Execute(SurrealWriter.UpdateOnly<GrantRow>(authority.Grant.Id.ToString("N")).Set(row => row.ExpiresAt, expired).Build());
            authority = authority with { Grant = authority.Grant with { ExpiresAt = expired } };
        }

        await FluentActions.Awaiting(() => ApplyAsync(authority)).Should().ThrowAsync<SurrealStatementException>();

        (await WalletAsync(authority))!.Label.Should().BeNull("the dependent effect rolls back with either authority fence");
    }

    private async Task ApplyAsync(ExchangeParticipantAuthority authority)
    {
        var guards = ExchangeAuthorityFence.Create(authority, "exchange_fixture")!;
        // raw: the fixture effect must share the actual two-party authority transaction.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(guards);
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.UpdateOnly<WalletRow>(authority.Wallet.Id.ToString("N"))
            .Set(row => row.Label, "authorized-effect").Build(), "exchange_effect"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        await Execute(SurrealQuery.Combine(statements.ToArray()));
    }

    private async Task<ExchangeParticipantAuthority> SeedAsync()
    {
        var tenant = Guid.NewGuid(); var participant = Guid.NewGuid(); var wallet = Guid.NewGuid(); var key = Guid.NewGuid(); var grant = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        foreach (var id in new[] { tenant, participant })
            await Execute(SurrealWriter.Create(new UserRow { Id = id.ToString("N"), Username = id.ToString("N"), Email = $"{id:N}@example.test", IsActive = true }));
        var keyHash = new string('a', 64);
        await Execute(SurrealWriter.Create(new KeyRow { Id = key.ToString("N"), UserId = $"user:{tenant:N}", KeyHash = keyHash,
            KeyPrefix = "fixture", IsActive = true, Scopes = [AzoaScopes.SwapSign], CreatedDate = now }));
        await Execute(SurrealWriter.Create(new WalletRow { Id = wallet.ToString("N"), UserId = $"user:{participant:N}", ChainType = "Algorand", Address = "participant-address" }));
        await Execute(SurrealWriter.Create(new GrantRow { Id = grant.ToString("N"), GrantorUserId = $"user:{participant:N}", TenantId = $"user:{tenant:N}",
            Scopes = AzoaScopes.SwapSign, Origin = "UserExplicit", GrantedAt = now }));
        var credential = new SigningCredentialWitness(1, "ApiKey", tenant, null, null, null, null, key, keyHash,
            [AzoaScopes.SwapSign], false, [AzoaScopes.SwapSign]);
        return new(1, new(1, credential, [AzoaScopes.SwapSign], []), participant, null,
            new(wallet, participant, "participant-address", "Algorand"), new(grant, AzoaScopes.SwapSign, AzoaScopes.SwapSign, now, null));
    }

    private Task<WalletRow?> WalletAsync(ExchangeParticipantAuthority authority)
        => _executor.QuerySingleAsync<WalletRow>(SurrealQuery<WalletRow>.Key(authority.Wallet.Id.ToString("N")));
    private async Task Execute(SurrealQuery query) => (await _executor.ExecuteAsync(query)).EnsureAllOk();
}
