using System.IdentityModel.Tokens.Jwt;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Services.Auth;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace AZOA.WebAPI.Tests.Auth;

public sealed class MemberHandoffAuthCutTests
{
    [Fact]
    public void A_login_carries_the_exact_claim_watermark_even_within_the_same_JWT_second()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = new string('x', 64), ["Jwt:Issuer"] = "test", ["Jwt:Audience"] = "test",
        }).Build();
        var cut = DateTime.SpecifyKind(new DateTime(2026, 11, 1, 12, 0, 0).AddTicks(1234567), DateTimeKind.Utc);
        var user = new User { Id = Guid.NewGuid(), Username = "member", Email = "member@example.test" };
        var before = new JwtSecurityTokenHandler().ReadJwtToken(UserLoginTokenIssuer.BuildLoginToken(config, user));
        user.AuthNotBefore = cut;
        var after = new JwtSecurityTokenHandler().ReadJwtToken(UserLoginTokenIssuer.BuildLoginToken(config, user));
        before.Claims.Should().NotContain(claim => claim.Type == AzoaClaims.AuthCut);
        after.Claims.Single(claim => claim.Type == AzoaClaims.AuthCut).Value.Should().Be(cut.ToString("O"));
    }
}
