using AZOA.WebAPI.Helpers;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttEvidenceProgressTests
{
    [Fact]
    public void Fixed_binding_and_each_page_commit_exact_state_and_cursor_without_lifetime_cutoff()
    {
        var row = WttEvidenceProgressContract.Begin("{\"application\":7,\"round\":500}", "{\"created\":false}");
        var firstId = row.Id; var firstDigest = row.Digest;
        for (var index = 0; index < 150; index++)
            row = WttEvidenceProgressContract.Advance(row, "{\"created\":true}", new string('a', 64), "cursor-" + index);
        Assert.False(row.Complete); Assert.Equal(150, row.Pages); Assert.Equal(firstId, row.Id);
        Assert.NotEqual(firstDigest, row.Digest);
        var final = WttEvidenceProgressContract.Advance(row, "{\"created\":true}", new string('b', 64), null);
        Assert.True(final.Complete); Assert.Null(final.NextCursor); Assert.Equal(151, final.Pages);
        Assert.Throws<InvalidDataException>(() => WttEvidenceProgressContract.Advance(final, "{}", new string('c', 64), null));
    }

    [Fact]
    public void Multi_page_cursor_cycle_and_corrupt_lineage_are_refused()
    {
        var row = WttEvidenceProgressContract.Begin("{\"round\":500}", "{}");
        row = WttEvidenceProgressContract.Advance(row, "{}", new string('a', 64), "A");
        row = WttEvidenceProgressContract.Advance(row, "{}", new string('b', 64), "B");
        Assert.Throws<InvalidDataException>(() => WttEvidenceProgressContract.Advance(row, "{}", new string('c', 64), "A"));
        row.NextCursor = "forged";
        Assert.Throws<InvalidDataException>(() => WttEvidenceProgressContract.Advance(row, "{}", new string('c', 64), "C"));
    }

    [Fact]
    public void Distinct_physical_or_execution_boundaries_cannot_share_evidence_cursor()
    {
        var original = WttEvidenceProgressContract.Begin("{\"application\":7,\"round\":500}", "{}");
        var next = WttEvidenceProgressContract.Begin("{\"application\":7,\"round\":501}", "{}");
        Assert.NotEqual(original.Id, next.Id);
        Assert.Throws<InvalidDataException>(() => WttEvidenceProgressContract.Begin("{ \"round\":500 }", "{}"));
    }
}
