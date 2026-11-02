using AZOA.WebAPI.Models;
using AZOA.WebAPI.Validators;
using FluentAssertions;
using Xunit;

namespace AZOA.WebAPI.Tests.Validators;

public sealed class UserLoginValidatorTests
{
    private const string UserId = "10000000-0000-4000-8000-000000000001";
    private const string EmptyId = "00000000-0000-0000-0000-000000000000";

    [Theory]
    [InlineData(UserId, null, "chosen-password", true)]
    [InlineData(UserId, "", "chosen-password", true)]
    [InlineData(UserId, "  ", "chosen-password", true)]
    [InlineData(null, "member@example.test", "chosen-password", true)]
    [InlineData(null, null, "chosen-password", false)]
    [InlineData(null, "", "chosen-password", false)]
    [InlineData(null, "  ", "chosen-password", false)]
    [InlineData(null, "not-an-email", "chosen-password", false)]
    [InlineData(UserId, "member@example.test", "chosen-password", false)]
    [InlineData(UserId, "not-an-email", "chosen-password", false)]
    [InlineData(EmptyId, "", "chosen-password", false)]
    [InlineData(EmptyId, "member@example.test", "chosen-password", false)]
    [InlineData(UserId, "", null, false)]
    [InlineData(UserId, "", "", false)]
    [InlineData(UserId, "", "  ", false)]
    [InlineData(null, "member@example.test", "", false)]
    [InlineData(null, "member@example.test", "  ", false)]
    public void Login_requires_one_nonempty_identity_and_preserves_required_password(
        string? userId, string? email, string? password, bool accepted)
    {
        var model = new UserLoginModel
        {
            UserId = userId is null ? null : Guid.Parse(userId),
            Email = email!,
            Password = password!,
        };
        new UserLoginValidator().Validate(model).IsValid.Should().Be(accepted);
    }
}
