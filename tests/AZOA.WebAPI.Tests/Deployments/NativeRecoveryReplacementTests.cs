using System.Text.Json;
using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Tests.Deployments;

public sealed class NativeRecoveryReplacementTests
{
    [Theory]
    [InlineData("Admitted")]
    [InlineData("AwaitingAuthorization")]
    [InlineData("Preparing")]
    [InlineData("Prepared")]
    public void UnsentRetirementRequiresInitialGenerationAndNoHistoricalSubmission(string state)
    {
        var row = new DeploymentOperation { ParentOperationId = "parent", State = state, PreparedJson = "retained-unsent-candidate" };
        Assert.Equal("NeverAuthorizedRetirement", NativeRecoveryReplacement.ProofKind(row));
        row.Generation = 1;
        Assert.Null(NativeRecoveryReplacement.ProofKind(row));
        row.Generation = 0; row.HistoryJson = History("ExpiredNoEffect");
        Assert.Null(NativeRecoveryReplacement.ProofKind(row));
        row.HistoryJson = "[]"; row.SubmissionAuthorized = true;
        Assert.Null(NativeRecoveryReplacement.ProofKind(row));
        row.SubmissionAuthorized = false; row.ReplacedByOperationId = "replacement";
        Assert.Null(NativeRecoveryReplacement.ProofKind(row));
    }

    [Theory]
    [InlineData("FailedOnChain", "ResolvedFailure")]
    [InlineData("ExpiredNoEffect", "ResolvedFailure")]
    [InlineData("Confirmed", null)]
    [InlineData("Pending", null)]
    [InlineData("Unknown", null)]
    public void PreviouslySubmittedRecoveryNeedsResolvedNoEffectHistory(string verdict, string? expected)
    {
        var row = new DeploymentOperation { ParentOperationId = "parent", State = "Failed", Generation = 1, HistoryJson = History(verdict) };
        Assert.Equal(expected, NativeRecoveryReplacement.ProofKind(row));
        row.PreparedJson = "unresolved-wire";
        Assert.Null(NativeRecoveryReplacement.ProofKind(row));
    }

    private static string History(string verdict) => JsonSerializer.Serialize(new[] { new NativeStageHistory(
        new("CloseBuffer", "transaction", "wire", "genesis", "{}", "1", "0"), new(verdict, "transaction", "genesis")) });
}
