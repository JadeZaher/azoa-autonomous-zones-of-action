using System.Buffers.Binary;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttFinalizedSnapshotTests
{
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task Rotated_guardian_deadline_is_exclusive_even_when_finalized_head_lags(int remaining, bool rejected)
    {
        const uint seconds = 1_800_000_000;
        var now = DateTimeOffset.FromUnixTimeSeconds(seconds);
        var payload = WttPayloadCodec.EncodeAttestation(new(new string('a', 64), 2, 8, new string('0', 64), new string('0', 64)));
        var wire = new byte[123 + payload.Length];
        wire[0] = 1; wire[5] = 1; wire[7] = 1; wire[39] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(wire.AsSpan(80), 2);
        Convert.FromHexString(new string('b', 64)).CopyTo(wire, 82);
        payload.CopyTo(wire, 123);
        var vaa = WttVaaCodec.Parse(wire);
        var evidence = new WttGuardianEvidence("algorand:local", "core", "token", 0, 1,
            [new string('1', 40)], (uint)(seconds + remaining), seconds - 5, "finalized", now, 2, new string('b', 64));
        var state = new Mock<IWttGuardianStateSource>();
        state.Setup(source => source.ReadWttGuardianEvidenceAsync(0, 2, new string('b', 64), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<WttGuardianEvidence>.Success(evidence));
        var signatures = new Mock<IWttGuardianSignatureVerifier>();
        signatures.Setup(verifier => verifier.VerifyGuardianSignature(It.IsAny<byte[]>(), It.IsAny<WormholeVaaSignature>(), It.IsAny<byte[]>()))
            .Returns(true);
        var verifier = new WttProofVerifier(signatures.Object, new Clock(now),
            Options.Create(new WttFinalityOptions { Networks = [new("algorand:local", 90)] }));
        var result = await verifier.VerifyAsync(Convert.ToBase64String(wire), new("algorand:local", "core", "token", vaa.BodyHex), state.Object);
        Assert.Equal(rejected, result.IsError);
        signatures.Verify(check => check.VerifyGuardianSignature(It.IsAny<byte[]>(), It.IsAny<WormholeVaaSignature>(), It.IsAny<byte[]>()),
            rejected ? Times.Never() : Times.Once());
    }

    [Fact]
    public void AlreadyPostedProofStillRequiresFreshActualChainTimeAndExactNetworkPolicy()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var options = new WttFinalityOptions { Networks = [new("solana:local", 90), new("eip155:31337", 1200)] };
        var verifier = new WttProofVerifier(Mock.Of<IWttGuardianSignatureVerifier>(), new Clock(now), Options.Create(options));
        Assert.True(verifier.HasFreshFinalizedSnapshot("solana:local", now.AddSeconds(-60).ToUnixTimeSeconds(), now));
        Assert.False(verifier.HasFreshFinalizedSnapshot("solana:local", now.AddSeconds(-91).ToUnixTimeSeconds(), now));
        Assert.False(verifier.HasFreshFinalizedSnapshot("solana:local", now.AddSeconds(6).ToUnixTimeSeconds(), now));
        Assert.False(verifier.HasFreshFinalizedSnapshot("solana:local", now.ToUnixTimeSeconds(), now.AddMinutes(-3)));
        Assert.False(verifier.HasFreshFinalizedSnapshot("solana:other", now.ToUnixTimeSeconds(), now));
        Assert.True(verifier.HasFreshFinalizedSnapshot("eip155:31337", now.AddMinutes(-15).ToUnixTimeSeconds(), now));
        options.Networks.Add(new("solana:local", 90));
        Assert.False(verifier.HasFreshFinalizedSnapshot("solana:local", now.ToUnixTimeSeconds(), now));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
