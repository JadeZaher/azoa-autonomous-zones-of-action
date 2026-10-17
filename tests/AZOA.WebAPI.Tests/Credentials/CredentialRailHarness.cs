using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Tests.TestSupport;
using Moq;

namespace AZOA.WebAPI.Tests.Credentials;

internal sealed record IssuedCredential(bool SeamExists, AZOAResult<IUserNFT>? Outcome, CredentialOwnershipRecord? Record);

/// <summary>Runs real credential lifecycle orchestration against a controlled chain-evidence module.</summary>
internal sealed class CredentialRailHarness
{
    internal const string Chain = "Algorand";
    internal NftLifecycleHarness Rail { get; } = new();
    internal Guid IssuerA { get; } = Guid.NewGuid();
    internal Guid RecipientB { get; } = Guid.NewGuid();
    internal string IssuerAddress { get; private set; }
    internal string RecipientAddress { get; private set; }
    internal List<IUserNFT> MintedRows { get; } = new();
    private readonly Dictionary<Guid, IWallet> _wallets = new();
    private readonly Dictionary<string, string> _assets = new();
    private string _nextAsset = string.Empty;

    internal CredentialRailHarness()
    {
        IssuerAddress = IssuerA.ToString("N"); RecipientAddress = RecipientB.ToString("N");
        HasWalletOnChain(IssuerA, IssuerAddress); HasWalletOnChain(RecipientB, RecipientAddress);
        Rail.Module.Setup(module => module.ObserveNftTransactionAsync(It.IsAny<PreparedNftTransaction>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PreparedNftTransaction transaction, CancellationToken _) =>
            {
                var submitted = Rail.Submitted.Any(item => item.TransactionId == transaction.TransactionId);
                if (transaction.Intent.Kind == NftTransactionKinds.Create && !_assets.ContainsKey(transaction.TransactionId)) _assets[transaction.TransactionId] = _nextAsset;
                return AZOAResult<NftTransactionObservation>.Success(new(Rail.ObservationOverride ?? (Rail.Confirm && submitted ? "Confirmed" : "Unseen"),
                    transaction.TransactionId, transaction.Intent.AssetId ?? _assets.GetValueOrDefault(transaction.TransactionId),
                    transaction.Intent.RecipientAddress, "genesis", "ASA"));
            });
    }

    internal void HasWalletOnChain(Guid user, string address)
    {
        if (!_wallets.TryGetValue(user, out var wallet)) _wallets[user] = wallet = Rail.AddWallet(user);
        Mock.Get(wallet).SetupGet(item => item.Address).Returns(address);
        if (user == IssuerA) IssuerAddress = address;
        if (user == RecipientB) RecipientAddress = address;
    }
    internal void HasNoWallet(Guid user) => Rail.Wallets.Setup(store => store.GetByUserAsync(user, It.IsAny<CancellationToken>()))
        .ReturnsAsync(AZOAResult<IEnumerable<IWallet>>.Success(Array.Empty<IWallet>()));
    internal void VerifyNoClawback() => Rail.Module.Verify(module => module.PrepareNftTransactionAsync(
        It.Is<NftTransactionIntent>(intent => intent.Kind == NftTransactionKinds.Revoke), It.IsAny<CancellationToken>()), Times.Never);
    internal void UseProvider(IBlockchainProvider provider) => Rail.Factory.Setup(factory => factory.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>())).Returns(provider);
    internal CredentialManager Manager => new(Rail.Store, Rail.Service, Rail.Factory.Object);

    internal async Task<IssuedCredential> IssueAsync(string assetId, Guid? recipient = null)
    {
        _nextAsset = assetId;
        var holder = recipient ?? RecipientB;
        var result = await Rail.Service.MintAsync(IssuerA, holder, SoulboundModelFor(assetId), "issue:" + assetId, credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(IssuerA));
        for (var step = 0; !result.IsError && result.Result is { State: not "Completed" } view && step < 8; step++)
            result = await Rail.Service.RefreshAsync(view.State == "AwaitingRecipientAcceptance" ? holder : IssuerA,
                view.OperationId, view.State == "AwaitingRecipientAcceptance",
                credential: NftLifecycleHarness.Credential(view.State == "AwaitingRecipientAcceptance" ? holder : IssuerA),
                acceptance: view.State == "AwaitingRecipientAcceptance" ? await Rail.Acceptance(view.OperationId) : null);
        if (result.IsError || result.Result is not { State: "Completed" })
            return new(true, AZOAResult<IUserNFT>.Failure(result.Message), null);
        var row = await Rail.Nfts.Object.GetUserNFTByIdAsync(result.Result.NftId);
        if (row.Result is not null) MintedRows.Add(row.Result);
        var record = await Rail.Store.FindCredentialAsync(Chain, ChainNetwork.Devnet.ToString(), assetId, null);
        return new(true, row, record);
    }

    internal static UserNFTMintModel SoulboundModelFor(string fixtureAssetId) => new()
    {
        ChainType = Chain, MetadataURI = "ipfs://credential/" + fixtureAssetId,
        Name = "Membership tier", IsSoulbound = true, IsTransferable = false,
    };

    internal Task<AZOAResult<CredentialRevokeResult>> RevokeAsync(string assetId, Guid callerUserId, string? idempotencyKey = null) =>
        Manager.RevokeAsync(assetId, new() { ChainType = Chain, AuthenticationWitness = AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(callerUserId) }, callerUserId, idempotencyKey);
}
internal sealed class InMemoryCredentialStore : ICredentialIssuerStore
{
    private readonly Dictionary<string, CredentialOwnershipRecord> _byTokenId = new(StringComparer.Ordinal);

    /// <summary>Number of confirmed-revocation ownership moves recorded.</summary>
    internal int OwnershipMoves { get; private set; }

    internal void Seed(CredentialOwnershipRecord record) => _byTokenId[record.TokenId] = record;

    /// <summary>The user every AZOA read surface would report as holding the credential.</summary>
    internal Guid? CurrentHolderOf(string tokenId) =>
        _byTokenId.TryGetValue(tokenId, out var row) ? row.HolderUserId : null;

    internal string? CurrentHolderAddressOf(string tokenId) =>
        _byTokenId.TryGetValue(tokenId, out var row) ? row.HolderAddress : null;

    public Task<AZOAResult<CredentialOwnershipRecord?>> FindCurrentByAssetIdAsync(
        string chainType, string assetId, CancellationToken ct = default) =>
        Task.FromResult(new AZOAResult<CredentialOwnershipRecord?>
        {
            Result = _byTokenId.TryGetValue(assetId, out var row)
                     && string.Equals(row.ChainType, chainType, StringComparison.OrdinalIgnoreCase)
                ? row
                : null,
        });

    public Task<AZOAResult<bool>> SetIssuerUserIdAsync(
        Guid credentialId, Guid issuerUserId, CancellationToken ct = default)
    {
        var row = _byTokenId.Values.FirstOrDefault(r => r.Id == credentialId);
        if (row is null)
            return Task.FromResult(new AZOAResult<bool> { IsError = true, Message = "No such credential." });

        // Write-once, as the SQL guard is: a different issuer is refused, the same one is idempotent.
        if (row.IssuerUserId is { } existing && existing != issuerUserId)
            return Task.FromResult(new AZOAResult<bool>
            {
                IsError = true,
                Message = $"Credential {credentialId} already records a different issuer.",
            });

        row.IssuerUserId = issuerUserId;
        return Task.FromResult(new AZOAResult<bool> { Result = true });
    }

    public Task<AZOAResult<bool>> RecordCredentialRevokedAsync(
        Guid credentialId, Guid issuerUserId, string issuerAddress, CancellationToken ct = default)
    {
        var row = _byTokenId.Values.FirstOrDefault(r => r.Id == credentialId);
        if (row is null)
            return Task.FromResult(new AZOAResult<bool> { IsError = true, Message = "No such credential." });

        row.HolderUserId = issuerUserId;
        row.HolderAddress = issuerAddress;
        OwnershipMoves++;
        return Task.FromResult(new AZOAResult<bool> { Result = true });
    }
}
