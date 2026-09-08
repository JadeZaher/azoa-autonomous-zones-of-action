// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Governance;
using AZOA.WebAPI.Services.Reserve;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>
/// Replay-safety + fail-closed proofs for the fungible-token (ASA) launch seam
/// (<see cref="FungibleTokenManager"/>). Uses an in-memory
/// <see cref="IIdempotencyStore"/> that faithfully reproduces claim-once / replay
/// semantics so the "create-token-happens-exactly-once" assertions are real, not
/// mock-scripted. The Algorand ASA capability module is mocked behind a mocked
/// provider + factory.
/// </summary>
public class FungibleTokenManagerTests
{
    private const string ApiKeyId = "22222222-2222-2222-2222-222222222222";
    private const string ChainType = "Algorand";

    /// <summary>The reserve account the default mint policy creates every asset under.</summary>
    private const string RESERVEADDRESS = "ALGORESERVEADDRESS";

    private readonly Mock<IValueAccessService> _valueAccess = new();
    private readonly Mock<IWalletManager> _walletManager = new();
    private readonly Mock<IWalletStore> _walletStore = new();
    private readonly Mock<IBlockchainProviderFactory> _factory = new();
    private readonly Mock<IBlockchainProvider> _provider = new();
    private readonly Mock<IAlgorandASAModule> _asa = new();
    private readonly InMemoryIdempotencyStore _idempotency = new();
    private readonly Mock<IPlatformReserve> _reserve = new();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _callerUserId = Guid.NewGuid();

    /// <summary>
    /// Builds the manager on the node's DEFAULT mint policy (W3.1): the asset is created
    /// BY the platform reserve, which therefore holds all four ASA authorities. Pass
    /// <see cref="ReserveMintTargets.CallerWallet"/> to exercise the legacy target.
    /// </summary>
    private FungibleTokenManager BuildManager(
        INodeGovernanceGuard? nodeGovernance = null,
        string mintTarget = ReserveMintTargets.PlatformReserve)
    {
        // Default: the provider resolves the ASA module and CreateASAAsync succeeds.
        WireProviderResolvesAsa();
        SetupCreateAsaSucceeds();
        WireReserveResolves();
        return new(
            _valueAccess.Object, _walletManager.Object, _walletStore.Object,
            _factory.Object, _idempotency, _reserve.Object,
            new ReserveOptions { FungibleMintTarget = mintTarget },
            nodeGovernance);
    }

    /// <summary>The reserve resolves an address and accepts the asset's ledger row.</summary>
    private void WireReserveResolves(string address = RESERVEADDRESS)
    {
        _reserve.Setup(r => r.GetAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<ReserveAccount>
            {
                Result = new ReserveAccount(ChainType, address, IsFeePayer: true),
            });
        _reserve.Setup(r => r.RegisterAssetAsync(
                It.IsAny<ReserveAssetRegistration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<AZOA.WebAPI.Persistence.SurrealDb.Models.PlatformReserveLedger>
            {
                Result = new AZOA.WebAPI.Persistence.SurrealDb.Models.PlatformReserveLedger(),
            });
    }

    // ── Provider / module wiring ───────────────────────────────────────────────

    private void WireProviderResolvesAsa()
    {
        _factory.Setup(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
                .Returns(_provider.Object);

        var module = _asa.Object;
        _provider
            .Setup(p => p.TryGetModule<IAlgorandASAModule>(out It.Ref<IAlgorandASAModule?>.IsAny))
            .Returns((out IAlgorandASAModule? m) => { m = module; return true; });
    }

    private void SetupCreateAsaSucceeds(string assetId = "12345")
        // The manager now calls the SigningContext overload (tenant-consent-delegation
        // AC4b) so a tenant-driven ASA create can be consent-gated.
        => _asa.Setup(a => a.CreateASAAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<AZOA.WebAPI.Core.SigningContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<string> { Result = assetId });

    // ── KYC helpers ────────────────────────────────────────────────────────────

    private void ApproveKyc(Guid userId)
        => _valueAccess.Setup(k => k.RequireValueAccessAsync(userId, null, It.IsAny<CancellationToken>()))
               .ReturnsAsync(new AZOAResult<bool> { Result = true, Message = "Success" });

    private void DenyKyc(Guid userId)
        => _valueAccess.Setup(k => k.RequireValueAccessAsync(userId, null, It.IsAny<CancellationToken>()))
               .ReturnsAsync(new AZOAResult<bool>
               {
                   IsError = true,
                   Result = false,
                   Message = $"{KycAuthorizationError.Forbidden}{KycAuthorizationError.VerificationRequiredMessage}"
               });

    [Fact]
    public async Task CreateAsync_TenantRequest_ForwardsTenantToValueAccess()
    {
        var tenantId = Guid.NewGuid();
        _valueAccess.Setup(access => access.RequireValueAccessAsync(_userId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<bool>.Failure("participant is not ready"));
        var manager = BuildManager();

        await manager.CreateAsync(_userId, CreateRequest(), _callerUserId, "tenant-readiness", ApiKeyId, tenantId);

        _valueAccess.Verify(
            access => access.RequireValueAccessAsync(_userId, tenantId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Wallet helpers ─────────────────────────────────────────────────────────

    private static IWallet WalletFor(Guid userId, string chain) =>
        Mock.Of<IWallet>(w =>
            w.Id == Guid.NewGuid() &&
            w.UserId == userId &&
            w.ChainType == chain &&
            w.Address == "ALGOTESTADDRESS" &&
            w.WalletType == WalletType.Platform);

    private void HasWallet(Guid userId, IWallet wallet)
        => _walletStore.Setup(s => s.GetByUserAsync(userId, It.IsAny<CancellationToken>()))
                       .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new[] { wallet } });

    private void HasNoWallet(Guid userId)
        => _walletStore.Setup(s => s.GetByUserAsync(userId, It.IsAny<CancellationToken>()))
                       .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = Array.Empty<IWallet>() });

    private static FungibleTokenCreateRequest CreateRequest() => new()
    {
        Name = "Project Token",
        UnitName = "PRJ",
        ChainType = ChainType,
        Total = 1_000_000,
        Decimals = 6
    };

    [Fact]
    public async Task CreateAsync_NodeGovernanceDisallowedAssetType_RejectsBeforeClaimOrSideEffects()
    {
        var guard = new NodeGovernanceGuard(Options.Create(new NodeGovernanceOptions
        {
            AllowedAssetTypes = new[] { "NFT" }
        }));
        var manager = BuildManager(guard);

        var result = await manager.CreateAsync(
            _userId, CreateRequest(), _callerUserId, "blocked", ApiKeyId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Node governance disallows fungible-token:create on asset type 'FungibleToken'");
        _valueAccess.Verify(k => k.RequireValueAccessAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        _walletStore.Verify(s => s.GetByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _factory.Verify(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()), Times.Never);
        _asa.Verify(a => a.CreateASAAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<AZOA.WebAPI.Core.SigningContext>(), It.IsAny<CancellationToken>()), Times.Never);
        (await _idempotency.GetAsync($"fungible:{ApiKeyId}:blocked", CancellationToken.None)).Should().BeNull();
    }

    // ── Mint destination (W3.1) ────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_DefaultTarget_OpensTheAssetsReserveLedgerRow()
    {
        // The ledger row is what lets a later allocation REFUSE an oversubscribed draw.
        // Creating the asset without it would leave a partition check with nothing to read.
        ApproveKyc(_userId);
        HasWallet(_userId, WalletFor(_userId, ChainType));
        var manager = BuildManager();

        var result = await manager.CreateAsync(
            _userId, CreateRequest(), _callerUserId, "reserve_ledger_key", ApiKeyId);

        result.IsError.Should().BeFalse(result.Message);
        result.Code.Should().BeNull("a clean create opens the ledger row without a warning");
        _reserve.Verify(r => r.RegisterAssetAsync(
            It.Is<ReserveAssetRegistration>(reg =>
                reg.ChainType == ChainType &&
                reg.AssetId == "12345" &&
                reg.UnitName == "PRJ" &&
                reg.Decimals == 6),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_LegacyCallerWalletTarget_KeepsTheCallerAsCreator_AndOpensNoLedgerRow()
    {
        // The legacy target still resolves for an operator who configures it explicitly.
        // It takes NO reserve ledger row on purpose: a caller-held asset has no reserve
        // budget to partition, so a row would be a partition nothing can ever draw from.
        ApproveKyc(_userId);
        HasWallet(_userId, WalletFor(_userId, ChainType));
        var manager = BuildManager(mintTarget: ReserveMintTargets.CallerWallet);

        var result = await manager.CreateAsync(
            _userId, CreateRequest(), _callerUserId, "caller_wallet_key", ApiKeyId);

        result.IsError.Should().BeFalse(result.Message);
        _asa.Verify(a => a.CreateASAAsync(
            "Project Token", "PRJ", 1_000_000, 6,
            "ALGOTESTADDRESS", "ALGOTESTADDRESS", "ALGOTESTADDRESS", "ALGOTESTADDRESS",
            "ALGOTESTADDRESS", It.IsAny<AZOA.WebAPI.Core.SigningContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _reserve.Verify(r => r.RegisterAssetAsync(
            It.IsAny<ReserveAssetRegistration>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Happy path ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_HappyPath_LaunchesAsaAndReturnsAssetId()
    {
        ApproveKyc(_userId);
        var wallet = WalletFor(_userId, ChainType);
        HasWallet(_userId, wallet);
        var manager = BuildManager();

        var result = await manager.CreateAsync(
            _userId, CreateRequest(), _callerUserId, "client_key_1", ApiKeyId);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.AssetId.Should().Be("12345");
        result.Result.UserId.Should().Be(_userId);
        result.Result.WalletAddress.Should().Be("ALGOTESTADDRESS");
        result.Result.Replayed.Should().BeFalse();

        // W3.1: under the DEFAULT policy the reserve address is the creator and every
        // ASA role. The caller's wallet still receives the units (WalletAddress above),
        // but it holds no manager/reserve/freeze/clawback authority over the asset.
        _asa.Verify(a => a.CreateASAAsync(
            "Project Token", "PRJ", 1_000_000, 6,
            RESERVEADDRESS, RESERVEADDRESS, RESERVEADDRESS, RESERVEADDRESS,
            RESERVEADDRESS, It.IsAny<AZOA.WebAPI.Core.SigningContext>(), It.IsAny<CancellationToken>()), Times.Once);

        var record = await _idempotency.GetAsync($"fungible:{ApiKeyId}:client_key_1", CancellationToken.None);
        record!.State.Should().Be(IdempotencyState.Completed);

        var publicJson = System.Text.Json.JsonSerializer.Serialize(result.Result);
        publicJson.Should().NotContain(ApiKeyId)
            .And.NotContain("client_key_1")
            .And.NotContain("idempotencyKey");
    }

    // ── KYC fail-closed ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_KycNotApproved_RejectsAndNeverCreatesToken()
    {
        DenyKyc(_userId);
        HasNoWallet(_userId);
        var manager = BuildManager();

        var result = await manager.CreateAsync(
            _userId, CreateRequest(), _callerUserId, "client_kyc", ApiKeyId);

        result.IsError.Should().BeTrue("a non-approved KYC user must be rejected fail-closed");
        result.Message.Should().StartWith(KycAuthorizationError.Forbidden);

        // No value-bearing side effect: never created a token, never provisioned.
        _asa.Verify(a => a.CreateASAAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<AZOA.WebAPI.Core.SigningContext>(), It.IsAny<CancellationToken>()), Times.Never);
        _walletManager.Verify(m => m.GenerateWalletAsync(
            It.IsAny<WalletGenerateRequest>(), It.IsAny<Guid>(), It.IsAny<AZOARequest?>()), Times.Never);

        // The idempotency key is FAILED (terminal) — not leaked InProgress.
        var record = await _idempotency.GetAsync($"fungible:{ApiKeyId}:client_kyc", CancellationToken.None);
        record!.State.Should().Be(IdempotencyState.Failed);
    }

    // ── Idempotency replay ─────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_DuplicateKey_ReplaysOriginalAndCreatesExactlyOnce()
    {
        ApproveKyc(_userId);
        var wallet = WalletFor(_userId, ChainType);
        HasWallet(_userId, wallet);
        var manager = BuildManager();

        var first = await manager.CreateAsync(
            _userId, CreateRequest(), _callerUserId, "dup_key", ApiKeyId);
        var second = await manager.CreateAsync(
            _userId, CreateRequest(), _callerUserId, "dup_key", ApiKeyId);

        first.IsError.Should().BeFalse(first.Message);
        first.Result!.Replayed.Should().BeFalse();

        second.IsError.Should().BeFalse(second.Message);
        second.Result!.Replayed.Should().BeTrue();
        second.Result.AssetId.Should().Be(first.Result.AssetId);

        // The irreversible ASA creation ran EXACTLY ONCE across the duplicate calls.
        _asa.Verify(a => a.CreateASAAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<AZOA.WebAPI.Core.SigningContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_UnexpectedPreEffectFailure_BubblesAndSettlesSafeFailure()
    {
        _valueAccess.Setup(k => k.RequireValueAccessAsync(_userId, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("sensitive diagnostic"));
        var manager = BuildManager();

        Func<Task> act = async () => await manager.CreateAsync(
            _userId, CreateRequest(), _callerUserId, "pre_effect_failure", ApiKeyId);

        await act.Should().ThrowAsync<InvalidOperationException>();
        var record = await _idempotency.GetAsync(
            $"fungible:{ApiKeyId}:pre_effect_failure", CancellationToken.None);
        record!.State.Should().Be(IdempotencyState.Failed);
        record.Error.Should().Be("Fungible token creation failed unexpectedly.");
    }

    [Fact]
    public async Task CreateAsync_UnexpectedProviderFailure_LeavesClaimInProgressAndDoesNotRetryEffect()
    {
        ApproveKyc(_userId);
        HasWallet(_userId, WalletFor(_userId, ChainType));
        var manager = BuildManager();
        _asa.Setup(a => a.CreateASAAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<AZOA.WebAPI.Core.SigningContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ambiguous provider failure"));

        Func<Task> first = async () => await manager.CreateAsync(
            _userId, CreateRequest(), _callerUserId, "ambiguous", ApiKeyId);
        await first.Should().ThrowAsync<InvalidOperationException>();

        var replay = await manager.CreateAsync(
            _userId, CreateRequest(), _callerUserId, "ambiguous", ApiKeyId);
        replay.IsError.Should().BeTrue();
        replay.Message.Should().Contain("in progress");
        var record = await _idempotency.GetAsync(
            $"fungible:{ApiKeyId}:ambiguous", CancellationToken.None);
        record!.State.Should().Be(IdempotencyState.InProgress);
        _asa.Verify(a => a.CreateASAAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<AZOA.WebAPI.Core.SigningContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── In-memory idempotency store (faithful claim-once + replay) ──────────────

    private sealed class InMemoryIdempotencyStore : IIdempotencyStore
    {
        private readonly Dictionary<string, IdempotencyRecord> _records = new(StringComparer.Ordinal);

        public Task<IdempotencyClaim> TryClaimAsync(string key, string operationType, CancellationToken ct)
        {
            if (_records.TryGetValue(key, out var existing))
                return Task.FromResult(new IdempotencyClaim(false, Clone(existing)));

            var record = new IdempotencyRecord
            {
                Key = key,
                OperationType = operationType,
                State = IdempotencyState.InProgress,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _records[key] = record;
            return Task.FromResult(new IdempotencyClaim(true, Clone(record)));
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
            => Task.FromResult(_records.TryGetValue(key, out var r) ? Clone(r) : null);

        private static IdempotencyRecord Clone(IdempotencyRecord r) => new()
        {
            Key = r.Key,
            OperationType = r.OperationType,
            State = r.State,
            ResultPayload = r.ResultPayload,
            Error = r.Error,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt
        };
    }
}
