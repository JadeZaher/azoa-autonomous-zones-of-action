using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Models.Deployments;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Deployments;

public sealed class NativeResourceEffectsTests
{
    [Fact]
    public void FailedHardeningPreservesConfirmedDeployment()
    {
        var history = new[] { Stage("CreateBuffer", "Confirmed", "buffer"), Stage("Deploy", "Confirmed", "program"),
            Stage("RemoveAuthority", "FailedOnChain", null) };
        var effect = NativeResourceEffects.FromHistory(history);
        effect!.Kind.Should().Be("Deployed");
        effect.ResourceId.Should().Be("program");
        effect.TransactionId.Should().Be("tx-Deploy");
    }

    [Fact]
    public void FailedWriteRetainsFundedBufferWithoutClaimingProgramDeployment()
    {
        var history = new[] { Stage("CreateBuffer", "Confirmed", "buffer"), Stage("Write:0", "FailedOnChain", null) };
        var effect = NativeResourceEffects.FromHistory(history);
        effect!.Kind.Should().Be("BufferFunded");
        effect.ResourceId.Should().Be("buffer");
    }

    [Fact]
    public void UnknownBufferCreationDoesNotEstablishAnEffect()
        => NativeResourceEffects.FromHistory([Stage("CreateBuffer", "Unknown", "buffer")]).Should().BeNull();

    [Fact]
    public void CleanupReceiptCannotEraseDeploymentEvidence()
    {
        var cleanup = Stage("CloseBuffer", "Confirmed", "buffer");
        cleanup = cleanup with { Observation = cleanup.Observation with
        {
            ResourceEffect = null, CleanupEffect = new("BufferClosed", "buffer", "tx-CloseBuffer", "{}"),
        } };
        var history = new[] { Stage("Deploy", "Confirmed", "program"), cleanup };
        NativeResourceEffects.FromHistory(history)!.Kind.Should().Be("Deployed");
        NativeResourceEffects.CleanupFromHistory(history)!.Kind.Should().Be("BufferClosed");
    }

    private static NativeStageHistory Stage(string stage, string verdict, string? resource)
        => new(new(stage, "tx-" + stage, "wire", "solana:genesis", "{}", "1", "0"),
            new(verdict, "tx-" + stage, "solana:genesis", resource, "{}", ResourceEffect: new(stage switch { "CreateBuffer" => "BufferFunded", "Deploy" => "Deployed", _ => stage }, resource, "tx-" + stage, "{}")));
}

