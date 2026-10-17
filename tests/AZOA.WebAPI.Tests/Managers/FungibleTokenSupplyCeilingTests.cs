using System.Globalization;
using System.Numerics;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Services.Reserve;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Managers;

public sealed class FungibleTokenSupplyCeilingTests
{
    private const ulong OldIntCeiling = int.MaxValue;
    [Fact]
    public void TryNarrowToRailTotal_accepts_the_old_int_ceiling()
    {
        var accepted = AlgorandAsaSupplyLimits.TryNarrowToRailTotal(
            new BigInteger(OldIntCeiling), out var railTotal, out var error);

        accepted.Should().BeTrue(error);
        railTotal.Should().Be(OldIntCeiling);
    }

    [Fact]
    public void TryNarrowToRailTotal_accepts_one_base_unit_above_the_old_int_ceiling()
    {
        var accepted = AlgorandAsaSupplyLimits.TryNarrowToRailTotal(
            new BigInteger(OldIntCeiling) + 1, out var railTotal, out var error);

        accepted.Should().BeTrue(error);
        railTotal.Should().Be(OldIntCeiling + 1);
    }

    [Fact]
    public void TryNarrowToRailTotal_accepts_the_rail_ceiling_exactly()
    {
        var accepted = AlgorandAsaSupplyLimits.TryNarrowToRailTotal(
            new BigInteger(AlgorandAsaSupplyLimits.MaxTotalBaseUnits), out var railTotal, out var error);

        accepted.Should().BeTrue(error);
        railTotal.Should().Be(ulong.MaxValue, "an Algorand ASA total is a chain-side uint64");
    }

    [Fact]
    public void TryNarrowToRailTotal_refuses_above_the_rail_ceiling_by_name_instead_of_truncating()
    {
        var accepted = AlgorandAsaSupplyLimits.TryNarrowToRailTotal(
            new BigInteger(AlgorandAsaSupplyLimits.MaxTotalBaseUnits) + 1, out var railTotal, out var error);

        accepted.Should().BeFalse();
        railTotal.Should().Be(0UL, "a refused total must never leak a truncated one");
        error.Should().Contain(AlgorandAsaSupplyLimits.MaxTotalBaseUnits.ToString());
        error.Should().Contain("uint64", "the refusal must name the rail, not a limit of this node");
    }

    [Fact]
    public void TryNarrowToRailTotal_refuses_a_non_positive_total()
    {
        AlgorandAsaSupplyLimits.TryNarrowToRailTotal(BigInteger.Zero, out _, out var zeroError)
            .Should().BeFalse();
        zeroError.Should().Contain("positive");

        AlgorandAsaSupplyLimits.TryNarrowToRailTotal(BigInteger.MinusOne, out _, out var negativeError)
            .Should().BeFalse();
        negativeError.Should().Contain("positive");
    }

    [Fact]
    public void The_genesis_ceiling_is_the_rail_ceiling_and_the_intended_total_is_reachable()
    {
        PlatformTokenGenesisDefaults.ProviderSupplyCeiling
            .Should().Be(AlgorandAsaSupplyLimits.MaxTotalBaseUnits);
        var accepted = AlgorandAsaSupplyLimits.TryNarrowToRailTotal(
            new BigInteger(PlatformTokenGenesisDefaults.IntendedTotalBaseUnits),
            out var railTotal, out var error);

        accepted.Should().BeTrue(error);
        railTotal.Should().Be(PlatformTokenGenesisDefaults.IntendedTotalBaseUnits);
    }


    [Theory]
    [InlineData(2147483647UL)]
    [InlineData(2147483648UL)]
    [InlineData(18446744073709551615UL)]
    public async Task Durable_admission_preserves_exact_total_without_preconsent_provider_calls(ulong total)
    {
        var harness = new FungibleAdmissionHarness();
        var result = await harness.AdmitAsync(harness.Request(total));
        result.IsError.Should().BeFalse(result.Message);
        harness.Commands.Values.Single().Intent.TotalBaseUnits.Should().Be(total.ToString(CultureInfo.InvariantCulture));
        result.Result!.AssetId.Should().BeEmpty();
        harness.Module.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Zero_total_is_refused_before_any_durable_creation_or_custody()
    {
        var harness = new FungibleAdmissionHarness();
        (await harness.AdmitAsync(harness.Request(0))).IsError.Should().BeTrue();
        harness.Commands.Should().BeEmpty();
        harness.Module.VerifyNoOtherCalls();
    }
}
