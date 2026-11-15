using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.IntegrationTests.Http.Infrastructure;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Services.Auth;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using SurrealForge.Client.Query;
using UserRow = AZOA.WebAPI.Persistence.SurrealDb.Models.User;
using GrantRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentGrant;

namespace AZOA.WebAPI.IntegrationTests.Http;

/// <summary>Generic consent writes enforce the actual issuer's exact login cut.</summary>
public sealed class ConsentLoginCutFlowTests(MemberHandoffWebApplicationFactory factory)
    : HttpFlowTestBase(factory), IClassFixture<MemberHandoffWebApplicationFactory>
{
    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("old")]
    [InlineData("same-second")]
    [InlineData("duplicate")]
    public async Task Generic_grant_rejects_stale_or_missing_exact_cut_even_when_JWT_time_is_current(string proof)
    {
        using var tenant = await OnboardTenantAsync();
        var member = await SeedUserAsync();
        var cut = DateTime.SpecifyKind(DateTime.UtcNow.AddSeconds(-2), DateTimeKind.Utc);
        cut = new DateTime(cut.Ticks - cut.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc).AddMilliseconds(500);
        await WriteAsync(SurrealWriter.UpdateOnly<UserRow>(member.Id.ToString("N")).Where(row => row.IsActive)
            .Set(row => row.AuthNotBefore, new DateTimeOffset(cut)).Build());
        member.AuthNotBefore = proof switch { "missing" => null, "old" => cut.AddSeconds(-1), "same-second" => cut.AddTicks(-1), _ => cut };
        var token = UserLoginTokenIssuer.BuildLoginToken(Flow.Services.GetRequiredService<IConfiguration>(), member);
        if (proof is "malformed" or "duplicate") token = AlterCut(token, proof);
        using var person = Flow.CreateClient();
        person.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var answer = await person.PostAsJsonAsync("api/user/consent", Command(tenant.TenantId), JsonOptions);
        await ShouldReturnAsync(answer, HttpStatusCode.BadRequest);
        var actor = SurrealForge.Client.SurrealLink.ToLink("user", member.Id.ToString("N"));
        (await RowsAsync(SurrealQuery<GrantRow>.From().Where(row => row.GrantorUserId == actor))).Should().BeEmpty();

        member.AuthNotBefore = cut;
        person.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            UserLoginTokenIssuer.BuildLoginToken(Flow.Services.GetRequiredService<IConfiguration>(), member));
        using var accepted = await person.PostAsJsonAsync("api/user/consent", Command(tenant.TenantId), JsonOptions);
        await ShouldReturnAsync(accepted, HttpStatusCode.OK);
    }

    [Fact]
    public async Task Legacy_unwatermarked_login_can_still_grant_permission()
    {
        using var tenant = await OnboardTenantAsync();
        var member = await SeedUserAsync();
        member.AuthNotBefore.Should().BeNull();
        using var person = Flow.CreateClient();
        person.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            UserLoginTokenIssuer.BuildLoginToken(Flow.Services.GetRequiredService<IConfiguration>(), member));
        using var accepted = await person.PostAsJsonAsync("api/user/consent", Command(tenant.TenantId), JsonOptions);
        await ShouldReturnAsync(accepted, HttpStatusCode.OK);
    }

    private static ConsentLifecycleCommand Command(Guid tenant)
        => new(Guid.NewGuid(), tenant, [AzoaScopes.KycRead], GrantOrigin.UserExplicit, null, DateTimeOffset.UtcNow.AddDays(1));

    private string AlterCut(string original, string proof)
    {
        var configuration = Flow.Services.GetRequiredService<IConfiguration>();
        var handler = new JwtSecurityTokenHandler();
        var source = handler.ReadJwtToken(original);
        var claims = source.Claims.Where(claim => claim.Type != AzoaClaims.AuthCut).ToList();
        if (proof == "duplicate") claims.AddRange(source.Claims.Where(claim => claim.Type == AzoaClaims.AuthCut));
        claims.Add(new Claim(AzoaClaims.AuthCut, "invalid-cut"));
        return handler.WriteToken(new JwtSecurityToken(source.Issuer, configuration["Jwt:Audience"], claims,
            expires: source.ValidTo, signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuration["Jwt:Key"]!)), SecurityAlgorithms.HmacSha256)));
    }
}
