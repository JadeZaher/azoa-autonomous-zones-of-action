using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Kyb;
using FluentAssertions;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Tests.Kyc;

public sealed class HostedSessionRecoveryContractTests
{
    [Fact]
    public void RecognizedPendingKybResultProvesExistenceWithoutApproval()
    {
        using var json = JsonDocument.Parse("""{"result":{"stepsResults":[]}}""");
        var result = IdentomatKybResultParser.Parse(json.RootElement);
        result.SessionExists.Should().BeTrue();
        result.IsTerminal.Should().BeFalse();
        result.IsApproved.Should().BeFalse();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"argumentError\":\"unknown session\",\"result\":{\"stepsResults\":[]}}")]
    public void UnrecognizedProviderPayloadDoesNotProveSessionExistence(string body)
    {
        using var json = JsonDocument.Parse(body);
        IdentomatKybResultParser.Parse(json.RootElement).SessionExists.Should().BeFalse();
    }

    [Fact]
    public void ParentAndChildReservationsKeepDistinctParameterBindings()
    {
        var statements = HostedSessionAliasFence.Reserve(new HostedSession
        {
            Id = Guid.NewGuid().ToString("N"), ProviderKey = "identomat", FlowKind = "kyc",
        }, "parent-one", "child-two");
        var transaction = SurrealQuery.Combine(statements.ToArray());
        transaction.Params["_alias0"].Should().Be("parent-one");
        transaction.Params["_alias1"].Should().Be("child-two");
        transaction.Params["_key0"].Should().NotBe(transaction.Params["_key1"]);
    }
}
