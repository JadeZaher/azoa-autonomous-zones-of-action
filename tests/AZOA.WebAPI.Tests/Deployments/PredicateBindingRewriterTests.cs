using AZOA.WebAPI.Helpers.Deployments;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Deployments;

public sealed class PredicateBindingRewriterTests
{
    [Theory]
    [InlineData("run.foo-bar.value == 'run.foo-bar.value'", "run.o0_foo-bar.value == 'run.foo-bar.value'")]
    [InlineData("run . foo-bar . value", "run . o0_foo-bar . value")]
    [InlineData("reads.run.foo == upstream.foo-bar.run.foo", "reads.run.foo == upstream.o0_foo-bar.run.foo")]
    [InlineData("'escaped\\\' run.foo-bar.x' == run.foo-bar.x", "'escaped\\\' run.foo-bar.x' == run.o0_foo-bar.x")]
    public void OnlyActualRootSelectorsAreRenamed(string input, string expected)
        => PredicateBindingRewriter.Rewrite(input, new Dictionary<string, string> { ["foo-bar"] = "o0_foo-bar" }).Should().Be(expected);
}
