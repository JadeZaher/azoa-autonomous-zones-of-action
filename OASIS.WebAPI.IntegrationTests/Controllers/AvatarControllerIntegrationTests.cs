using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using OASIS.WebAPI.IntegrationTests.Builders;
using OASIS.WebAPI.IntegrationTests.Factories;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.IntegrationTests.Controllers;

public class AvatarControllerIntegrationTests : IntegrationTestBase
{
    public AvatarControllerIntegrationTests(OASISTestWebApplicationFactory factory) : base(factory) { }

    // ═══════════════════════════════════════════════════════════════
    // REGISTER
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Register_ShouldCreateAvatar_ReturnOk()
    {
        var model = new AvatarBuilder().WithUsername("neo").WithEmail("neo@matrix.com").BuildRegisterModel();

        var response = await Client.PostAsJsonAsync("api/avatar/register", model);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<Avatar>(response);
        result.Should().NotBeNull();
        result!.IsError.Should().BeFalse();
        result.Result!.Username.Should().Be("neo");
    }

    // ═══════════════════════════════════════════════════════════════
    // LOGIN
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Login_WithValidCredentials_ShouldReturnToken()
    {
        await SeedAvatarAsync(a => a.WithEmail("login@oasis.local").WithPassword("secret123"));
        var model = new AvatarBuilder().WithEmail("login@oasis.local").WithPassword("secret123").BuildLoginModel();

        var response = await Client.PostAsJsonAsync("api/avatar/login", model);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<string>(response);
        result!.IsError.Should().BeFalse();
        result.Result.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Login_WithInvalidCredentials_ShouldReturnUnauthorized()
    {
        await SeedAvatarAsync(a => a.WithEmail("wrong@oasis.local").WithPassword("right"));
        var model = new AvatarLoginModel { Email = "wrong@oasis.local", Password = "bad" };

        var response = await Client.PostAsJsonAsync("api/avatar/login", model);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ═══════════════════════════════════════════════════════════════
    // GET / GETALL
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Get_ExistingAvatar_ShouldReturnAvatar()
    {
        var avatar = await SeedAvatarAsync();

        var response = await Client.GetAsync($"api/avatar/{avatar.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<Avatar>(response);
        result!.Result!.Id.Should().Be(avatar.Id);
    }

    [Fact]
    public async Task Get_NonExistingAvatar_ShouldReturnNotFound()
    {
        var response = await Client.GetAsync($"api/avatar/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAll_ShouldReturnList()
    {
        await SeedAvatarAsync(a => a.WithUsername("a1"));
        await SeedAvatarAsync(a => a.WithUsername("a2"));

        var response = await Client.GetAsync("api/avatar");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<IEnumerable<Avatar>>(response);
        result!.Result.Should().HaveCount(2);
    }

    // ═══════════════════════════════════════════════════════════════
    // UPDATE
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_ShouldModifyAvatar()
    {
        var avatar = await SeedAvatarAsync();
        var update = new AvatarUpdateModel { FirstName = "Updated" };

        var response = await Client.PutAsJsonAsync($"api/avatar/{avatar.Id}", update);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<Avatar>(response);
        result!.Result!.FirstName.Should().Be("Updated");
    }

    // ═══════════════════════════════════════════════════════════════
    // DELETE
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Delete_ShouldRemoveAvatar()
    {
        var avatar = await SeedAvatarAsync();

        var response = await Client.DeleteAsync($"api/avatar/{avatar.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Delete_NonExisting_ShouldReturnNotFound()
    {
        var response = await Client.DeleteAsync($"api/avatar/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ═══════════════════════════════════════════════════════════════
    // WALLETS
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task AddWallet_ShouldAttachToAvatar()
    {
        var avatar = await SeedAvatarAsync();
        var wallet = new WalletBuilder().ForAvatar(avatar.Id).OnChain("Solana").Build();

        var response = await Client.PostAsJsonAsync($"api/avatar/{avatar.Id}/wallets", wallet);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<Wallet>(response);
        result!.Result!.AvatarId.Should().Be(avatar.Id);
    }

    [Fact]
    public async Task GetWallets_ShouldReturnAvatarWallets()
    {
        var avatar = await SeedAvatarAsync();
        await SeedWalletAsync(w => w.ForAvatar(avatar.Id));
        await SeedWalletAsync(w => w.ForAvatar(avatar.Id));

        var response = await Client.GetAsync($"api/avatar/{avatar.Id}/wallets");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<IEnumerable<Wallet>>(response);
        result!.Result.Should().HaveCount(2);
    }

    [Fact]
    public async Task RemoveWallet_ShouldDeleteWallet()
    {
        var avatar = await SeedAvatarAsync();
        var wallet = await SeedWalletAsync(w => w.ForAvatar(avatar.Id));

        var response = await Client.DeleteAsync($"api/avatar/{avatar.Id}/wallets/{wallet.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ═══════════════════════════════════════════════════════════════
    // AUTH
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Get_WithoutAuth_ShouldReturnUnauthorized()
    {
        var unauthClient = Factory.CreateClient();
        var response = await unauthClient.GetAsync($"api/avatar/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
