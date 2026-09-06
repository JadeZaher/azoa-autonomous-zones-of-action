using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Actions;
using Xunit;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;
using ActionTemplateEntity = AZOA.WebAPI.Models.Actions.ActionTemplate;
using ActionTemplateNodeEntity = AZOA.WebAPI.Models.Actions.ActionTemplateNode;
using ActionTemplateEdgeEntity = AZOA.WebAPI.Models.Actions.ActionTemplateEdge;

namespace AZOA.WebAPI.Tests.Actions;

/// <summary>
/// Regression: user-supplied parametersJson with duplicate object keys (e.g.
/// {"x":1,"x":2}) used to throw ArgumentException out of ToDictionary -> 500.
/// It must now be rejected as a clean validation failure.
/// </summary>
public class ActionInstantiatorDuplicateKeyTests
{
    private readonly ActionInstantiator _instantiator;
    private readonly Mock<IActionTemplateStore> _templateStoreMock = new();
    private readonly Mock<IActionDagValidator> _validatorMock = new();

    public ActionInstantiatorDuplicateKeyTests()
    {
        _validatorMock.Setup(v => v.Validate(It.IsAny<ActionEntity>(), It.IsAny<bool>()))
            .Returns(new DagValidationResult { IsValid = true });
        _instantiator = new ActionInstantiator(_templateStoreMock.Object, _validatorMock.Object);
    }

    [Fact]
    public async Task InstantiateAsync_DuplicateParamKeys_ThrowsCleanValidationError()
    {
        var templateId = Guid.NewGuid();
        _templateStoreMock
            .Setup(s => s.GetTemplateAsync(templateId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionTemplateEntity
            {
                Id = templateId,
                Name = "Dup Param ActionDefinition",
                AuthorUserId = Guid.NewGuid(),
                Nodes = new List<ActionTemplateNodeEntity>(),
                Edges = new List<ActionTemplateEdgeEntity>(),
            });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _instantiator.InstantiateAsync(templateId, "{\"x\":1,\"x\":2}", Guid.NewGuid()));

        Assert.Contains("Duplicate parameter key", ex.Message);
        Assert.Contains("x", ex.Message);
    }
}
