// SPDX-License-Identifier: UNLICENSED

using System.Numerics;
using FluentAssertions;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Reserve;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>
/// OI-26: the genesis supply ceiling. The ASA module surface used to take an <c>int</c>
/// total and <see cref="FungibleTokenManager"/> narrowed onto it with
/// <c>checked((int)request.Total)</c>, so no asset this node could mint was allowed to
/// exceed 2,147,483,647 base units. An Algorand ASA total cannot be raised once the asset
/// exists — a bigger supply means a SECOND asset — so the ceiling had to move before
/// genesis, not after.
/// <para>These proofs are taken AT THE COUNTER: the assertion is the exact base-unit total
/// handed to <c>IAlgorandASAModule.CreateASAAsync</c>, because a truncated total would
/// still look like a perfectly successful mint everywhere else.</para>
/// </summary>
public class FungibleTokenSupplyCeilingTests
{
    private const string ApiKeyId = "33333333-3333-3333-3333-333333333333";
    private const string ChainType = "Algorand";
    private const string ReserveAddress = "ALGORESERVEADDRESS";
    private const string WalletAddress = "ALGOTESTADDRESS";

    /// <summary>The total the old <c>int</c> module surface could not exceed.</summary>
    private const ulong OldIntCeiling = int.MaxValue;

    private readonly Mock<IValueAccessService> _valueAccess = new();
    private readonly Mock<IWalletManager> _walletManager = new();
    private readonly Mock<IWalletStore> _walletStore = new();
    private readonly Mock<IBlockchainProviderFactory> _factory = new();
    private readonly Mock<IBlockchainProvider> _provider = new();
    private readonly Mock<IAlgorandASAModule> _asa = new();
    private readonly Mock<IPlatformReserve> _reserve = new();
    private readonly InMemoryIdempotencyStore _idempotency = new();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _callerUserId = Guid.NewGuid();

    /// <summary>The exact base-unit total the ASA rail was handed; null when it was never called.</summary>
    private ulong? _totalHandedToTheRail;

    private int _createAsaCallCount;

    // ── The rail ceiling itself ────────────────────────────────────────────────

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
        // The boundary OI-26 is about: the first total the pre-widening path could not
        // carry, and the one `checked((int)request.Total)` would have thrown on.
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
        // PlatformTokenGenesisDefaults aliases the rail limit rather than re-typing it, so
        // the genesis refusal and the module surface cannot drift apart.
        PlatformTokenGenesisDefaults.ProviderSupplyCeiling
            .Should().Be(AlgorandAsaSupplyLimits.MaxTotalBaseUnits);

        // The 10^15 base units N-X2 names had to become mintable BEFORE genesis: after the
        // asset exists a larger supply would mean a second, confusing unit.
        var accepted = AlgorandAsaSupplyLimits.TryNarrowToRailTotal(
            new BigInteger(PlatformTokenGenesisDefaults.IntendedTotalBaseUnits),
            out var railTotal, out var error);

        accepted.Should().BeTrue(error);
        railTotal.Should().Be(PlatformTokenGenesisDefaults.IntendedTotalBaseUnits);
    }

    // ── At the counter: the total the mint path actually hands the rail ────────

    [Fact]
    public async Task CreateAsync_at_the_old_int_ceiling_hands_the_rail_that_exact_total()
    {
        var manager = BuildManager();

        var result = await manager.CreateAsync(
            _userId, RequestFor(OldIntCeiling), _callerUserId, "ceiling_int_max", ApiKeyId);

        result.IsError.Should().BeFalse(result.Message);
        _totalHandedToTheRail.Should().Be(OldIntCeiling);
    }

    [Fact]
    public async Task CreateAsync_above_the_old_int_ceiling_hands_the_rail_that_exact_total()
    {
        // Before OI-26 this refused in TryValidateSupply ("exceeds the maximum supported by
        // the provider surface"). It must now mint, and mint the total that was ASKED FOR —
        // a truncated 1 base unit would look identical everywhere except right here.
        var manager = BuildManager();
        const ulong oneAbove = OldIntCeiling + 1;

        var result = await manager.CreateAsync(
            _userId, RequestFor(oneAbove), _callerUserId, "ceiling_int_max_plus_one", ApiKeyId);

        result.IsError.Should().BeFalse(result.Message);
        _totalHandedToTheRail.Should().Be(oneAbove);
    }

    [Fact]
    public async Task CreateAsync_at_the_rail_ceiling_hands_the_rail_that_exact_total()
    {
        var manager = BuildManager();

        var result = await manager.CreateAsync(
            _userId, RequestFor(AlgorandAsaSupplyLimits.MaxTotalBaseUnits),
            _callerUserId, "ceiling_rail", ApiKeyId);

        result.IsError.Should().BeFalse(result.Message);
        _totalHandedToTheRail.Should().Be(ulong.MaxValue);
    }

    [Fact]
    public async Task CreateAsync_still_refuses_a_zero_total_before_the_rail_is_touched()
    {
        // Widening the ceiling must not widen the floor, and the refusal must cost nothing:
        // the counter does not move and the claim is terminal, not a leaked InProgress.
        var manager = BuildManager();

        var result = await manager.CreateAsync(
            _userId, RequestFor(0UL), _callerUserId, "zero_total", ApiKeyId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("positive");
        _createAsaCallCount.Should().Be(0);
        _totalHandedToTheRail.Should().BeNull();

        var record = await _idempotency.GetAsync($"fungible:{ApiKeyId}:zero_total", CancellationToken.None);
        record!.State.Should().Be(IdempotencyState.Failed);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private FungibleTokenManager BuildManager()
    {
        _valueAccess
            .Setup(a => a.RequireValueAccessAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { Result = true, Message = "Success" });

        _walletStore
            .Setup(s => s.GetByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new[] { PlatformWallet() } });

        _factory
            .Setup(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
            .Returns(_provider.Object);

        var module = _asa.Object;
        _provider
            .Setup(p => p.TryGetModule<IAlgorandASAModule>(out It.Ref<IAlgorandASAModule?>.IsAny))
            .Returns((out IAlgorandASAModule? m) => { m = module; return true; });

        _asa.Setup(a => a.CreateASAAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<SigningContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<string> { Result = "98765" })
            .Callback<string, string, ulong, int, string, string, string, string, string,
                SigningContext, CancellationToken>(
                (_, _, total, _, _, _, _, _, _, _, _) =>
                {
                    _createAsaCallCount++;
                    _totalHandedToTheRail = total;
                });

        _reserve
            .Setup(r => r.GetAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<ReserveAccount>
            {
                Result = new ReserveAccount(ChainType, ReserveAddress, IsFeePayer: true),
            });
        _reserve
            .Setup(r => r.RegisterAssetAsync(It.IsAny<ReserveAssetRegistration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<AZOA.WebAPI.Persistence.SurrealDb.Models.PlatformReserveLedger>
            {
                Result = new AZOA.WebAPI.Persistence.SurrealDb.Models.PlatformReserveLedger(),
            });

        return new FungibleTokenManager(
            _valueAccess.Object, _walletManager.Object, _walletStore.Object,
            _factory.Object, _idempotency, _reserve.Object,
            new ReserveOptions { FungibleMintTarget = ReserveMintTargets.PlatformReserve });
    }

    private IWallet PlatformWallet() =>
        Mock.Of<IWallet>(w =>
            w.Id == Guid.NewGuid() &&
            w.UserId == _userId &&
            w.ChainType == ChainType &&
            w.Address == WalletAddress &&
            w.WalletType == WalletType.Platform);

    private static FungibleTokenCreateRequest RequestFor(ulong total) => new()
    {
        Name = "Project Token",
        UnitName = "PRJ",
        ChainType = ChainType,
        Total = total,
        Decimals = 2,
    };

    /// <summary>Claim-once idempotency ledger, faithful enough that "exactly once" is real.</summary>
    private sealed class InMemoryIdempotencyStore : IIdempotencyStore
    {
        private readonly Dictionary<string, IdempotencyRecord> _records = new(StringComparer.Ordinal);

        public Task<IdempotencyClaim> TryClaimAsync(string key, string operationType, CancellationToken ct)
        {
            if (_records.TryGetValue(key, out var existing))
                return Task.FromResult(new IdempotencyClaim(false, existing));

            var record = new IdempotencyRecord
            {
                Key = key,
                OperationType = operationType,
                State = IdempotencyState.InProgress,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _records[key] = record;
            return Task.FromResult(new IdempotencyClaim(true, record));
        }

        public Task CompleteAsync(string key, string resultPayload, CancellationToken ct)
        {
            if (_records.TryGetValue(key, out var record) && record.State == IdempotencyState.InProgress)
            {
                record.State = IdempotencyState.Completed;
                record.ResultPayload = resultPayload;
                record.UpdatedAt = DateTime.UtcNow;
            }
            return Task.CompletedTask;
        }

        public Task FailAsync(string key, string error, CancellationToken ct)
        {
            if (_records.TryGetValue(key, out var record) && record.State == IdempotencyState.InProgress)
            {
                record.State = IdempotencyState.Failed;
                record.Error = error;
                record.UpdatedAt = DateTime.UtcNow;
            }
            return Task.CompletedTask;
        }

        public Task<IdempotencyRecord?> GetAsync(string key, CancellationToken ct)
            => Task.FromResult(_records.TryGetValue(key, out var record) ? record : null);
    }
}
