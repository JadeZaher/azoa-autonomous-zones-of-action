using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;

namespace AZOA.WebAPI.Tests.Managers;

public sealed class TenantCustodialAccountManagerTests
{
    private readonly Mock<ITenantManager> _tenants = new();
    private readonly Mock<IWalletManager> _wallets = new();
    private readonly Mock<IKycManager> _kyc = new();
    private readonly Mock<IBlockchainProviderFactory> _providers = new();
    private readonly Mock<IIdempotencyStore> _idempotency = new();

    public TenantCustodialAccountManagerTests()
    {
        _idempotency.Setup(store => store.TryClaimAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, string operation, CancellationToken _) => new IdempotencyClaim(
                true,
                new IdempotencyRecord
                {
                    Key = key,
                    OperationType = operation,
                    State = IdempotencyState.InProgress
                }));
    }

    [Fact]
    public async Task Ensure_RetryReturnsOneStableSecretFreeAvatarAndWallet()
    {
        var tenantId = Guid.NewGuid();
        var avatarId = Guid.NewGuid();
        var wallet = new Wallet
        {
            Id = Guid.NewGuid(),
            AvatarId = avatarId,
            ChainType = "Algorand",
            Address = "ALGOADDRESS",
            WalletType = WalletType.Platform,
            EncryptedPrivateKey = "ciphertext-never-on-wire"
        };
        _tenants.Setup(manager => manager.ProvisionChildAsync(
                tenantId,
                It.Is<ProvisionChildModel>(request => request.ExternalUserId == "user-42"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<ChildAvatarResponse>.Success(new ChildAvatarResponse
            {
                AvatarId = avatarId,
                ExternalUserId = "user-42"
            }));
        _wallets.Setup(manager => manager.BootstrapWalletAsync(
                It.IsAny<WalletGenerateRequest>(), avatarId, It.IsAny<AZOARequest?>()))
            .ReturnsAsync(AZOAResult<IWallet>.Success(wallet));
        _kyc.Setup(manager => manager.GetStatusAsync(avatarId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmissionModel>.Failure(
                KycAuthorizationError.NotFound + "No submission."));
        var manager = CreateManager();

        var first = await manager.EnsureAsync(tenantId, "user-42", "stable-idempotency-key-0001");
        var retry = await manager.EnsureAsync(tenantId, "user-42", "stable-idempotency-key-0001");

        first.IsError.Should().BeFalse();
        retry.Result!.AvatarId.Should().Be(first.Result!.AvatarId);
        retry.Result.WalletId.Should().Be(first.Result.WalletId);
        var json = JsonSerializer.Serialize(first.Result);
        json.Should().NotContain("ciphertext-never-on-wire");
        json.Should().NotContain("EncryptedPrivateKey");
        json.Should().NotContain("SeedPhrase");
    }

    [Fact]
    public async Task GetStatus_CrossTenantMissDoesNotProbeWalletOrKyc()
    {
        var tenantId = Guid.NewGuid();
        _tenants.Setup(manager => manager.ResolveChildAsync(
                tenantId, "other-tenant-user", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<ChildAvatarResponse>.Failure(
                TenantAuthorizationError.NotFound + "No child."));
        var manager = CreateManager();

        var result = await manager.GetStatusAsync(tenantId, "other-tenant-user");

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(TenantAuthorizationError.NotFound);
        _wallets.VerifyNoOtherCalls();
        _kyc.Verify(manager => manager.GetCapabilities(), Times.Never);
        _kyc.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SubmitKyc_ReturnsNoDocumentOrProviderDetail()
    {
        var tenantId = Guid.NewGuid();
        var avatarId = Guid.NewGuid();
        _tenants.Setup(manager => manager.ResolveChildAsync(
                tenantId, "user-42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<ChildAvatarResponse>.Success(new ChildAvatarResponse
            {
                AvatarId = avatarId,
                ExternalUserId = "user-42"
            }));
        _kyc.Setup(manager => manager.GetStatusAsync(avatarId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmissionModel>.Failure(
                KycAuthorizationError.NotFound + "No submission."));
        _kyc.Setup(manager => manager.SubmitAsync(
                It.IsAny<SubmitKycModel>(), avatarId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmissionModel>.Success(new KycSubmissionModel
            {
                Id = Guid.NewGuid(),
                AvatarId = avatarId,
                Status = KycStatus.PENDING,
                ProviderSessionId = "provider-secret-detail",
                ProviderResult = "provider-payload",
                ReviewerId = "reviewer",
                SubmittedAt = DateTime.UtcNow,
                Documents = new List<KycDocumentModel>
                {
                    new() { FileUrl = "https://documents.example/private-object", FileName = "identity.pdf" }
                }
            }));
        var manager = CreateManager();

        var result = await manager.SubmitKycAsync(tenantId, "user-42", new TenantKycSubmissionRequest
        {
            Documents = new List<TenantKycDocumentReferenceRequest>
            {
                new()
                {
                    Type = KycDocumentType.GOVERNMENT_ID,
                    ReferenceUrl = "https://documents.example/private-object",
                    FileName = "identity.pdf"
                }
            }
        });

        result.IsError.Should().BeFalse();
        var json = JsonSerializer.Serialize(result.Result);
        json.Should().NotContain("private-object");
        json.Should().NotContain("provider-secret-detail");
        json.Should().NotContain("provider-payload");
        json.Should().NotContain("reviewer");
    }

    [Fact]
    public async Task Ensure_MissingKmsProvisionsIdentityButSkipsWallet()
    {
        var tenantId = Guid.NewGuid();
        var avatarId = Guid.NewGuid();
        _tenants.Setup(manager => manager.ProvisionChildAsync(
                tenantId,
                It.Is<ProvisionChildModel>(request => request.ExternalUserId == "user-42"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<ChildAvatarResponse>.Success(new ChildAvatarResponse
            {
                AvatarId = avatarId,
                ExternalUserId = "user-42"
            }));
        _kyc.Setup(manager => manager.GetStatusAsync(avatarId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmissionModel>.Failure(
                KycAuthorizationError.NotFound + "No submission."));
        var manager = CreateManager(walletEncryptionKey: string.Empty);

        var result = await manager.EnsureAsync(
            tenantId, "user-42", "stable-idempotency-key-0001");

        result.IsError.Should().BeFalse();
        result.Result!.IdentityReady.Should().BeTrue();
        result.Result.KycReady.Should().BeTrue();
        result.Result!.Ready.Should().BeFalse();
        result.Result.WalletReady.Should().BeFalse();
        result.Result.UnavailableReason.Should().Contain("key management");
        _tenants.Verify(manager => manager.ProvisionChildAsync(
            tenantId,
            It.IsAny<ProvisionChildModel>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _wallets.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Ensure_ReusedIdempotencyKeyForDifferentSubjectIsRejectedBeforeSideEffects()
    {
        var tenantId = Guid.NewGuid();
        var manager = CreateManager();
        var payload = JsonSerializer.Serialize(new TenantCustodialAccountStatusResponse
        {
            TenantId = tenantId.ToString("D"),
            ExternalSubject = "first-user",
            Ready = false
        });
        _idempotency.Setup(store => store.TryClaimAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdempotencyClaim(
                false,
                new IdempotencyRecord
                {
                    State = IdempotencyState.Completed,
                    ResultPayload = payload
                }));

        var result = await manager.EnsureAsync(
            tenantId,
            "second-user",
            "stable-idempotency-key-0001");

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("already bound");
        _tenants.VerifyNoOtherCalls();
        _wallets.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Ensure_CompletedIdentityOnlyReplayConvergesMissingWalletStage()
    {
        var tenantId = Guid.NewGuid();
        var avatarId = Guid.NewGuid();
        var wallet = new Wallet
        {
            Id = Guid.NewGuid(),
            AvatarId = avatarId,
            ChainType = "Algorand",
            Address = "ALGOADDRESS",
            WalletType = WalletType.Platform,
            EncryptedPrivateKey = "ciphertext"
        };
        _idempotency.Setup(store => store.TryClaimAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdempotencyClaim(false, new IdempotencyRecord
            {
                OperationType = "tenant_custodial_ensure",
                State = IdempotencyState.Completed,
                ResultPayload = JsonSerializer.Serialize(new TenantCustodialAccountStatusResponse
                {
                    TenantId = tenantId.ToString("D"),
                    ExternalSubject = "user-42",
                    AvatarId = avatarId.ToString("D"),
                    IdentityReady = true,
                    WalletReady = false
                })
            }));
        _tenants.Setup(manager => manager.ProvisionChildAsync(
                tenantId,
                It.Is<ProvisionChildModel>(request => request.ExternalUserId == "user-42"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<ChildAvatarResponse>.Success(new ChildAvatarResponse
            {
                AvatarId = avatarId,
                ExternalUserId = "user-42"
            }));
        _wallets.Setup(manager => manager.BootstrapWalletAsync(
                It.IsAny<WalletGenerateRequest>(), avatarId, It.IsAny<AZOARequest?>()))
            .ReturnsAsync(AZOAResult<IWallet>.Success(wallet));
        _kyc.Setup(manager => manager.GetStatusAsync(avatarId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmissionModel>.Failure(
                KycAuthorizationError.NotFound + "No submission."));
        var manager = CreateManager();

        var result = await manager.EnsureAsync(
            tenantId, "user-42", "stable-idempotency-key-0001");

        result.IsError.Should().BeFalse();
        result.Result!.IdentityReady.Should().BeTrue();
        result.Result.WalletReady.Should().BeTrue();
        _wallets.Verify(manager => manager.BootstrapWalletAsync(
            It.IsAny<WalletGenerateRequest>(), avatarId, It.IsAny<AZOARequest?>()), Times.Once);
    }

    [Fact]
    public async Task BeginKyc_StableKeyDelegatesEnsureActiveAndReturnsNoProviderId()
    {
        var tenantId = Guid.NewGuid();
        var avatarId = Guid.NewGuid();
        string? operationType = null;
        string? payload = null;
        var claims = 0;
        _tenants.Setup(manager => manager.ResolveChildAsync(
                tenantId, "user-42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<ChildAvatarResponse>.Success(new ChildAvatarResponse
            {
                AvatarId = avatarId,
                ExternalUserId = "user-42"
            }));
        _idempotency.Setup(store => store.TryClaimAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, string operation, CancellationToken _) =>
            {
                operationType = operation;
                claims++;
                return claims == 1
                    ? new IdempotencyClaim(true, new IdempotencyRecord
                    {
                        Key = key,
                        OperationType = operation,
                        State = IdempotencyState.InProgress
                    })
                    : new IdempotencyClaim(false, new IdempotencyRecord
                    {
                        Key = key,
                        OperationType = operationType!,
                        State = IdempotencyState.Completed,
                        ResultPayload = payload
                    });
            });
        _idempotency.Setup(store => store.CompleteAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string key, string resultPayload, CancellationToken token) => payload = resultPayload)
            .Returns(Task.CompletedTask);
        _kyc.Setup(manager => manager.BeginAsync(avatarId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSessionStartModel>.Success(new KycSessionStartModel
            {
                ProviderKey = "manual",
                AcceptsDocumentReferences = true,
                ProviderSessionId = "provider-internal-id",
                ExpiresAt = DateTime.UtcNow.AddMinutes(30),
                Instructions = "Submit approved references."
            }));
        var manager = CreateManager();

        var first = await manager.BeginKycAsync(
            tenantId, "user-42", "stable-kyc-attempt-key-0001");
        var replay = await manager.BeginKycAsync(
            tenantId, "user-42", "stable-kyc-attempt-key-0001");

        first.IsError.Should().BeFalse();
        replay.IsError.Should().BeFalse();
        replay.Result.Should().BeEquivalentTo(first.Result);
        operationType.Should().StartWith("tenant_custodial_kyc_session_");
        operationType.Should().NotContain(":");
        JsonSerializer.Serialize(first.Result).Should().NotContain("provider-internal-id");
        _kyc.Verify(manager => manager.BeginAsync(avatarId, tenantId, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Ensure_CancelledConcurrentObserverCannotExecuteOrFailOwnersClaim()
    {
        var tenantId = Guid.NewGuid();
        var avatarId = Guid.NewGuid();
        var ledger = new Mock<IIdempotencyStore>();
        var record = new IdempotencyRecord();
        var recordSync = new object();
        var effectEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOwner = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var duplicateClaimed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observerCancellation = new CancellationTokenSource();
        var claimCount = 0;
        var provisionCount = 0;

        ledger.Setup(store => store.TryClaimAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, string operation, CancellationToken _) =>
            {
                var won = Interlocked.Increment(ref claimCount) == 1;
                lock (recordSync)
                {
                    if (won)
                    {
                        record.Key = key;
                        record.OperationType = operation;
                        record.State = IdempotencyState.InProgress;
                    }
                    else
                    {
                        duplicateClaimed.TrySetResult(true);
                        observerCancellation.Cancel();
                        releaseOwner.TrySetResult(true);
                    }

                    return new IdempotencyClaim(won, CloneIdempotencyRecord(record));
                }
            });
        ledger.Setup(store => store.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken _) =>
            {
                lock (recordSync)
                    return Task.FromResult<IdempotencyRecord?>(CloneIdempotencyRecord(record));
            });
        ledger.Setup(store => store.CompleteAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string payload, CancellationToken _) =>
            {
                lock (recordSync)
                {
                    record.ResultPayload = payload;
                    record.Error = null;
                    record.State = IdempotencyState.Completed;
                }
            })
            .Returns(Task.CompletedTask);
        ledger.Setup(store => store.FailAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string error, CancellationToken _) =>
            {
                lock (recordSync)
                {
                    record.Error = error;
                    record.State = IdempotencyState.Failed;
                }
            })
            .Returns(Task.CompletedTask);
        _tenants.Setup(manager => manager.ProvisionChildAsync(
                tenantId, It.IsAny<ProvisionChildModel>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, ProvisionChildModel request, CancellationToken operationToken) =>
            {
                Interlocked.Increment(ref provisionCount);
                effectEntered.TrySetResult(true);
                await releaseOwner.Task.WaitAsync(operationToken);
                return AZOAResult<ChildAvatarResponse>.Success(new ChildAvatarResponse
                {
                    AvatarId = avatarId,
                    ExternalUserId = request.ExternalUserId
                });
            });
        _wallets.Setup(manager => manager.BootstrapWalletAsync(
                It.IsAny<WalletGenerateRequest>(), avatarId, It.IsAny<AZOARequest?>()))
            .ReturnsAsync(AZOAResult<IWallet>.Success(new Wallet
            {
                Id = Guid.NewGuid(),
                AvatarId = avatarId,
                ChainType = "Algorand",
                Address = "ALGOADDRESS",
                WalletType = WalletType.Platform,
                EncryptedPrivateKey = "ciphertext"
            }));
        _kyc.Setup(manager => manager.GetStatusAsync(
                avatarId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmissionModel>.Failure(
                KycAuthorizationError.NotFound + "No submission."));
        var manager = CreateManager(idempotencyStore: ledger.Object);

        var owner = manager.EnsureAsync(
            tenantId, "user-42", "stable-idempotency-key-0001");
        await effectEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var observer = manager.EnsureAsync(
            tenantId,
            "user-42",
            "stable-idempotency-key-0001",
            observerCancellation.Token);
        await duplicateClaimed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observer);
        var ownerResult = await owner;

        ownerResult.IsError.Should().BeFalse();
        provisionCount.Should().Be(1);
        SnapshotIdempotencyRecord(recordSync, record).State
            .Should().Be(IdempotencyState.Completed);
        ledger.Verify(store => store.CompleteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        ledger.Verify(store => store.FailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BeginKyc_ConcurrentObserverReplaysOwnerWithoutStartingOrSettlingAgain()
    {
        var tenantId = Guid.NewGuid();
        var avatarId = Guid.NewGuid();
        var ledger = new Mock<IIdempotencyStore>();
        var record = new IdempotencyRecord();
        var recordSync = new object();
        var effectEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOwner = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var duplicateClaimed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var claimCount = 0;
        var beginCount = 0;

        ledger.Setup(store => store.TryClaimAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, string operation, CancellationToken _) =>
            {
                var won = Interlocked.Increment(ref claimCount) == 1;
                lock (recordSync)
                {
                    if (won)
                    {
                        record.Key = key;
                        record.OperationType = operation;
                        record.State = IdempotencyState.InProgress;
                    }
                    else
                    {
                        duplicateClaimed.TrySetResult(true);
                        releaseOwner.TrySetResult(true);
                    }

                    return new IdempotencyClaim(won, CloneIdempotencyRecord(record));
                }
            });
        ledger.Setup(store => store.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken _) =>
            {
                lock (recordSync)
                    return Task.FromResult<IdempotencyRecord?>(CloneIdempotencyRecord(record));
            });
        ledger.Setup(store => store.CompleteAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string payload, CancellationToken _) =>
            {
                lock (recordSync)
                {
                    record.ResultPayload = payload;
                    record.Error = null;
                    record.State = IdempotencyState.Completed;
                }
            })
            .Returns(Task.CompletedTask);
        _tenants.Setup(manager => manager.ResolveChildAsync(
                tenantId, "user-42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<ChildAvatarResponse>.Success(new ChildAvatarResponse
            {
                AvatarId = avatarId,
                ExternalUserId = "user-42"
            }));
        _kyc.Setup(manager => manager.BeginAsync(
                avatarId, tenantId, It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, Guid _, CancellationToken operationToken) =>
            {
                Interlocked.Increment(ref beginCount);
                effectEntered.TrySetResult(true);
                await releaseOwner.Task.WaitAsync(operationToken);
                return AZOAResult<KycSessionStartModel>.Success(new KycSessionStartModel
                {
                    ProviderKey = "manual",
                    AcceptsDocumentReferences = true,
                    DevelopmentSimulation = true
                });
            });
        var manager = CreateManager(idempotencyStore: ledger.Object);

        var owner = manager.BeginKycAsync(
            tenantId, "user-42", "stable-kyc-attempt-key-0001");
        await effectEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var observer = manager.BeginKycAsync(
            tenantId, "user-42", "stable-kyc-attempt-key-0001");
        await duplicateClaimed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseOwner.TrySetResult(true);

        var results = await Task.WhenAll(owner, observer);

        results.Should().OnlyContain(result => !result.IsError);
        results[1].Result.Should().BeEquivalentTo(results[0].Result);
        beginCount.Should().Be(1);
        SnapshotIdempotencyRecord(recordSync, record).State
            .Should().Be(IdempotencyState.Completed);
        ledger.Verify(store => store.CompleteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        ledger.Verify(store => store.FailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Ensure_RequestCancellationPropagatesOnlyAfterOwnedClaimIsSettled()
    {
        var tenantId = Guid.NewGuid();
        var avatarId = Guid.NewGuid();
        var cancellation = new CancellationTokenSource();
        var provisionCount = 0;
        IdempotencyRecord? record = null;
        var manager = CreateManager();
        _idempotency.Setup(store => store.TryClaimAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, string operation, CancellationToken _) =>
            {
                var won = record is null;
                record ??= new IdempotencyRecord
                {
                    Key = key,
                    OperationType = operation,
                    State = IdempotencyState.InProgress
                };
                return new IdempotencyClaim(won, record);
            });
        _idempotency.Setup(store => store.CompleteAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string payload, CancellationToken _) =>
            {
                record!.State = IdempotencyState.Completed;
                record.ResultPayload = payload;
            })
            .Returns(Task.CompletedTask);
        _tenants.Setup(manager => manager.ProvisionChildAsync(
                tenantId, It.IsAny<ProvisionChildModel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, ProvisionChildModel request, CancellationToken operationToken) =>
            {
                provisionCount++;
                if (provisionCount == 1)
                    cancellation.Cancel();
                operationToken.CanBeCanceled.Should().BeFalse(
                    because: "the claim owner must finish and settle independently of request cancellation");

                return AZOAResult<ChildAvatarResponse>.Success(new ChildAvatarResponse
                {
                    AvatarId = avatarId,
                    ExternalUserId = request.ExternalUserId
                });
            });
        _wallets.Setup(manager => manager.BootstrapWalletAsync(
                It.IsAny<WalletGenerateRequest>(), avatarId, It.IsAny<AZOARequest?>()))
            .ReturnsAsync(AZOAResult<IWallet>.Success(new Wallet
            {
                Id = Guid.NewGuid(),
                AvatarId = avatarId,
                ChainType = "Algorand",
                Address = "ALGOADDRESS",
                WalletType = WalletType.Platform,
                EncryptedPrivateKey = "ciphertext"
            }));
        _kyc.Setup(manager => manager.GetStatusAsync(
                avatarId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmissionModel>.Failure(
                KycAuthorizationError.NotFound + "No submission."));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.EnsureAsync(
            tenantId,
            "user-42",
            "stable-idempotency-key-0001",
            cancellation.Token));
        var retry = await manager.EnsureAsync(
            tenantId, "user-42", "stable-idempotency-key-0001");

        retry.IsError.Should().BeFalse();
        record!.OperationType.Should().StartWith("tenant_custodial_ensure_");
        record.State.Should().Be(IdempotencyState.Completed);
        _idempotency.Verify(store => store.FailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _idempotency.Verify(store => store.CompleteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BeginKyc_RequestCancellationPropagatesOnlyAfterOwnedClaimIsSettled()
    {
        var tenantId = Guid.NewGuid();
        var avatarId = Guid.NewGuid();
        var cancellation = new CancellationTokenSource();
        var beginCount = 0;
        IdempotencyRecord? record = null;
        var manager = CreateManager();
        _tenants.Setup(manager => manager.ResolveChildAsync(
                tenantId, "user-42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<ChildAvatarResponse>.Success(new ChildAvatarResponse
            {
                AvatarId = avatarId,
                ExternalUserId = "user-42"
            }));
        _idempotency.Setup(store => store.TryClaimAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, string operation, CancellationToken _) =>
            {
                var won = record is null;
                record ??= new IdempotencyRecord
                {
                    Key = key,
                    OperationType = operation,
                    State = IdempotencyState.InProgress
                };
                return new IdempotencyClaim(won, record);
            });
        _idempotency.Setup(store => store.CompleteAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string payload, CancellationToken _) =>
            {
                record!.State = IdempotencyState.Completed;
                record.ResultPayload = payload;
            })
            .Returns(Task.CompletedTask);
        _kyc.Setup(kyc => kyc.BeginAsync(
                avatarId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid _, CancellationToken operationToken) =>
            {
                beginCount++;
                if (beginCount == 1)
                    cancellation.Cancel();
                if (beginCount == 1)
                    operationToken.CanBeCanceled.Should().BeFalse(
                        because: "the claim owner must finish and settle independently of request cancellation");

                return AZOAResult<KycSessionStartModel>.Success(new KycSessionStartModel
                {
                    ProviderKey = "manual",
                    AcceptsDocumentReferences = true,
                    DevelopmentSimulation = true
                });
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.BeginKycAsync(
            tenantId,
            "user-42",
            "stable-kyc-attempt-key-0001",
            cancellation.Token));
        var retry = await manager.BeginKycAsync(
            tenantId, "user-42", "stable-kyc-attempt-key-0001");

        retry.IsError.Should().BeFalse();
        retry.Result!.DevelopmentSimulation.Should().BeTrue();
        record!.State.Should().Be(IdempotencyState.Completed);
        _idempotency.Verify(store => store.FailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _idempotency.Verify(store => store.CompleteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Capabilities_LiveModeNeverTreatsDevelopmentKeyAsProductionCustody()
    {
        var manager = CreateManager(blockchainMode: "Live");

        var capabilities = manager.GetCapabilities();

        capabilities.CustodyAvailable.Should().BeFalse();
        capabilities.IdentityReady.Should().BeTrue();
        capabilities.KycReady.Should().BeTrue();
        capabilities.WalletProvisioningReady.Should().BeFalse();
        capabilities.Ready.Should().BeFalse();
        capabilities.UnavailableReason.Should().Contain("Development");
    }

    private static IdempotencyRecord SnapshotIdempotencyRecord(
        object sync,
        IdempotencyRecord record)
    {
        lock (sync)
            return CloneIdempotencyRecord(record);
    }

    private static IdempotencyRecord CloneIdempotencyRecord(IdempotencyRecord record)
        => new()
        {
            Key = record.Key,
            OperationType = record.OperationType,
            State = record.State,
            ResultPayload = record.ResultPayload,
            Error = record.Error,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt
        };

    private TenantCustodialAccountManager CreateManager(
        string walletEncryptionKey = "a-development-custody-secret-that-is-long-enough",
        string blockchainMode = "Simulated",
        IIdempotencyStore? idempotencyStore = null)
    {
        var kycCapabilities = new KycProviderCapabilitiesModel
        {
            Provider = KycProvider.MANUAL,
            ProviderKey = "manual",
            Available = true,
            AcceptsDocumentReferences = true,
            DevelopmentSimulation = true
        };
        _kyc.Setup(manager => manager.GetCapabilities()).Returns(kycCapabilities);
        _kyc.Setup(manager => manager.GetCapabilitiesAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycProviderCapabilitiesModel>.Success(kycCapabilities));
        _providers.Setup(factory => factory.GetProvider("Algorand", ChainNetwork.Devnet))
            .Returns(Mock.Of<IBlockchainProvider>());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZOA:WalletEncryptionKey"] = walletEncryptionKey,
                ["Blockchain:DefaultChain"] = "Algorand",
                ["Blockchain:DefaultNetwork"] = "Devnet",
                ["Blockchain:Mode"] = blockchainMode,
                ["CustodialAccounts:Enabled"] = "true",
                ["CustodialAccounts:WalletChain"] = "Algorand",
                ["CustodialAccounts:CustodyMode"] = "DevelopmentOnly"
            })
            .Build();

        return new TenantCustodialAccountManager(
            _tenants.Object,
            _wallets.Object,
            _kyc.Object,
            _providers.Object,
            idempotencyStore ?? _idempotency.Object,
            configuration,
            Mock.Of<IHostEnvironment>(environment => environment.EnvironmentName == "Development"));
    }
}
