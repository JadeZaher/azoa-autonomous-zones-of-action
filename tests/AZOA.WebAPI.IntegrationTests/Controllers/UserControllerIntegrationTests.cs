using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using AZOA.WebAPI.IntegrationTests.Builders;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.IntegrationTests.Controllers;

public class UserControllerIntegrationTests : IntegrationTestBase
{
    public UserControllerIntegrationTests(AZOATestWebApplicationFactory factory) : base(factory) { }

    // ═══════════════════════════════════════════════════════════════
    // REGISTER
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Register_ShouldCreateUser_ReturnOk()
    {
        var model = new UserBuilder().WithUsername("neo").WithEmail("neo@matrix.com").BuildRegisterModel();

        var response = await Client.PostAsJsonAsync("api/user/register", model);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<User>(response);
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
        // Password must satisfy UserRegisterValidator (>=8 chars, upper, lower,
        // digit). "secret123" had no uppercase and was rejected at seed time.
        await SeedUserAsync(a => a.WithEmail("login@azoa.local").WithPassword("Secret123"));
        var model = new UserBuilder().WithEmail("login@azoa.local").WithPassword("Secret123").BuildLoginModel();

        var response = await Client.PostAsJsonAsync("api/user/login", model);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<string>(response);
        result!.IsError.Should().BeFalse();
        result.Result.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Login_WithInvalidCredentials_ShouldReturnUnauthorized()
    {
        // Seed a VALID user (password must satisfy the register validator),
        // then attempt login with a deliberately wrong password. "right" was a
        // 5-char no-upper-no-digit string rejected at seed time, so the seed —
        // not the login — was failing.
        await SeedUserAsync(a => a.WithEmail("wrong@azoa.local").WithPassword("Right123"));
        var model = new UserLoginModel { Email = "wrong@azoa.local", Password = "WrongPass1" };

        var response = await Client.PostAsJsonAsync("api/user/login", model);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ═══════════════════════════════════════════════════════════════
    // GET / GETALL
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Get_ExistingUser_ShouldReturnUser()
    {
        var user = await SeedUserAsync();

        var response = await Client.GetAsync($"api/user/{user.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<User>(response);
        result!.Result!.Id.Should().Be(user.Id);
    }

    [Fact]
    public async Task Get_NonExistingUser_ShouldReturnNotFound()
    {
        var response = await Client.GetAsync($"api/user/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAll_ShouldReturnList()
    {
        // Username must be 3-50 chars (UserRegisterValidator); "a1"/"a2" were
        // 2 chars and rejected at seed time.
        await SeedUserAsync(a => a.WithUsername("auser1"));
        await SeedUserAsync(a => a.WithUsername("auser2"));

        var response = await Client.GetAsync("api/user");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<IEnumerable<User>>(response);
        result!.Result.Should().HaveCount(2);
    }

    // ═══════════════════════════════════════════════════════════════
    // UPDATE
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_ShouldModifyUser()
    {
        var user = await SeedUserAsync();
        var update = new UserUpdateModel { FirstName = "Updated" };
        using var userClient = Factory.CreateAuthenticatedClientForUser(user.Id);

        var response = await userClient.PutAsJsonAsync($"api/user/{user.Id}", update);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadResultAsync<User>(response);
        result!.Result!.FirstName.Should().Be("Updated");
    }

    // ═══════════════════════════════════════════════════════════════
    // DELETE
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Delete_ShouldRemoveUser()
    {
        var user = await SeedUserAsync();
        using var userClient = Factory.CreateAuthenticatedClientForUser(user.Id);

        var response = await userClient.DeleteAsync($"api/user/{user.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Delete_NonExisting_ShouldReturnNotFound()
    {
        var response = await Client.DeleteAsync($"api/user/{Guid.NewGuid()}");

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
        var response = await unauthClient.GetAsync($"api/user/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
