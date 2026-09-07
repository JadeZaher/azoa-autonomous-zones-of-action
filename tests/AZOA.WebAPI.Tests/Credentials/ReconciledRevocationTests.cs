// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Idempotency;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Reconciliation;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Credentials;

/// <summary>
/// AC5 + AC6 on the path nobody wired: a revocation that confirms through RECONCILIATION
/// rather than in-line.
///
/// <para><c>ReconciliationService</c> contains no <c>CredentialRevoke</c> case at all. On a
/// chain-confirmed probe it flips the operation row to <c>Completed</c> and settles the
/// owning idempotency claim with the cached <c>CredentialRevokeResult</c> — while
/// <c>nft_ownership</c> still names the holder. The client replays a <c>Completed</c>
/// revocation and every AZOA read surface still reports the credential as held by the party
/// it was taken from. That is a fabricated success reported by the reconcile path, which is
/// the exact defect class this codebase has shipped three times.</para>
///
/// <para>The service is built through <see cref="ActivatorUtilities"/> rather than
/// <c>new</c>, so the test compiles against today's constructor and picks up the ownership
/// consumer the moment Phase 3 injects one.</para>
/// </summary>
public sealed class ReconciledRevocationTests
{
    private const string Chain = "Algorand";
    private const string AssetId = "778899";
    private const string TxHash = "RECONCILEDCLAWBACKTX";
    private const string IssuerAddress = "ISSUERALGOADDRESS";
    private const string IdempotencyKey = "credential-revoke:reconciled";

    private readonly Guid _issuerA = Guid.NewGuid();
    private readonly Guid _holderB = Guid.NewGuid();
    private readonly Guid _credentialId = Guid.NewGuid();

    private readonly FakeBridgeStore _operations = new();
    private readonly FakeIdempotencyStore _idempotency = new();
    private readonly Mock<ICredentialIssuerStore> _credentials = new();
    private readonly Mock<IBlockchainProviderFactory> _factory = new();
    private readonly Mock<IBlockchainProvider> _provider = new();

    public ReconciledRevocationTests()
    {
        _provider.Setup(p => p.ChainType).Returns(Chain);
        _factory.Setup(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>())).Returns(_provider.Object);
        _factory.Setup(f => f.GetDefaultProvider()).Returns(_provider.Object);

        _credentials.Setup(c => c.RecordCredentialRevokedAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { Result = true });
    }

    /// <summary>
    /// AC5, reconciliation path. The clawback confirmed on chain, so AZOA's ownership record
    /// must follow chain truth — with the credential and destination read from the operation
    /// row, never guessed.
    /// </summary>
    [Fact]
    public async Task ConfirmedByReconciliation_RepointsOwnershipToTheIssuer()
    {
        var operationId = SeedRevokeOperation(ageMinutes: 30, includeCredentialId: true);
        ChainReports(TxHash, confirmed: true);
        _idempotency.Seed(IdempotencyKey, CredentialManager.OperationTypeName, IdempotencyState.InProgress);

        var report = await CreateService().ReconcileOperationsAsync(CancellationToken.None);

        report.Advanced.Should().Be(1);
        (await _operations.GetOperationAsync(operationId))!.Status.Should().Be(OperationStatus.Completed);

        _credentials.Verify(c => c.RecordCredentialRevokedAsync(
                _credentialId, _issuerA, IssuerAddress, It.IsAny<CancellationToken>()), Times.Once,
            "AC5: a revocation confirmed by reconciliation must re-point nft_ownership exactly as "
            + "the in-line path does. Without it the operation reads Completed, the idempotency "
            + "claim replays a Completed result, and every AZOA read surface still reports the "
            + "credential as held by the party it was clawed back from");

        (await _idempotency.GetAsync(IdempotencyKey, CancellationToken.None))!
            .State.Should().Be(IdempotencyState.Completed,
                "a stranded claim must settle from chain truth, or the credential becomes "
                + "permanently un-revocable by its own idempotency record");
    }

    /// <summary>
    /// AC6. Reconciliation cannot report a revocation Completed when it cannot say WHICH
    /// credential moved: the ownership consumer has nothing to act on, so marking the row
    /// Completed manufactures a success the ledger can never be reconciled against. An
    /// operation in that shape belongs to an operator, not to an automatic advance.
    /// </summary>
    [Fact]
    public async Task ConfirmedButUnidentifiableCredential_IsFlaggedForOperators_NotCompleted()
    {
        var operationId = SeedRevokeOperation(ageMinutes: 60, includeCredentialId: false);
        ChainReports(TxHash, confirmed: true);
        _idempotency.Seed(IdempotencyKey, CredentialManager.OperationTypeName, IdempotencyState.InProgress);

        var report = await CreateService().ReconcileOperationsAsync(CancellationToken.None);

        report.Advanced.Should().Be(0,
            "a CredentialRevoke whose credential cannot be identified must not advance");
        report.StuckFlagged.Should().Be(1, "it is an operator problem, and must be visible as one");

        (await _operations.GetOperationAsync(operationId))!.Status.Should().Be(
            OperationStatus.PendingConfirmation, "the row is left exactly as it was found");

        _credentials.Verify(c => c.RecordCredentialRevokedAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        (await _idempotency.GetAsync(IdempotencyKey, CancellationToken.None))!
            .State.Should().Be(IdempotencyState.InProgress,
                "settling the claim Completed would replay a revocation AZOA never recorded");
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private ReconciliationService CreateService()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IBridgeStore>(_operations);
        services.AddSingleton<IIdempotencyStore>(_idempotency);
        services.AddSingleton(_factory.Object);
        services.AddSingleton(_credentials.Object);
        services.AddSingleton<ILogger<ReconciliationService>>(NullLogger<ReconciliationService>.Instance);
        services.AddSingleton<IOptions<ReconciliationOptions>>(Options.Create(new ReconciliationOptions
        {
            Enabled = true,
            BatchSize = 100,
            BridgeStaleAfterSeconds = 60,
            BridgeHardStuckAfterSeconds = 900,
            OperationStaleAfterSeconds = 60,
            OperationHardStuckAfterSeconds = 900,
        }));

        // Constructor-agnostic on purpose: the ownership consumer Phase 3 adds is picked up
        // without editing this test, and its absence today is what makes the test red.
        return ActivatorUtilities.CreateInstance<ReconciliationService>(services.BuildServiceProvider());
    }

    private void ChainReports(string txHash, bool confirmed) =>
        _provider.Setup(p => p.GetTransactionStatusAsync(txHash, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<Dictionary<string, object>>
                 {
                     Result = new Dictionary<string, object>
                     {
                         ["confirmed"] = confirmed,
                         ["confirmedRound"] = 42L,
                     },
                 });

    /// <summary>
    /// A <c>CredentialRevoke</c> row exactly as <c>CredentialManager</c> writes it when a
    /// clawback is broadcast but does not confirm in-line.
    /// </summary>
    private Guid SeedRevokeOperation(int ageMinutes, bool includeCredentialId)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TxHash"] = TxHash,
            ["ChainType"] = Chain,
            ["AssetId"] = AssetId,
            ["TokenId"] = AssetId,
            ["HolderUserId"] = _holderB.ToString(),
            ["HolderAddress"] = "HOLDERALGOADDRESS",
            ["RecipientAddress"] = IssuerAddress,
            ["IdempotencyKey"] = IdempotencyKey,
            [IdempotencyParameterNames.ResultPayload] = "{\"AssetId\":\"" + AssetId + "\"}",
        };

        if (includeCredentialId)
            parameters["CredentialId"] = _credentialId.ToString();

        var operation = new BlockchainOperation
        {
            Id = Guid.NewGuid(),
            UserId = _issuerA,
            InitiatorUserId = _issuerA,
            OperationType = CredentialManager.OperationTypeName,
            Status = OperationStatus.PendingConfirmation,
            Amount = RailAmount.OfChainAsset(1),
            AssetType = "Credential",
            RecipientAddress = IssuerAddress,
            CreatedDate = DateTime.UtcNow.AddMinutes(-ageMinutes),
            Parameters = parameters,
        };

        _operations.SeedOperation(operation);
        return operation.Id;
    }
}
