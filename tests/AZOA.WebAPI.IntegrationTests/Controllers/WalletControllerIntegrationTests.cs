using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using AZOA.WebAPI.IntegrationTests.Builders;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.IntegrationTests.Controllers;

public class WalletControllerIntegrationTests : IntegrationTestBase
{
    public WalletControllerIntegrationTests(AZOATestWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task Create_ShouldReturnWalletWithAvatarId()
    {
        // Address must satisfy WalletCreateModelValidator (^[a-zA-Z0-9]+$): the
        // prior "sol_addr_1" had underscores and was rejected with a 400.
        var model = new WalletCreateModel { ChainType = "Solana", Address = "soladdr1", Label = "Main" };

        var response = await Client.PostAsJsonAsync("api/wallet", model);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<Wallet>(response);
        result!.IsError.Should().BeFalse();
        result.Result!.ChainType.Should().Be("Solana");
        result.Result.Address.Should().Be("soladdr1");
    }

    [Fact]
    public async Task Create_DuplicateAddress_ReturnsBadRequest()
    {
        var avatarId = Guid.Parse(TestAuthHandler.DefaultAvatarId);
        // Address must be alphanumeric (WalletCreateModelValidator); both the
        // seed and the duplicate attempt use the same value to trigger the
        // duplicate-address 400. "dup_addr" had an underscore and failed at seed.
        await SeedWalletAsync(w => w.ForAvatar(avatarId).OnChain("Solana").WithAddress("dupaddr"));

        var model = new WalletCreateModel { ChainType = "Solana", Address = "dupaddr" };
        var response = await Client.PostAsJsonAsync("api/wallet", model);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Get_ExistingWallet_ShouldReturnWallet()
    {
        var wallet = await SeedWalletAsync(w => w.ForAvatar(Guid.Parse(TestAuthHandler.DefaultAvatarId)));

        var response = await Client.GetAsync($"api/wallet/{wallet.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<Wallet>(response);
        result!.Result!.Id.Should().Be(wallet.Id);
    }

    [Fact]
    public async Task Query_WithFilters_ShouldReturnFiltered()
    {
        var avatarId = Guid.Parse(TestAuthHandler.DefaultAvatarId);
        await SeedWalletAsync(w => w.ForAvatar(avatarId).OnChain("Solana").AsDefault());
        await SeedWalletAsync(w => w.ForAvatar(avatarId).OnChain("Algorand"));

        var response = await Client.GetAsync("api/wallet?ChainType=Solana&IsDefault=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<IEnumerable<Wallet>>(response);
        result!.Result.Should().ContainSingle(w => w.ChainType == "Solana" && w.IsDefault);
    }

    [Fact]
    public async Task Update_ShouldModifyLabel()
    {
        var wallet = await SeedWalletAsync(w => w.ForAvatar(Guid.Parse(TestAuthHandler.DefaultAvatarId)).WithLabel("Old"));
        var update = new WalletUpdateModel { Label = "NewLabel" };

        var response = await Client.PutAsJsonAsync($"api/wallet/{wallet.Id}", update);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<Wallet>(response);
        result!.Result!.Label.Should().Be("NewLabel");
    }

    [Fact]
    public async Task SetDefault_ShouldSwapDefaultFlag()
    {
        var avatarId = Guid.Parse(TestAuthHandler.DefaultAvatarId);
        var prev    = await SeedWalletAsync(w => w.ForAvatar(avatarId).OnChain("Solana").AsDefault());
        var current = await SeedWalletAsync(w => w.ForAvatar(avatarId).OnChain("Solana"));

        var response = await Client.PostAsJsonAsync($"api/wallet/{current.Id}/set-default", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<bool>(response);
        result!.IsError.Should().BeFalse();
        result.Result.Should().BeTrue();

        // Verify via HTTP GET — prev should no longer be default; current should be.
        var prevGet = await Client.GetAsync($"api/wallet/{prev.Id}");
        var prevResult = await ReadResultAsync<Wallet>(prevGet);
        prevResult!.Result!.IsDefault.Should().BeFalse();

        var curGet = await Client.GetAsync($"api/wallet/{current.Id}");
        var curResult = await ReadResultAsync<Wallet>(curGet);
        curResult!.Result!.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_ShouldRemoveWallet()
    {
        var wallet = await SeedWalletAsync(w => w.ForAvatar(Guid.Parse(TestAuthHandler.DefaultAvatarId)));

        var response = await Client.DeleteAsync($"api/wallet/{wallet.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetPortfolio_ShouldReturnStubWithNfts()
    {
        var avatarId = Guid.Parse(TestAuthHandler.DefaultAvatarId);
        var wallet = await SeedWalletAsync(w => w.ForAvatar(avatarId).OnChain("Solana"));
        await SeedHolonAsync(h => h.ForAvatar(avatarId).AsAsset("NFT").WithName("MyNFT"));

        var response = await Client.GetAsync($"api/wallet/{wallet.Id}/portfolio");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<PortfolioResult>(response);
        result!.IsError.Should().BeFalse();
        result.Result!.WalletId.Should().Be(wallet.Id);
        result.Result.Symbol.Should().Be("SOL");
        result.Result.Nfts.Should().ContainSingle(n => n.Name == "MyNFT");
    }

    [Fact]
    public async Task Get_WithoutAuth_ShouldReturnUnauthorized()
    {
        var unauthClient = Factory.CreateClient();
        var response = await unauthClient.GetAsync($"api/wallet/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
