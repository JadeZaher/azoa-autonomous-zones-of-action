using System.Text.Json.Nodes;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed class TinymanRentReleaseHistoryTests
{
    [Fact]
    public void EarlierExternalInnerCloseWinsEvenWithoutAnInnerIdOrUniqueOffset()
    {
        var block = Block();
        var inner = Close("owner");
        inner["intra-round-offset"] = 2UL;
        var parent = new JsonObject
        {
            ["id"] = "external-parent", ["sender"] = "application", ["tx-type"] = "appl", ["intra-round-offset"] = 2UL,
            ["inner-txns"] = new JsonArray(inner),
        };
        block["transactions"]!.AsArray().Insert(1, parent);
        Scan(block, out var release).Should().BeTrue();
        release!.TransactionId.Should().Be("external-parent");
        release.InnerPath.Should().Be("/0000000000");
        release.OuterOffset.Should().Be(2);
    }

    [Fact]
    public void CleanupIsCreditedOnlyWhenItIsTheFirstCloseAfterOriginalOptIn()
    {
        var block = Block();
        Scan(block, out var release).Should().BeTrue();
        release!.TransactionId.Should().Be("cleanup");
        release.InnerPath.Should().BeEmpty();
        var later = Close("owner");
        later["id"] = "later";
        later["intra-round-offset"] = 6UL;
        block["transactions"]!.AsArray().Add(later);
        Scan(block, out var withLater).Should().BeTrue();
        withLater!.TransactionId.Should().Be("cleanup");
    }

    [Fact]
    public void PaginationRetainsEarlierCandidateAndRefusesChangedEvidenceAtSamePosition()
    {
        var block = Block();
        Scan(block, out var release).Should().BeTrue();
        var prior = new TinymanRentReleaseEvent("earlier", 899, 3, "", "historical-block");
        TinymanRentReleaseHistory.ScanBlock("owner", 7, "genesis", 898, 0, 900, 4,
            TinymanSwapReceiptTests.Element(block), prior, out var retained).Should().BeTrue();
        retained.Should().Be(prior);
        var changed = release! with { BlockDigest = "different-block" };
        TinymanRentReleaseHistory.ScanBlock("owner", 7, "genesis", 900, 0, 900, 4,
            TinymanSwapReceiptTests.Element(block), changed, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("genesis")]
    [InlineData("duplicate-offset")]
    [InlineData("clawback-close")]
    public void AmbiguousOrImpossibleHistoryNeverProducesReleaseProof(string mutation)
    {
        var block = Block();
        if (mutation == "genesis") block["genesis-hash"] = "other";
        if (mutation == "duplicate-offset") block["transactions"]![1]!["intra-round-offset"] = 0UL;
        if (mutation == "clawback-close") block["transactions"]![1]!["asset-transfer-transaction"]!["sender"] = "other";
        Scan(block, out _).Should().BeFalse();
    }

    private static bool Scan(JsonObject block, out TinymanRentReleaseEvent? release)
        => TinymanRentReleaseHistory.ScanBlock("owner", 7, "genesis", 900, 0, 900, 4,
            TinymanSwapReceiptTests.Element(block), null, out release);

    private static JsonObject Block()
    {
        var allocation = new JsonObject { ["id"] = "opt-in", ["intra-round-offset"] = 0UL, ["sender"] = "owner", ["tx-type"] = "axfer",
            ["asset-transfer-transaction"] = new JsonObject { ["asset-id"] = 7UL, ["amount"] = 0UL, ["receiver"] = "owner" } };
        var cleanup = Close("owner");
        cleanup["id"] = "cleanup";
        cleanup["intra-round-offset"] = 4UL;
        return new() { ["round"] = 900UL, ["genesis-hash"] = "genesis", ["transactions"] = new JsonArray(allocation, cleanup) };
    }

    private static JsonObject Close(string owner) => new()
    {
        ["sender"] = owner, ["tx-type"] = "axfer", ["asset-transfer-transaction"] = new JsonObject
        {
            ["asset-id"] = 7UL, ["amount"] = 0UL, ["receiver"] = owner, ["close-to"] = owner, ["close-amount"] = 0UL,
        },
    };
}
