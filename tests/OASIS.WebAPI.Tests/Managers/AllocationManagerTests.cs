// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using Moq;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Interfaces.Stores;
using OASIS.WebAPI.Managers;
using OASIS.WebAPI.Models.Idempotency;
using OASIS.WebAPI.Models.Kyc;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Tests.Managers;

/// <summary>
/// Replay-safety + fail-closed + provision-if-absent + IDOR proofs for the
/// fiat-settlement allocation seam. Uses an in-memory <see cref="IIdempotencyStore"/>
/// that faithfully reproduces claim-once / replay-cached semantics so the
/// "mint/transfer happens exactly once" assertions are real, not mock-scripted.
/// </summary>
public class AllocationManagerTests
{
    private const string ApiKeyId = "11111111-1111-1111-1111-111111111111";
    private const string ChainType = "Algorand";

    private readonly Mock<IKycGateService> _kyc = new();
    private readonly Mock<IWalletManager> _walletManager = new();
    private readonly Mock<IWalletStore> _walletStore = new();
    private readonly Mock<INftManager> _nft = new();
    private readonly InMemoryIdempotencyStore _idempotency = new();

    private readonly Guid _avatarId = Guid.NewGuid();
    private readonly Guid _callerAvatarId = Guid.NewGuid();

    private AllocationManager BuildManager() => new(
        _kyc.Object, _walletManager.Object, _walletStore.Object, _nft.Object, _idempotency);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void ApproveKyc(Guid avatarId)
        => _kyc.Setup(k => k.RequireVerifiedAsync(avatarId))
               .ReturnsAsync(new OASISResult<bool> { Result = true, Message = "Success" });

    private void DenyKyc(Guid avatarId)
        => _kyc.Setup(k => k.RequireVerifiedAsync(avatarId))
               .ReturnsAsync(new OASISResult<bool>
               {
                   IsError = true,
                   Result = false,
                   Message = $"{KycAuthorizationError.Forbidden}{KycAuthorizationError.VerificationRequiredMessage}"
               });

    private static IWallet WalletFor(Guid avatarId, string chain) =>
        Mock.Of<IWallet>(w =>
            w.Id == Guid.NewGuid() &&
            w.AvatarId == avatarId &&
            w.ChainType == chain &&
            w.Address == "ALGOTESTADDRESS" &&
            w.WalletType == WalletType.Platform);

    private void HasNoWallet(Guid avatarId)
        => _walletStore.Setup(s => s.GetByAvatarAsync(avatarId, It.IsAny<CancellationToken>()))
                       .ReturnsAsync(new OASISResult<IEnumerable<IWallet>> { Result = Array.Empty<IWallet>() });

    private void HasWallet(Guid avatarId, IWallet wallet)
        => _walletStore.Setup(s => s.GetByAvatarAsync(avatarId, It.IsAny<CancellationToken>()))
                       .ReturnsAsync(new OASISResult<IEnumerable<IWallet>> { Result = new[] { wallet } });

    private void SetupGenerate(Guid avatarId, IWallet wallet)
        => _walletManager.Setup(m => m.GenerateWalletAsync(
                It.IsAny<WalletGenerateRequest>(), avatarId, It.IsAny<OASISRequest?>()))
            .ReturnsAsync(new OASISResult<IWallet> { Result = wallet });

    private void SetupMintSucceeds()
        => _nft.Setup(n => n.MintAsync(It.IsAny<NftMintRequest>(), It.IsAny<Guid>(), It.IsAny<OASISRequest?>()))
               .ReturnsAsync(new OASISResult<IBlockchainOperation>
               {
                   Result = Mock.Of<IBlockchainOperation>(o => o.Id == Guid.NewGuid())
               });

    private static AllocationRequest MintRequest() => new()
    {
        Kind = AllocationKind.Mint,
        ChainType = ChainType,
        Amount = "100.00",
        Name = "Project Alpha",
        AssetId = "PRJALPHA"
    };

    // ── Idempotency ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AllocateAsync_DuplicateKey_ReplaysOriginalAndMintsExactlyOnce()
    {
        ApproveKyc(_avatarId);
        var wallet = WalletFor(_avatarId, ChainType);
        HasWallet(_avatarId, wallet);
        SetupMintSucceeds();
        var manager = BuildManager();

        var first = await manager.AllocateAsync(
            _avatarId, MintRequest(), _callerAvatarId, "pi_stable_key", ApiKeyId);
        var second = await manager.AllocateAsync(
            _avatarId, MintRequest(), _callerAvatarId, "pi_stable_key", ApiKeyId);

        first.IsError.Should().BeFalse();
        first.Result!.Replayed.Should().BeFalse();

        second.IsError.Should().BeFalse();
        second.Result!.Replayed.Should().BeTrue();
        second.Result.OperationId.Should().Be(first.Result.OperationId);

        // The irreversible effect ran exactly once across the duplicate calls.
        _nft.Verify(n => n.MintAsync(It.IsAny<NftMintRequest>(), It.IsAny<Guid>(), It.IsAny<OASISRequest?>()),
            Times.Once);
    }

    [Fact]
    public async Task AllocateAsync_AbsentClientKey_UsesDeterministicContentKey_StillDedupes()
    {
        ApproveKyc(_avatarId);
        var wallet = WalletFor(_avatarId, ChainType);
        HasWallet(_avatarId, wallet);
        SetupMintSucceeds();
        var manager = BuildManager();

        // No client Idempotency-Key on either call ⇒ deterministic content key.
        var first = await manager.AllocateAsync(_avatarId, MintRequest(), _callerAvatarId, null, ApiKeyId);
        var second = await manager.AllocateAsync(_avatarId, MintRequest(), _callerAvatarId, null, ApiKeyId);

        first.IsError.Should().BeFalse();
        second.Result!.Replayed.Should().BeTrue();
        _nft.Verify(n => n.MintAsync(It.IsAny<NftMintRequest>(), It.IsAny<Guid>(), It.IsAny<OASISRequest?>()),
            Times.Once);
    }

    // ── KYC fail-closed ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("pending")]
    [InlineData("rejected")]
    [InlineData("unknown")]
    public async Task AllocateAsync_KycNotApproved_RejectsAndNeverMints(string kycState)
    {
        DenyKyc(_avatarId);
        HasNoWallet(_avatarId);
        SetupMintSucceeds();
        var manager = BuildManager();

        var result = await manager.AllocateAsync(
            _avatarId, MintRequest(), _callerAvatarId, $"pi_{kycState}", ApiKeyId);

        result.IsError.Should().BeTrue($"a {kycState} KYC avatar must be rejected fail-closed");
        result.Message.Should().StartWith(KycAuthorizationError.Forbidden);

        // No value-bearing side effect: never minted, never provisioned.
        _nft.Verify(n => n.MintAsync(It.IsAny<NftMintRequest>(), It.IsAny<Guid>(), It.IsAny<OASISRequest?>()),
            Times.Never);
        _walletManager.Verify(m => m.GenerateWalletAsync(
            It.IsAny<WalletGenerateRequest>(), It.IsAny<Guid>(), It.IsAny<OASISRequest?>()), Times.Never);
    }

    // ── Provision-if-absent ──────────────────────────────────────────────────

    [Fact]
    public async Task AllocateAsync_NoWallet_GeneratesExactlyOnce()
    {
        ApproveKyc(_avatarId);
        HasNoWallet(_avatarId);
        SetupGenerate(_avatarId, WalletFor(_avatarId, ChainType));
        SetupMintSucceeds();
        var manager = BuildManager();

        var result = await manager.AllocateAsync(
            _avatarId, MintRequest(), _callerAvatarId, "pi_key", ApiKeyId);

        result.IsError.Should().BeFalse();
        result.Result!.WalletProvisioned.Should().BeTrue();
        _walletManager.Verify(m => m.GenerateWalletAsync(
            It.IsAny<WalletGenerateRequest>(), _avatarId, It.IsAny<OASISRequest?>()), Times.Once);
    }

    [Fact]
    public async Task AllocateAsync_ExistingWallet_ReusedNotDuplicated()
    {
        ApproveKyc(_avatarId);
        HasWallet(_avatarId, WalletFor(_avatarId, ChainType));
        SetupMintSucceeds();
        var manager = BuildManager();

        var result = await manager.AllocateAsync(
            _avatarId, MintRequest(), _callerAvatarId, "pi_key", ApiKeyId);

        result.IsError.Should().BeFalse();
        result.Result!.WalletProvisioned.Should().BeFalse();
        _walletManager.Verify(m => m.GenerateWalletAsync(
            It.IsAny<WalletGenerateRequest>(), It.IsAny<Guid>(), It.IsAny<OASISRequest?>()), Times.Never);
    }

    // ── IDOR ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AllocateAsync_TransferAlwaysTargetsContractAvatar_NotBody()
    {
        ApproveKyc(_avatarId);
        var wallet = WalletFor(_avatarId, ChainType);
        HasWallet(_avatarId, wallet);

        NftTransferRequest? captured = null;
        _nft.Setup(n => n.TransferAsync(
                It.IsAny<Guid>(), It.IsAny<NftTransferRequest>(), It.IsAny<Guid>(), It.IsAny<OASISRequest?>()))
            .Callback<Guid, NftTransferRequest, Guid, OASISRequest?>((_, req, _, _) => captured = req)
            .ReturnsAsync(new OASISResult<IBlockchainOperation>
            {
                Result = Mock.Of<IBlockchainOperation>(o => o.Id == Guid.NewGuid())
            });

        var attacker = Guid.NewGuid(); // a hostile "owner" the caller might try to inject
        var request = new AllocationRequest
        {
            Kind = AllocationKind.Transfer,
            ChainType = ChainType,
            Amount = "100.00",
            AssetRecordId = Guid.NewGuid(),
            Memo = "x"
        };

        var result = await manager_AllocateAttackerBody(request, attacker);

        result.IsError.Should().BeFalse();
        captured.Should().NotBeNull();
        // The transfer target is the authorised contract avatar, never the attacker.
        captured!.TargetAvatarId.Should().Be(_avatarId);
        captured.TargetAvatarId.Should().NotBe(attacker);
    }

    /// <summary>
    /// AllocationRequest carries no owner id by design (IDOR-resistant). This
    /// helper documents that even when a caller is the attacker avatar, the
    /// allocation still targets the contract <c>avatarId</c> route value.
    /// </summary>
    private Task<OASISResult<AllocationResult>> manager_AllocateAttackerBody(
        AllocationRequest request, Guid attackerCaller)
    {
        var manager = BuildManager();
        return manager.AllocateAsync(_avatarId, request, attackerCaller, "pi_transfer", ApiKeyId);
    }

    // ── In-memory idempotency store (faithful claim-once + replay) ────────────

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
