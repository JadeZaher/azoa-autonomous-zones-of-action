using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Tests.Architecture;

namespace AZOA.WebAPI.Tests.Controllers;

/// <summary>QA H5 / M5: every user-returning response, serialized the way the API serializes it, carries no credential material; the directory is operator-only.</summary>
public sealed class UserResponseCredentialTests
{
    private static readonly Guid Self = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private const string Hash = "$2a$11$abcdefghijklmnopqrstuuW0yG8m2lRk1n1u8m2vX0qkq2fE9b3hS";

    // Mirrors Program.cs AddJsonOptions: web defaults + string enums.
    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static User StoredUser() => new()
    {
        Id = Self,
        Username = "wallet-solana-abc-1234",
        Email = "owner@example.com",
        PasswordHash = Hash,
        AuthNotBefore = DateTime.UtcNow,
    };

    public static TheoryData<string> Responses => new() { "get-self", "register", "update", "assign-role" };

    [Theory]
    [MemberData(nameof(Responses))]
    public async Task User_responses_never_serialize_the_password_hash(string response)
    {
        var manager = new Mock<IUserManager>();
        var stored = AZOAResult<IUser>.Success(StoredUser());
        manager.Setup(m => m.GetAsync(Self, It.IsAny<AZOARequest?>())).ReturnsAsync(stored);
        manager.Setup(m => m.RegisterAsync(It.IsAny<UserRegisterModel>(), It.IsAny<AZOARequest?>())).ReturnsAsync(stored);
        manager.Setup(m => m.UpdateAsync(Self, It.IsAny<UserUpdateModel>(), Self, It.IsAny<AZOARequest?>())).ReturnsAsync(stored);
        manager.Setup(m => m.AssignDappRoleAsync(Self, It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(stored);
        var controller = new UserController(manager.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Self.ToString())], "Test")),
                },
            },
        };

        var result = response switch
        {
            "get-self" => (await controller.Get(Self, null)).Result,
            "register" => (await controller.Register(new UserRegisterModel(), null)).Result,
            "update" => (await controller.Update(Self, new UserUpdateModel { Title = "t" }, null)).Result,
            _ => (await controller.AssignDappRole(Self, new UserRoleAssignmentModel { Role = "dapp:user" })).Result,
        };

        var json = JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value, ApiJson);
        json.Should().Contain("owner@example.com", "the caller's own view still carries its profile");
        json.Should().NotContainAny(Hash, "passwordHash", "authNotBefore");
    }

    [Fact]
    public void The_user_directory_is_operator_only()
    {
        var directory = typeof(UserController).GetMethod(nameof(UserController.GetAll))!;

        GateInventory.Policies(directory).Should().Equal("Operator");
    }
}
