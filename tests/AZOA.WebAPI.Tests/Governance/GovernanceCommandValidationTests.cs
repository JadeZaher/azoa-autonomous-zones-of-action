using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Actions.Handlers;
using AZOA.WebAPI.Services.Governance;
using FluentAssertions;
using Moq;
using SurrealForge.Client.Query;
using Xunit;
using ActionNode = AZOA.WebAPI.Models.Actions.ActionNode;
using ActionDefinition = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Tests.Governance;

public sealed class GovernanceCommandValidationTests
{
    [Theory]
    [InlineData("null-options")]
    [InlineData("duplicate-options")]
    [InlineData("empty-option")]
    [InlineData("missing-close")]
    [InlineData("closed")]
    [InlineData("negative-quorum")]
    [InlineData("invalid-scope")]
    [InlineData("null-description")]
    public async Task Invalid_creation_returns_typed_failure_without_writing(string scenario)
    {
        var actor = Guid.NewGuid();
        var config = new GovernanceProposalCreateNodeConfig { Name = "Question", Options = ["yes", "no"], ClosesAt = DateTimeOffset.UtcNow.AddHours(1) };
        switch (scenario)
        {
            case "null-options": config.Options = null!; break;
            case "duplicate-options": config.Options = ["yes", "yes"]; break;
            case "empty-option": config.Options = ["yes", " "]; break;
            case "missing-close": config.ClosesAt = null; break;
            case "closed": config.ClosesAt = DateTimeOffset.UtcNow.AddMinutes(-1); break;
            case "negative-quorum": config.MinimumVotes = -1; break;
            case "invalid-scope": config.UniquenessScope = "1"; break;
            case "null-description": config.Description = null!; break;
        }
        var executor = new Mock<ISurrealExecutor>(MockBehavior.Strict);
        executor.Setup(value => value.QueryAsync<GovernanceCommandReceipt>(It.IsAny<SurrealQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GovernanceCommandReceipt>());
        var users = new Mock<IUserStore>();
        users.Setup(value => value.GetByIdAsync(actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IUser>.Success(new AZOA.WebAPI.Models.User { Id = actor, IsActive = true }));
        var keys = new Mock<IApiKeyStore>();
        keys.Setup(value => value.ListByUserAsync(actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new AZOA.WebAPI.Models.ApiKey { UserId = actor, IsActive = true, Scopes = AzoaScopes.TenantProvision } });
        var service = new ConsumerGovernanceService(new(executor.Object), users.Object, keys.Object,
            Mock.Of<IValueAccessService>(), Mock.Of<IKycStore>(), Mock.Of<IKycIdentityStore>(), Mock.Of<IHolonManager>(), NodeGovernanceGuard.Unrestricted);
        var context = Context(actor, actor, config);
        var result = await new GovernanceProposalCreateNodeHandler(service).HandleAsync(context);
        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Child_cannot_administer_tenant_election_even_with_acting_tenant_context()
    {
        var service = new ConsumerGovernanceService(null!, null!, null!, null!, null!, null!, null!, null!);
        var result = await service.CreateAsync(Context(Guid.NewGuid(), Guid.NewGuid(), new()), new(), default);
        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("tenant principal");
    }

    [Fact]
    public void Tally_dependency_guard_requires_dedicated_ledger_store()
        => GovernanceVoteOfRecordGuard.Violations(typeof(GovernanceTallyNodeHandler)).Should().BeEmpty();

    private static ActionNodeExecutionContext Context(Guid actor, Guid tenant, GovernanceProposalCreateNodeConfig config)
    {
        var node = new ActionNode { Id = Guid.NewGuid(), NodeType = ActionNodeType.GovernanceProposalCreate, Config = JsonSerializer.Serialize(config) };
        return new(Guid.NewGuid(), node.Id, new ActionDefinition { Nodes = [node] }, actor, actingTenantId: tenant);
    }
}
