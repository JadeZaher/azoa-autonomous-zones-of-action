using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using AZOA.WebAPI.Controllers;

namespace AZOA.WebAPI.Tests.Controllers;

public class DappCompositionAuthorizationTests
{
    [Theory]
    [InlineData(nameof(DappSeriesController.Create), "DappDevelop")]
    [InlineData(nameof(DappSeriesController.Update), "DappDevelop")]
    [InlineData(nameof(DappSeriesController.Delete), "DappDevelop")]
    [InlineData(nameof(DappSeriesController.AddQuest), "DappDevelop")]
    [InlineData(nameof(DappSeriesController.RemoveQuest), "DappDevelop")]
    [InlineData(nameof(DappSeriesController.ReorderQuest), "DappDevelop")]
    [InlineData(nameof(DappSeriesController.UpdateMappings), "DappDevelop")]
    [InlineData(nameof(DappCompositionController.Compose), "DappManage")]
    [InlineData(nameof(DappCompositionController.Generate), "DappManage")]
    [InlineData(nameof(DappCompositionController.Deploy), "DappManage")]
    public void WriteEndpoints_ShouldRequireExpectedDappPolicy(string methodName, string policy)
    {
        var controllerType = methodName switch
        {
            nameof(DappCompositionController.Compose) or
            nameof(DappCompositionController.Generate) or
            nameof(DappCompositionController.Deploy) => typeof(DappCompositionController),
            _ => typeof(DappSeriesController),
        };

        var method = controllerType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
        method.Should().NotBeNull();

        var authorize = method!.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault();
        authorize.Should().NotBeNull();
        authorize!.Policy.Should().Be(policy);
    }

    [Theory]
    [InlineData(nameof(DappSeriesController.List))]
    [InlineData(nameof(DappSeriesController.Get))]
    [InlineData(nameof(DappSeriesController.ListQuests))]
    [InlineData(nameof(DappCompositionController.Validate))]
    [InlineData(nameof(DappCompositionController.GetManifest))]
    [InlineData(nameof(DappCompositionController.GetStatus))]
    public void ReadEndpoints_ShouldStayPolicyFree(string methodName)
    {
        var controllerType = methodName switch
        {
            nameof(DappCompositionController.Validate) or
            nameof(DappCompositionController.GetManifest) or
            nameof(DappCompositionController.GetStatus) => typeof(DappCompositionController),
            _ => typeof(DappSeriesController),
        };

        var method = controllerType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
        method.Should().NotBeNull();

        method!.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Should().NotContain(a => a.Policy == "DappDevelop");
    }
}
