// SPDX-License-Identifier: UNLICENSED

using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Users;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Credentials;

/// <summary>One credential as the production mint seam produced it.</summary>
/// <param name="SeamExists">False when no D41 recipient seam exists to call.</param>
/// <param name="Outcome">The mint result, when a seam was found.</param>
/// <param name="Record">The credential as the revoke path will read it back.</param>
internal sealed record IssuedCredential(
    bool SeamExists, AZOAResult<IUserNFT>? Outcome, CredentialOwnershipRecord? Record);

/// <summary>
/// Both halves of the credential rail wired to each other: the real
/// <see cref="UserNFTService"/> mint seam feeding the real <see cref="CredentialManager"/>
/// revoke seam through an in-memory ownership store.
/// </summary>
/// <remarks>
/// The point of the harness is that the revoke arrangement is NOT hand-written. Every
/// existing D19 test stubs a two-party credential into being; here the credential is
/// whatever the mint path actually wrote, projected the way the store projects it. That
/// is the only way an "issuer may revoke, holder may not" test can be non-vacuous.
/// See <c>tests/AZOA.WebAPI.Tests/Credentials/AGENTS.md</c>.
/// </remarks>
internal sealed class CredentialRailHarness
{
    internal const string Chain = "Algorand";

    internal Guid IssuerA { get; } = Guid.NewGuid();
    internal Guid RecipientB { get; } = Guid.NewGuid();

    internal string IssuerAddress { get; private set; } = "ISSUERALGOADDRESS";
    internal string RecipientAddress { get; private set; } = "RECIPIENTALGOADDRESS";

    internal Mock<INftStore> Nfts { get; } = new();
    internal Mock<IWalletStore> Wallets { get; } = new();
    internal Mock<IBlockchainOperationStore> Operations { get; } = new();
    internal Mock<IValueAccessService> ValueAccess { get; } = new();
    internal Mock<IBlockchainProviderFactory> Factory { get; } = new();
    internal Mock<IBlockchainProvider> Provider { get; } = new();
    internal Mock<IAlgorandASAModule> Asa { get; } = new();
    internal FakeIdempotencyStore Idempotency { get; } = new();
    internal InMemoryCredentialStore Credentials { get; } = new();

    /// <summary>Every ownership row the mint seam wrote, in order.</summary>
    internal List<IUserNFT> MintedRows { get; } = new();

    internal CredentialRailHarness()
    {
        Nfts.Setup(s => s.UpsertUserNFTAsync(It.IsAny<IUserNFT>(), It.IsAny<CancellationToken>()))
            .Callback<IUserNFT, CancellationToken>((row, _) => MintedRows.Add(row))
            .ReturnsAsync((IUserNFT row, CancellationToken _) => new AZOAResult<IUserNFT> { Result = row });

        Operations.Setup(o => o.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IBlockchainOperation op, CancellationToken _) =>
                new AZOAResult<IBlockchainOperation> { Result = op });

        ValueAccess.Setup(v => v.RequireValueAccessAsync(
                It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { Result = true });

        Factory.Setup(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
               .Returns(() => ResolvedProvider);
        Factory.Setup(f => f.GetDefaultProvider()).Returns(() => ResolvedProvider);
        Factory.SetupGet(f => f.DefaultNetwork).Returns(ChainNetwork.Devnet);

        var module = Asa.Object;
        Provider.Setup(p => p.TryGetModule<IAlgorandASAModule>(out It.Ref<IAlgorandASAModule?>.IsAny))
                .Returns((out IAlgorandASAModule? m) => { m = module; return true; });
        ResolvedProvider = Provider.Object;

        ClawbackConfirms("CLAWBACKTX");
        HasWalletOnChain(IssuerA, IssuerAddress);
        HasWalletOnChain(RecipientB, RecipientAddress);
    }

    /// <summary>The provider the factory hands back — the mock unless a real one is installed.</summary>
    private IBlockchainProvider ResolvedProvider { get; set; } = null!;

    /// <summary>Drives the revoke path through a REAL provider (an Algod-stub-backed one, say).</summary>
    internal void UseProvider(IBlockchainProvider provider) => ResolvedProvider = provider;

    /// <summary>Gives a user a wallet on <see cref="Chain"/> at a chosen address.</summary>
    internal void HasWalletOnChain(Guid userId, string address)
    {
        if (userId == IssuerA) IssuerAddress = address;
        if (userId == RecipientB) RecipientAddress = address;

        Wallets.Setup(w => w.GetByUserAsync(userId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>>
               {
                   Result = new[]
                   {
                       Mock.Of<IWallet>(x => x.Id == Guid.NewGuid() && x.UserId == userId
                                             && x.ChainType == Chain && x.Address == address),
                   },
               });
    }

    /// <summary>Removes a user's wallets on every chain — the fail-closed arrangement.</summary>
    internal void HasNoWallet(Guid userId) =>
        Wallets.Setup(w => w.GetByUserAsync(userId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = Array.Empty<IWallet>() });

    internal void ClawbackConfirms(string txId) =>
        Asa.Setup(a => a.ClawbackAssetAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<RailAmount>(),
                It.IsAny<SigningContext>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new AZOAResult<string> { Result = txId, Message = "confirmed in round 42" });

    internal void VerifyNoClawback() =>
        Asa.Verify(a => a.ClawbackAssetAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<RailAmount>(),
                It.IsAny<SigningContext>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

    internal IUserNFTService MintService =>
        new UserNFTService(Nfts.Object, Mock.Of<IHolonStore>(), Wallets.Object);

    internal CredentialManager Manager => new(
        Credentials, Wallets.Object, Factory.Object, Operations.Object, Idempotency, ValueAccess.Object);

    /// <summary>
    /// Issues one soulbound credential from <see cref="IssuerA"/> to <see cref="RecipientB"/>
    /// through the production mint seam and registers what it wrote with the revoke path's store.
    /// </summary>
    internal async Task<IssuedCredential> IssueAsync(string tokenId)
    {
        var before = MintedRows.Count;
        var attempt = await CredentialTrackSeams.IssueSoulboundAsync(
            MintService, IssuerA, RecipientB, SoulboundModelFor(tokenId));

        if (!attempt.SeamExists)
            return new IssuedCredential(false, null, null);

        var written = MintedRows.Skip(before).LastOrDefault();
        if (written is null)
            return new IssuedCredential(true, attempt.Outcome, null);

        var record = CredentialTrackSeams.ProjectAsTheStoreWould(written);
        Credentials.Seed(record);
        return new IssuedCredential(true, attempt.Outcome, record);
    }

    internal static UserNFTMintModel SoulboundModelFor(string tokenId) => new()
    {
        ChainType = Chain,
        TokenId = tokenId,
        NFTContractAddress = "CREDENTIALS",
        TokenStandard = "ASA",
        MetadataURI = "ipfs://credential",
        Name = "Membership tier",
        IsSoulbound = true,
        IsTransferable = false,
    };

    internal Task<AZOAResult<CredentialRevokeResult>> RevokeAsync(
        string assetId, Guid callerUserId, string? idempotencyKey = null) =>
        Manager.RevokeAsync(
            assetId, new CredentialRevokeRequest { ChainType = Chain }, callerUserId, idempotencyKey);
}

/// <summary>
/// A transparent in-memory <see cref="ICredentialIssuerStore"/>: it records what it is given
/// and re-points ownership on a confirmed revocation, exactly as
/// <c>SurrealNftStore.RecordCredentialRevokedAsync</c> does (same live row, issuer untouched).
/// It invents nothing — every credential in it came from the mint seam under test.
/// </summary>
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
