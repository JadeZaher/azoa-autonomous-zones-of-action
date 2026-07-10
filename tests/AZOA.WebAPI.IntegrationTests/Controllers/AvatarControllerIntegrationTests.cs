using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using AZOA.WebAPI.IntegrationTests.Builders;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.IntegrationTests.Controllers;

public class AvatarControllerIntegrationTests : IntegrationTestBase
{
    public AvatarControllerIntegrationTests(AZOATestWebApplicationFactory factory) : base(factory) { }

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
        // Password must satisfy AvatarRegisterValidator (>=8 chars, upper, lower,
        // digit). "secret123" had no uppercase and was rejected at seed time.
        await SeedAvatarAsync(a => a.WithEmail("login@azoa.local").WithPassword("Secret123"));
        var model = new AvatarBuilder().WithEmail("login@azoa.local").WithPassword("Secret123").BuildLoginModel();

        var response = await Client.PostAsJsonAsync("api/avatar/login", model);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<string>(response);
        result!.IsError.Should().BeFalse();
        result.Result.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Login_WithInvalidCredentials_ShouldReturnUnauthorized()
    {
        // Seed a VALID avatar (password must satisfy the register validator),
        // then attempt login with a deliberately wrong password. "right" was a
        // 5-char no-upper-no-digit string rejected at seed time, so the seed —
        // not the login — was failing.
        await SeedAvatarAsync(a => a.WithEmail("wrong@azoa.local").WithPassword("Right123"));
        var model = new AvatarLoginModel { Email = "wrong@azoa.local", Password = "WrongPass1" };

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
        // Username must be 3-50 chars (AvatarRegisterValidator); "a1"/"a2" were
        // 2 chars and rejected at seed time.
        await SeedAvatarAsync(a => a.WithUsername("auser1"));
        await SeedAvatarAsync(a => a.WithUsername("auser2"));

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
        using var avatarClient = Factory.CreateAuthenticatedClientForAvatar(avatar.Id);

        var response = await avatarClient.PutAsJsonAsync($"api/avatar/{avatar.Id}", update);

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
        using var avatarClient = Factory.CreateAuthenticatedClientForAvatar(avatar.Id);

        var response = await avatarClient.DeleteAsync($"api/avatar/{avatar.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Delete_NonExisting_ShouldReturnNotFound()
    {
        var response = await Client.DeleteAsync($"api/avatar/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ═══════════════════════════════════════════════════════════════
    // ═══════════════════════════════════════════════════════════════

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
