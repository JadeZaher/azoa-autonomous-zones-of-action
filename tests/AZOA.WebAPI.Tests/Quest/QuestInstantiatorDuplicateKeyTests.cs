using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Services.Quest;
using Xunit;
using QuestEntity = AZOA.WebAPI.Models.Quest.Quest;
using QuestTemplateEntity = AZOA.WebAPI.Models.Quest.QuestTemplate;
using QuestTemplateNodeEntity = AZOA.WebAPI.Models.Quest.QuestTemplateNode;
using QuestTemplateEdgeEntity = AZOA.WebAPI.Models.Quest.QuestTemplateEdge;

namespace AZOA.WebAPI.Tests.Quest;

/// <summary>
/// Regression: user-supplied parametersJson with duplicate object keys (e.g.
/// {"x":1,"x":2}) used to throw ArgumentException out of ToDictionary -> 500.
/// It must now be rejected as a clean validation failure.
/// </summary>
public class QuestInstantiatorDuplicateKeyTests
{
    private readonly QuestInstantiator _instantiator;
    private readonly Mock<IQuestTemplateStore> _templateStoreMock = new();
    private readonly Mock<IQuestDagValidator> _validatorMock = new();

    public QuestInstantiatorDuplicateKeyTests()
    {
        _validatorMock.Setup(v => v.Validate(It.IsAny<QuestEntity>(), It.IsAny<bool>()))
            .Returns(new DagValidationResult { IsValid = true });
        _instantiator = new QuestInstantiator(_templateStoreMock.Object, _validatorMock.Object);
    }

    [Fact]
    public async Task InstantiateAsync_DuplicateParamKeys_ThrowsCleanValidationError()
    {
        var templateId = Guid.NewGuid();
        _templateStoreMock
            .Setup(s => s.GetTemplateAsync(templateId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuestTemplateEntity
            {
                Id = templateId,
                Name = "Dup Param Quest",
                AuthorAvatarId = Guid.NewGuid(),
                Nodes = new List<QuestTemplateNodeEntity>(),
                Edges = new List<QuestTemplateEdgeEntity>(),
            });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _instantiator.InstantiateAsync(templateId, "{\"x\":1,\"x\":2}", Guid.NewGuid()));

        Assert.Contains("Duplicate parameter key", ex.Message);
        Assert.Contains("x", ex.Message);
    }
}
