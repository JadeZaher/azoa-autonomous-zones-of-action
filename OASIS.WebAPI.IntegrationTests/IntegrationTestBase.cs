using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OASIS.WebAPI.Data;
using OASIS.WebAPI.IntegrationTests.Builders;
using OASIS.WebAPI.IntegrationTests.Factories;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.IntegrationTests;

/// <summary>
/// Base class for integration tests providing shared infrastructure:
/// - Factory lifecycle management (IClassFixture)
/// - Database seeding helpers
/// - Authenticated HTTP client
/// - JSON serialization defaults
/// </summary>
public abstract class IntegrationTestBase : IClassFixture<OASISTestWebApplicationFactory>, IDisposable
{
    protected readonly OASISTestWebApplicationFactory Factory;
    protected readonly HttpClient Client;
    protected readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected IntegrationTestBase(OASISTestWebApplicationFactory factory)
    {
        Factory = factory;
        Client = factory.CreateAuthenticatedClient();
        ClearDatabase();
    }

    public void Dispose()
    {
        ClearDatabase();
        Client.Dispose();
    }

    protected void ClearDatabase()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OASISDbContext>();
        db.Database.EnsureDeleted();
        db.Database.EnsureCreated();
    }

    protected async Task<Avatar> SeedAvatarAsync(Action<AvatarBuilder>? configure = null)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OASISDbContext>();

        var builder = new AvatarBuilder();
        configure?.Invoke(builder);
        var avatar = builder.Build();

        db.Avatars.Add(avatar);
        await db.SaveChangesAsync();
        return avatar;
    }

    protected async Task<Holon> SeedHolonAsync(Action<HolonBuilder>? configure = null)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OASISDbContext>();

        var builder = new HolonBuilder();
        configure?.Invoke(builder);
        var holon = builder.Build();

        db.Holons.Add(holon);
        await db.SaveChangesAsync();
        return holon;
    }

    protected async Task<Wallet> SeedWalletAsync(Action<WalletBuilder>? configure = null)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OASISDbContext>();

        var builder = new WalletBuilder();
        configure?.Invoke(builder);
        var wallet = builder.Build();

        db.Wallets.Add(wallet);
        await db.SaveChangesAsync();
        return wallet;
    }

    protected async Task<STARODK> SeedSTARODKAsync(Action<STARODKBuilder>? configure = null)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OASISDbContext>();

        var builder = new STARODKBuilder();
        configure?.Invoke(builder);
        var odk = builder.Build();

        db.STARODKs.Add(odk);
        await db.SaveChangesAsync();
        return odk;
    }

    protected async Task<BlockchainOperation> SeedBlockchainOperationAsync(Action<BlockchainOperationBuilder>? configure = null)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OASISDbContext>();

        var builder = new BlockchainOperationBuilder();
        configure?.Invoke(builder);
        var op = builder.Build();

        db.BlockchainOperations.Add(op);
        await db.SaveChangesAsync();
        return op;
    }

    protected async Task<T?> ReadResponseAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions);
    }

    protected async Task<OASISResult<T>?> ReadResultAsync<T>(HttpResponseMessage response)
    {
        return await ReadResponseAsync<OASISResult<T>>(response);
    }
}
