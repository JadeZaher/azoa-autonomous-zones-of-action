using System.Reflection;
using AZOA.WebAPI.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Tests.Controllers;

public sealed class ActionRecoverySecurityContractTests
{
    [Fact]
    public void Every_recovery_endpoint_requires_recent_operator_session_and_financial_rate_limit()
    {
        var controller = typeof(ActionRecoveryOperatorController);
        controller.GetCustomAttribute<AuthorizeAttribute>()!.Policy.Should().Be("RecentNodeOperatorSession");
        controller.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName.Should().Be("financial");
        controller.GetCustomAttributes<AllowAnonymousAttribute>().Should().BeEmpty();
        foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            method.GetCustomAttributes<AllowAnonymousAttribute>().Should().BeEmpty();
            method.GetCustomAttributes<DisableRateLimitingAttribute>().Should().BeEmpty();
        }
    }
}
