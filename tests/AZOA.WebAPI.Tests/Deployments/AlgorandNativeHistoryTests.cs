using System.Text.Json;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Deployments;

public sealed class AlgorandNativeHistoryTests
{
    [Fact]
    public void UpdateBetweenPreparationAndExecutionCannotClaimOriginalCode()
    {
        AlgorandProvider.ProgramsAtPosition(Input(), 77, "call", 10, 4,
            [Creation(), Update(10, 3, "changed"), Call()]).Should().BeFalse();
    }

    [Fact]
    public void LaterSameRoundUpdateDoesNotInvalidateCodeActuallyExecuted()
    {
        AlgorandProvider.ProgramsAtPosition(Input(), 77, "call", 10, 4,
            [Update(10, 5, "changed"), Call(), Creation()]).Should().BeTrue();
    }

    [Fact]
    public void MissingCreationOrTargetCannotProveCompleteCodeHistory()
    {
        AlgorandProvider.ProgramsAtPosition(Input(), 77, "call", 10, 4, [Call()]).Should().BeFalse();
        AlgorandProvider.ProgramsAtPosition(Input(), 77, "call", 10, 4, [Creation()]).Should().BeFalse();
    }

    [Fact]
    public void CompleteMutableHistoryUsesLatestProgramBeforeCall()
    {
        AlgorandProvider.ProgramsAtPosition(Input(), 77, "call", 10, 4,
            [Creation(), Update(8, 1, "changed"), Update(10, 2, "approval"), Call()]).Should().BeTrue();
    }

    private static AvmNativeInput Input() => new(new("avm-application/1", JsonSerializer.SerializeToElement(
        new AvmApplicationArtifact(new("Bytecode", "approval"), new("Bytecode", "clear"), new(0, 0), new(0, 0), 0, [], [], [], [], [])), "fixture"), "sender", "Call", "77");
    private static JsonElement Creation() => JsonSerializer.SerializeToElement(new Dictionary<string, object>
    {
        ["id"] = "create", ["confirmed-round"] = 1, ["intra-round-offset"] = 0, ["created-application-index"] = 77,
        ["application-transaction"] = new Dictionary<string, object> { ["application-id"] = 0, ["approval-program"] = "approval", ["clear-state-program"] = "clear" },
    });
    private static JsonElement Update(ulong round, ulong offset, string program) => JsonSerializer.SerializeToElement(new Dictionary<string, object>
    {
        ["id"] = $"update-{round}-{offset}", ["confirmed-round"] = round, ["intra-round-offset"] = offset,
        ["application-transaction"] = new Dictionary<string, object> { ["application-id"] = 77, ["on-completion"] = "update", ["approval-program"] = program, ["clear-state-program"] = "clear" },
    });
    private static JsonElement Call() => JsonSerializer.SerializeToElement(new Dictionary<string, object>
    {
        ["id"] = "call", ["confirmed-round"] = 10, ["intra-round-offset"] = 4,
        ["application-transaction"] = new Dictionary<string, object> { ["application-id"] = 77, ["on-completion"] = "noop" },
    });
}
