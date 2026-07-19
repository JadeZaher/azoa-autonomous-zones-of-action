using AZOA.WebAPI.Services.Admin;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Services.Admin;

public sealed class NodeOperatorIdentityTests
{
    private const string ValidPassword = "azoa-generated-operator-secret-2026!";

    [Fact]
    public void ReservedIdentity_IsStableAndOutsideOrdinaryEmailSpace()
    {
        NodeOperatorIdentity.AvatarId.Should().Be(Guid.Parse("a20a0000-0000-4000-8000-000000000001"));
        NodeOperatorIdentity.ReservedEmail.Should().Be("node-operator@azoa.invalid");
    }

    [Fact]
    public void NormalizeUsername_ProducesTheCanonicalPersistedForm()
    {
        NodeOperatorIdentity.NormalizeUsername("  Node.Operator_1  ")
            .Should().Be("node.operator_1");
    }

    [Theory]
    [InlineData("node-operator")]
    [InlineData("ops.1")]
    [InlineData("a_b")]
    public void IsValidUsername_AcceptsCanonicalOperatorNames(string username)
    {
        NodeOperatorIdentity.IsValidUsername(username).Should().BeTrue();
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("-operator")]
    [InlineData("operator-")]
    [InlineData("operator name")]
    [InlineData("operator@example.com")]
    public void IsValidUsername_RejectsAmbiguousOrUnsafeNames(string username)
    {
        NodeOperatorIdentity.IsValidUsername(username).Should().BeFalse();
    }

    [Fact]
    public void Validate_AcceptsCompleteBoundedSeedConfiguration()
    {
        NodeOperatorIdentity.Validate(ValidOptions()).Should().BeNull();
    }

    [Theory]
    [InlineData("short")]
    [InlineData("change-me-node-operator-password")]
    [InlineData("replace-this-node-operator-password")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("password with spaces is rejected")]
    public void Validate_RejectsWeakOrPlaceholderPasswords(string password)
    {
        var options = ValidOptions();
        options.Password = password;

        NodeOperatorIdentity.Validate(options).Should().Contain("Password");
    }

    [Fact]
    public void Validate_UsesTheBcryptUtf8ByteLimit()
    {
        var options = ValidOptions();
        options.Password = string.Concat(Enumerable.Repeat("é", 37));

        NodeOperatorIdentity.Validate(options).Should().Contain("24-72 byte");
    }

    [Theory]
    [InlineData(0, 20, "positive monotonic")]
    [InlineData(1, 4, "between 5 and 30")]
    [InlineData(1, 31, "between 5 and 30")]
    public void Validate_RejectsUnsafeRevisionOrSessionBounds(
        long credentialRevision,
        int sessionMinutes,
        string expectedMessage)
    {
        var options = ValidOptions();
        options.CredentialRevision = credentialRevision;
        options.SessionMinutes = sessionMinutes;

        NodeOperatorIdentity.Validate(options).Should().Contain(expectedMessage);
    }

    private static NodeOperatorOptions ValidOptions() => new()
    {
        Username = "node-operator",
        Password = ValidPassword,
        CredentialRevision = 1,
        SessionMinutes = 20,
    };
}
