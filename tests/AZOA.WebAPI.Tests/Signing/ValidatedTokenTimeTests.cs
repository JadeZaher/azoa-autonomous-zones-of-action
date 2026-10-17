using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Services.Auth;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace AZOA.WebAPI.Tests.Signing;

public sealed class ValidatedTokenTimeTests
{
    [Fact]
    public void NewlyIssuedLoginAfterRevocationHasUsableTimestampEvidence()
    {
        var watermark = DateTime.UtcNow.AddSeconds(-2);
        var configuration = new ConfigurationBuilder().AddJsonFile("appsettings.json").AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "source-fixture-only-signing-key-with-more-than-thirty-two-bytes",
            ["Jwt:Issuer"] = "fixture", ["Jwt:Audience"] = "fixture",
        }).Build();
        var user = Mock.Of<IUser>(row => row.Id == Guid.NewGuid() && row.Email == "fixture@example.invalid" && row.Username == "fixture");
        var token = new JwtSecurityTokenHandler().ReadJwtToken(UserLoginTokenIssuer.BuildLoginToken(configuration, user));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(token.Claims, "validated-fixture"));
        ValidatedTokenTime.TryRead(principal, out var issued, out var expires).Should().BeTrue();
        token.Claims.Should().Contain(claim => claim.Type == "iat");
        AuthWatermark.IsTokenStale(issued.UtcDateTime, watermark).Should().BeFalse();
        expires.Should().BeAfter(issued);
        AuthWatermark.IsTokenStale(issued.UtcDateTime, issued.UtcDateTime.AddSeconds(2)).Should().BeTrue();
    }

    [Fact]
    public void ChildNotBeforePreservesRevocationBoundaryWithoutSyntheticAuthenticationTime()
    {
        var instant = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        var principal = Principal(AzoaClaims.TokenUseChild, "nbf", instant.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        ValidatedTokenTime.TryRead(principal, out var issued, out _).Should().BeTrue();
        issued.Should().Be(instant);
        AuthWatermark.IsTokenStale(issued.UtcDateTime, instant.UtcDateTime.AddSeconds(-2)).Should().BeFalse();
        AuthWatermark.IsTokenStale(issued.UtcDateTime, instant.UtcDateTime.AddSeconds(2)).Should().BeTrue();
        principal.FindFirst(AzoaClaims.AuthTime).Should().BeNull();
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("253402300800")]
    [InlineData("not-a-time")]
    public void MalformedTimestampCannotBeReplacedByCurrentTime(string value)
        => ValidatedTokenTime.TryRead(Principal(AzoaClaims.TokenUseLogin, AzoaClaims.AuthTime, value), out _, out _).Should().BeFalse();

    [Fact]
    public void MissingOrAmbiguousCredentialClassTimestampIsRefused()
    {
        var childWithOnlyLoginTime = Principal(AzoaClaims.TokenUseChild, AzoaClaims.AuthTime, "1800000000");
        ValidatedTokenTime.TryRead(childWithOnlyLoginTime, out _, out _).Should().BeFalse();
        var duplicate = Principal(AzoaClaims.TokenUseLogin, AzoaClaims.AuthTime, "1800000000");
        ((ClaimsIdentity)duplicate.Identity!).AddClaim(new(AzoaClaims.AuthTime, "1800000001"));
        ValidatedTokenTime.TryRead(duplicate, out _, out _).Should().BeFalse();
    }

    private static ClaimsPrincipal Principal(string kind, string name, string value)
        => new(new ClaimsIdentity([new(AzoaClaims.TokenUse, kind), new(name, value), new("exp", "1800003600")], "validated-fixture"));
}
