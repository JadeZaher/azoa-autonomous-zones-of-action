using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Interfaces.Managers;

namespace AZOA.WebAPI.Tests.Controllers;

public sealed class FirstPartyCredentialBoundaryTests
{
    [Fact]
    public void ConsentSurface_RequiresFirstPartyLogin()
        => PoliciesOn(typeof(ConsentController))
            .Should().Contain("FirstPartyLogin");

    [Theory]
    [InlineData(nameof(AvatarController.Update))]
    [InlineData(nameof(AvatarController.Delete))]
    [InlineData(nameof(AvatarController.LogoutEverywhere))]
    public void AvatarOwnerMutations_RequireFirstPartyLogin(string action)
        => PoliciesOn(typeof(AvatarController).GetMethod(action)!)
            .Should().Contain("FirstPartyLogin");

    [Fact]
    public void WalletExport_RequiresRecentFirstPartyLogin()
        => PoliciesOn(typeof(WalletController).GetMethod(nameof(WalletController.Export))!)
            .Should().Contain("RecentFirstPartyLogin");

    [Theory]
    [InlineData(nameof(WalletController.Create))]
    [InlineData(nameof(WalletController.Update))]
    [InlineData(nameof(WalletController.Delete))]
    [InlineData(nameof(WalletController.SetDefault))]
    [InlineData(nameof(WalletController.Generate))]
    [InlineData(nameof(WalletController.Connect))]
    public void WalletOwnerMutations_RequireFirstPartyLogin(string action)
        => PoliciesOn(typeof(WalletController).GetMethod(action)!)
            .Should().Contain("FirstPartyLogin");

    [Fact]
    public void ChildCredentialEndpoint_IsExplicitlyUnavailable()
    {
        var controller = new TenantController(
            Mock.Of<ITenantManager>(),
            Mock.Of<ITenantCustodialAccountManager>());

        var result = controller.IssueChildCredential(Guid.NewGuid(), null);

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    private static IEnumerable<string?> PoliciesOn(System.Reflection.MemberInfo member)
        => member.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Select(attribute => attribute.Policy);
}
