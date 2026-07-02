using FluentAssertions;
using AZOA.WebAPI.Services.Quest.Predicates;
using Xunit;

namespace AZOA.WebAPI.Tests.Quest.Predicates;

/// <summary>
/// Unit tests for <see cref="GatePath.TryParse"/>. Pins the closed grammar
/// so both GateCheck evaluation and $from binding share one tested authority.
/// </summary>
public class GatePathTests
{
    [Theory]
    [InlineData("upstream.nodeName.field")]
    [InlineData("upstream.gate.amount")]
    [InlineData("upstream.myNode.nested.deep")]
    [InlineData("holon.3f2504e0-4f89-11d3-9a0c-0305e82c3301.status")]
    [InlineData("holon.3f2504e0-4f89-11d3-9a0c-0305e82c3301.phase")]
    [InlineData("upstream.node_with_underscores.field")]
    public void TryParse_ValidPath_ReturnsTrue(string path)
    {
        var ok = GatePath.TryParse(path, out var segments, out var error);

        ok.Should().BeTrue(because: $"'{path}' is a valid gate path");
        error.Should().BeEmpty();
        segments.Should().HaveCountGreaterThanOrEqualTo(2);
        segments[0].Should().BeOneOf("upstream", "holon");
    }

    [Theory]
    [InlineData("upstream.nodeName")]
    [InlineData("holon.3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    public void TryParse_TwoSegmentPath_IsValid(string path)
    {
        // Two segments (root + one name) is the minimum valid path.
        var ok = GatePath.TryParse(path, out var segments, out _);

        ok.Should().BeTrue();
        segments.Should().HaveCount(2);
    }

    [Fact]
    public void TryParse_UpstreamRoot_Accepted()
    {
        GatePath.TryParse("upstream.gate.amount", out var segs, out _);
        segs[0].Should().Be("upstream");
    }

    [Fact]
    public void TryParse_HolonRoot_Accepted()
    {
        var guid = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
        GatePath.TryParse($"holon.{guid}.status", out var segs, out _);
        segs[0].Should().Be("holon");
        segs[1].Should().Be(guid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_EmptyOrWhitespace_ReturnsFalse(string path)
    {
        var ok = GatePath.TryParse(path, out _, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeEmpty();
    }

    [Fact]
    public void TryParse_NoRoot_SingleSegment_ReturnsFalse()
    {
        var ok = GatePath.TryParse("nodeName", out _, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("at least two");
    }

    [Fact]
    public void TryParse_UnknownRoot_ReturnsFalse()
    {
        var ok = GatePath.TryParse("reads.someKey.value", out _, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("reads").And.Contain("not valid");
    }

    [Fact]
    public void TryParse_ConsecutiveDots_ReturnsFalse()
    {
        var ok = GatePath.TryParse("upstream..field", out _, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("empty segment");
    }

    [Fact]
    public void TryParse_OperatorChars_ReturnsFalse()
    {
        var ok = GatePath.TryParse("upstream.gate.amount==100", out _, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("invalid characters");
    }

    [Fact]
    public void TryParse_LeadingHyphenSegment_ReturnsFalse()
    {
        var ok = GatePath.TryParse("upstream.-badStart.field", out _, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("invalid characters");
    }

    [Theory]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("00000000-0000-0000-0000-000000000001")]
    public void IsGuidShaped_ValidGuid_ReturnsTrue(string guid)
    {
        GatePath.IsGuidShaped(guid).Should().BeTrue();
    }

    [Theory]
    [InlineData("notAGuid")]
    [InlineData("status")]
    [InlineData("")]
    public void IsGuidShaped_NonGuid_ReturnsFalse(string value)
    {
        GatePath.IsGuidShaped(value).Should().BeFalse();
    }
}
