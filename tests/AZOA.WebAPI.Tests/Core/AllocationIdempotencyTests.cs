// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Core.Idempotency;
using AZOA.WebAPI.Models.Requests;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Core;

public sealed class AllocationIdempotencyTests
{
    [Fact]
    public void Create_WithClientKey_MatchesReceiptLookupCorrelationAfterWhitespaceTrim()
    {
        var apiKeyId = Guid.NewGuid();
        var request = new AllocationRequest
        {
            ChainType = "Algorand",
            Amount = "100",
            Name = "Unit",
        };

        var allocation = AllocationIdempotency.Create(
            apiKeyId, Guid.NewGuid(), request, "  payment-intent-42  ");
        var receipt = AllocationIdempotency.CreateFromClientKey(apiKeyId, "payment-intent-42");

        allocation.Correlation.Should().Be(receipt.Correlation);
        allocation.Correlation.Should().MatchRegex("^[0-9a-f]{64}$");
        allocation.Correlation.Should().NotContain("payment-intent-42");
    }

    [Fact]
    public void Create_WithoutClientKey_UsesStableContentIdentityButDifferentTargetChangesIt()
    {
        var apiKeyId = Guid.NewGuid();
        var request = new AllocationRequest
        {
            Kind = AllocationKind.Mint,
            ChainType = "Algorand",
            Amount = "100",
            AssetId = "unit-1",
        };
        var firstAvatar = Guid.NewGuid();

        var first = AllocationIdempotency.Create(apiKeyId, firstAvatar, request, null);
        var replay = AllocationIdempotency.Create(apiKeyId, firstAvatar, request, null);
        var differentTarget = AllocationIdempotency.Create(apiKeyId, Guid.NewGuid(), request, null);

        replay.Correlation.Should().Be(first.Correlation);
        differentTarget.Correlation.Should().NotBe(first.Correlation);
    }
}
