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

    private readonly Mock<IKycGateService> _kyc = new();
    private readonly Mock<IWalletManager> _walletManager = new();
    private readonly Mock<IWalletStore> _walletStore = new();
    private readonly Mock<IBlockchainProviderFactory> _factory = new();
    private readonly Mock<IBlockchainProvider> _provider = new();
    private readonly Mock<IAlgorandASAModule> _asa = new();
    private readonly InMemoryIdempotencyStore _idempotency = new();

    private readonly Guid _avatarId = Guid.NewGuid();
    private readonly Guid _callerAvatarId = Guid.NewGuid();

    private FungibleTokenManager BuildManager(INodeGovernanceGuard? nodeGovernance = null)
    {
        // Default: the provider resolves the ASA module and CreateASAAsync succeeds.
        WireProviderResolvesAsa();
        SetupCreateAsaSucceeds();
        return new(
            _kyc.Object, _walletManager.Object, _walletStore.Object,
            _factory.Object, _idempotency, nodeGovernance);
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

    private void ApproveKyc(Guid avatarId)
        => _kyc.Setup(k => k.RequireVerifiedAsync(avatarId))
               .ReturnsAsync(new AZOAResult<bool> { Result = true, Message = "Success" });

    private void DenyKyc(Guid avatarId)
        => _kyc.Setup(k => k.RequireVerifiedAsync(avatarId))
               .ReturnsAsync(new AZOAResult<bool>
               {
                   IsError = true,
                   Result = false,
                   Message = $"{KycAuthorizationError.Forbidden}{KycAuthorizationError.VerificationRequiredMessage}"
               });

    // ── Wallet helpers ─────────────────────────────────────────────────────────

    private static IWallet WalletFor(Guid avatarId, string chain) =>
        Mock.Of<IWallet>(w =>
            w.Id == Guid.NewGuid() &&
            w.AvatarId == avatarId &&
            w.ChainType == chain &&
            w.Address == "ALGOTESTADDRESS" &&
            w.WalletType == WalletType.Platform);

    private void HasWallet(Guid avatarId, IWallet wallet)
        => _walletStore.Setup(s => s.GetByAvatarAsync(avatarId, It.IsAny<CancellationToken>()))
                       .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new[] { wallet } });

    private void HasNoWallet(Guid avatarId)
        => _walletStore.Setup(s => s.GetByAvatarAsync(avatarId, It.IsAny<CancellationToken>()))
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
            _avatarId, CreateRequest(), _callerAvatarId, "blocked", ApiKeyId);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Node governance disallows fungible-token:create on asset type 'FungibleToken'");
        _kyc.Verify(k => k.RequireVerifiedAsync(It.IsAny<Guid>()), Times.Never);
        _walletStore.Verify(s => s.GetByAvatarAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _factory.Verify(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()), Times.Never);
        _asa.Verify(a => a.CreateASAAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<AZOA.WebAPI.Core.SigningContext>(), It.IsAny<CancellationToken>()), Times.Never);
        (await _idempotency.GetAsync($"fungible:{ApiKeyId}:blocked", CancellationToken.None)).Should().BeNull();
    }

    // ── Happy path ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_HappyPath_LaunchesAsaAndReturnsAssetId()
    {
        ApproveKyc(_avatarId);
        var wallet = WalletFor(_avatarId, ChainType);
        HasWallet(_avatarId, wallet);
        var manager = BuildManager();

        var result = await manager.CreateAsync(
            _avatarId, CreateRequest(), _callerAvatarId, "client_key_1", ApiKeyId);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.AssetId.Should().Be("12345");
        result.Result.AvatarId.Should().Be(_avatarId);
        result.Result.WalletAddress.Should().Be("ALGOTESTADDRESS");
        result.Result.Replayed.Should().BeFalse();

        // The avatar's custodial address is used for every ASA role (mechanism only).
        _asa.Verify(a => a.CreateASAAsync(
            "Project Token", "PRJ", 1_000_000, 6,
            "ALGOTESTADDRESS", "ALGOTESTADDRESS", "ALGOTESTADDRESS", "ALGOTESTADDRESS",
            "ALGOTESTADDRESS", It.IsAny<AZOA.WebAPI.Core.SigningContext>(), It.IsAny<CancellationToken>()), Times.Once);

        var record = await _idempotency.GetAsync(result.Result!.IdempotencyKey, CancellationToken.None);
        record!.State.Should().Be(IdempotencyState.Completed);
    }

    // ── KYC fail-closed ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_KycNotApproved_RejectsAndNeverCreatesToken()
    {
        DenyKyc(_avatarId);
        HasNoWallet(_avatarId);
        var manager = BuildManager();

        var result = await manager.CreateAsync(
            _avatarId, CreateRequest(), _callerAvatarId, "client_kyc", ApiKeyId);

        result.IsError.Should().BeTrue("a non-approved KYC avatar must be rejected fail-closed");
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
        ApproveKyc(_avatarId);
        var wallet = WalletFor(_avatarId, ChainType);
        HasWallet(_avatarId, wallet);
        var manager = BuildManager();

        var first = await manager.CreateAsync(
            _avatarId, CreateRequest(), _callerAvatarId, "dup_key", ApiKeyId);
        var second = await manager.CreateAsync(
            _avatarId, CreateRequest(), _callerAvatarId, "dup_key", ApiKeyId);

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
        _kyc.Setup(k => k.RequireVerifiedAsync(_avatarId))
            .ThrowsAsync(new InvalidOperationException("sensitive diagnostic"));
        var manager = BuildManager();

        Func<Task> act = async () => await manager.CreateAsync(
            _avatarId, CreateRequest(), _callerAvatarId, "pre_effect_failure", ApiKeyId);

        await act.Should().ThrowAsync<InvalidOperationException>();
        var record = await _idempotency.GetAsync(
            $"fungible:{ApiKeyId}:pre_effect_failure", CancellationToken.None);
        record!.State.Should().Be(IdempotencyState.Failed);
        record.Error.Should().Be("Fungible token creation failed unexpectedly.");
    }

    [Fact]
    public async Task CreateAsync_UnexpectedProviderFailure_LeavesClaimInProgressAndDoesNotRetryEffect()
    {
        ApproveKyc(_avatarId);
        HasWallet(_avatarId, WalletFor(_avatarId, ChainType));
        var manager = BuildManager();
        _asa.Setup(a => a.CreateASAAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<AZOA.WebAPI.Core.SigningContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ambiguous provider failure"));

        Func<Task> first = async () => await manager.CreateAsync(
            _avatarId, CreateRequest(), _callerAvatarId, "ambiguous", ApiKeyId);
        await first.Should().ThrowAsync<InvalidOperationException>();

        var replay = await manager.CreateAsync(
            _avatarId, CreateRequest(), _callerAvatarId, "ambiguous", ApiKeyId);
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
