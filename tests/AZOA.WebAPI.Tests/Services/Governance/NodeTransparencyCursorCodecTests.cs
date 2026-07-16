using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Services.Governance;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;

namespace AZOA.WebAPI.Tests.Services.Governance;

public sealed class NodeTransparencyCursorCodecTests
{
    [Fact]
    public void Cursor_RoundTripsAcrossProvidersSharingPersistentVersionedKeyRing()
    {
        var directory = new DirectoryInfo(Path.Combine(
            Path.GetTempPath(),
            $"azoa-transparency-keys-{Guid.NewGuid():N}"));
        directory.Create();
        try
        {
            var firstProvider = DataProtectionProvider.Create(
                directory,
                builder => builder.SetApplicationName("AZOA.WebAPI.NodeTransparency.v1"));
            var firstCodec = new NodeTransparencyCursorCodec(firstProvider);
            var expected = new NodeTransparencyStoreCursor(
                DateTimeOffset.Parse("2026-07-11T12:00:00Z"),
                "node_fee_audit:private-record-id");
            var encoded = firstCodec.Encode(NodeTransparencyAuditKind.FeeSchedule, expected);

            var restartedProvider = DataProtectionProvider.Create(
                directory,
                builder => builder.SetApplicationName("AZOA.WebAPI.NodeTransparency.v1"));
            var restartedCodec = new NodeTransparencyCursorCodec(restartedProvider);

            restartedCodec.TryDecode(
                    encoded,
                    NodeTransparencyAuditKind.FeeSchedule,
                    out var decoded)
                .Should().BeTrue();
            decoded.Should().Be(expected);
            encoded.Should().NotContain("private-record-id");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Cursor_IsBoundToKindAndRejectsTampering()
    {
        var codec = new NodeTransparencyCursorCodec(new EphemeralDataProtectionProvider());
        var encoded = codec.Encode(
            NodeTransparencyAuditKind.Governance,
            new NodeTransparencyStoreCursor(DateTimeOffset.UtcNow, "node_governance_audit:private"));

        codec.TryDecode(encoded, NodeTransparencyAuditKind.Treasury, out _)
            .Should().BeFalse();
        var tamperedCharacters = encoded.ToCharArray();
        tamperedCharacters[^1] = tamperedCharacters[^1] == 'a' ? 'b' : 'a';
        var tampered = new string(tamperedCharacters);
        codec.TryDecode(tampered, NodeTransparencyAuditKind.Governance, out _)
            .Should().BeFalse();
    }
}
