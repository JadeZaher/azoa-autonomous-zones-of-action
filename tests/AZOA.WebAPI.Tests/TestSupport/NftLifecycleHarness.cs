using System.Text.Json;
using System.Collections.Concurrent;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Governance;
using AZOA.WebAPI.Services.Users;
using Microsoft.Extensions.Configuration;
using Moq;

namespace AZOA.WebAPI.Tests.TestSupport;

internal sealed class NftLifecycleHarness
{
    internal InMemoryNftLifecycleStore Store { get; } = new();
    internal Mock<IWalletStore> Wallets { get; } = new();
    internal Mock<INftStore> Nfts { get; } = new();
    internal Mock<INftProtocolCostQuoteStore> ProtocolCosts { get; } = new();
    internal Mock<INftWorkflowCostAcceptanceStore> WorkflowCosts { get; } = new();
    internal Mock<IActionNodeExecutionStore> ActionExecutions { get; } = new();
    internal Mock<INftProtocolCostModule> CostModule { get; } = new();
    internal Mock<INftTransactionModule> Module { get; } = new();
    internal Mock<IBlockchainProviderFactory> Factory { get; } = new();
    internal Mock<IValueAccessService> Access { get; } = new();
    internal ConcurrentQueue<PreparedNftTransaction> Submitted { get; } = new();
    internal ConcurrentQueue<NftTransactionIntent> Prepared { get; } = new();
    internal bool Confirm { get; set; } = true;
    internal bool LoseSubmitResponse { get; set; }
    internal string? ObservationOverride { get; set; }
    internal Func<NftTransactionIntent, Task>? BeforePrepare { get; set; }
    internal IConfigurationRoot Configuration { get; }
    internal NftLifecycleService Service { get; }
    internal string ChainType { get; }
    /// <summary>Shared service and store clock; advance it to lapse direct credentials without sleeping.</summary>
    internal ShiftedClock Clock { get; } = new();
    private readonly List<IWallet> _wallets = new();

    internal NftLifecycleHarness(string chainType = "Algorand")
    {
        ChainType = chainType;
        Store.Now = Clock.GetUtcNow;
        Configuration = new ConfigurationBuilder().AddJsonFile("appsettings.json").AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NftFees:Policies:0:ChainType"] = "Algorand", ["NftFees:Policies:0:Network"] = "Devnet",
            ["NftFees:Policies:0:Revision"] = "1", ["NftFees:Policies:0:TreasuryAddress"] = "treasury",
            ["NftFees:Policies:0:MintNativeBaseUnits"] = "0", ["NftFees:Policies:0:TransferNativeBaseUnits"] = "0",
            ["NftFees:Policies:0:BurnNativeBaseUnits"] = "0", ["NftFees:Policies:0:RevokeNativeBaseUnits"] = "0",
            ["NftFees:Policies:0:RefundNativeBaseUnits"] = "0",
            ["NftFees:Policies:1:ChainType"] = "Solana", ["NftFees:Policies:1:Network"] = "Devnet",
            ["NftFees:Policies:1:Revision"] = "1", ["NftFees:Policies:1:TreasuryAddress"] = "treasury",
            ["NftFees:Policies:1:MintNativeBaseUnits"] = "0", ["NftFees:Policies:1:TransferNativeBaseUnits"] = "0",
            ["NftFees:Policies:1:BurnNativeBaseUnits"] = "0", ["NftFees:Policies:1:RevokeNativeBaseUnits"] = "0",
            ["NftFees:Policies:1:RefundNativeBaseUnits"] = "0",
        }).Build();
        var provider = new Mock<IBlockchainProvider>();
        provider.SetupGet(item => item.ChainType).Returns(chainType);
        provider.SetupGet(item => item.ActiveNetwork).Returns(ChainNetwork.Devnet);
        provider.Setup(item => item.TryGetModule<INftTransactionModule>(out It.Ref<INftTransactionModule?>.IsAny))
            .Returns((out INftTransactionModule? module) => { module = Module.Object; return true; });
        provider.Setup(item => item.TryGetModule<INftProtocolCostModule>(out It.Ref<INftProtocolCostModule?>.IsAny))
            .Returns((out INftProtocolCostModule? module) => { module = CostModule.Object; return true; });
        Factory.SetupGet(item => item.DefaultNetwork).Returns(ChainNetwork.Devnet);
        Factory.Setup(item => item.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>())).Returns(provider.Object);
        Module.SetupGet(item => item.NativeCurrencyCode).Returns("ALGO");
        Module.SetupGet(item => item.NativeCurrencyDecimals).Returns(6);
        Module.SetupGet(item => item.NftTokenStandard).Returns("ASA");
        Module.SetupGet(item => item.CredentialAuthorityAddress).Returns("platform");
        Module.Setup(item => item.PrepareNftTransactionAsync(It.IsAny<NftTransactionIntent>(), It.IsAny<CancellationToken>()))
            .Returns(async (NftTransactionIntent intent, CancellationToken _) =>
            {
                if (BeforePrepare is { } pause) await pause(intent);
                Prepared.Enqueue(intent);
                var txid = NftLifecycleIdentity.Fingerprint(JsonSerializer.Serialize(intent));
                return AZOAResult<PreparedNftTransaction>.Success(new(txid, Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(txid)), intent, "genesis", "ASA"));
            });
        Module.Setup(item => item.SubmitNftTransactionAsync(It.IsAny<PreparedNftTransaction>(), It.IsAny<CancellationToken>()))
            .Returns((PreparedNftTransaction transaction, CancellationToken _) =>
            {
                Submitted.Enqueue(transaction);
                return LoseSubmitResponse ? Task.FromException(new HttpRequestException("Response lost")) : Task.CompletedTask;
            });
        Module.Setup(item => item.ObserveNftTransactionAsync(It.IsAny<PreparedNftTransaction>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PreparedNftTransaction transaction, CancellationToken _) =>
            {
                var status = ObservationOverride ?? (Confirm && Submitted.Any(item => item.TransactionId == transaction.TransactionId) ? "Confirmed" : "Unseen");
                return AZOAResult<NftTransactionObservation>.Success(new(status, transaction.TransactionId,
                    transaction.Intent.AssetId ?? transaction.TransactionId[..12], transaction.Intent.RecipientAddress, "genesis", "ASA"));
            });
        Module.Setup(item => item.ReadNftOwnershipAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync((string _, string owner, string _, CancellationToken _, string _) => AZOAResult<NftOwnershipObservation>.Success(new(true, owner, false)));
        Wallets.Setup(item => item.GetByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid user, CancellationToken _) => AZOAResult<IEnumerable<IWallet>>.Success(_wallets.Where(item => item.UserId == user).ToArray()));
        Wallets.Setup(item => item.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => AZOAResult<IEnumerable<IWallet>>.Success(_wallets.ToArray()));
        Wallets.Setup(item => item.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => _wallets.FirstOrDefault(item => item.Id == id) is { } wallet
                ? AZOAResult<IWallet>.Success(wallet) : AZOAResult<IWallet>.Failure("Wallet not found."));
        Nfts.Setup(item => item.GetUserNFTByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid id, CancellationToken _) =>
        {
            if (!Store.Ownership.TryGetValue(id.ToString("N"), out var row)) return AZOAResult<IUserNFT>.Failure("Not found");
            var nft = new Mock<IUserNFT>();
            nft.SetupGet(item => item.Id).Returns(id);
            nft.SetupGet(item => item.UserId).Returns(Guid.Parse(SurrealForge.Client.SurrealLink.FromLink(row.UserId)!)); // mirrors SurrealNftStore's link read
            nft.SetupGet(item => item.ChainType).Returns(row.ChainType);
            nft.SetupGet(item => item.TokenId).Returns(row.TokenId);
            nft.SetupGet(item => item.CurrentOwner).Returns(row.CurrentOwner);
            nft.SetupGet(item => item.NFTContractAddress).Returns(row.ContractAddress);
            nft.SetupGet(item => item.TokenStandard).Returns(row.TokenStandard);
            nft.SetupGet(item => item.MetadataURI).Returns(row.MetadataUri);
            nft.SetupGet(item => item.Name).Returns(row.Name);
            nft.SetupGet(item => item.Attributes).Returns(new Dictionary<string, string>());
            nft.SetupGet(item => item.IsTransferable).Returns(row.IsTransferable);
            nft.SetupGet(item => item.IsSoulbound).Returns(row.IsSoulbound);
            nft.SetupGet(item => item.IsActive).Returns(row.IsActive);
            nft.SetupGet(item => item.MintedDate).Returns(row.MintedDate.UtcDateTime);
            nft.SetupGet(item => item.LastTransferDate).Returns(row.LastTransferDate?.UtcDateTime);
            return AZOAResult<IUserNFT>.Success(nft.Object);
        });
        Access.Setup(item => item.RequireValueAccessAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<bool>.Success(true));
        var governance = new Mock<INodeGovernanceGuard>();
        governance.Setup(item => item.EnsureAllowedAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(NodeGovernanceDecision.Allowed);
        var authorities = new AZOA.WebAPI.Services.Signing.SigningAdmissionAuthorityFactory(Mock.Of<IUserStore>(), Wallets.Object,
            Mock.Of<SurrealForge.Client.Query.ISurrealExecutor>());
        Service = new(Store, Wallets.Object, Nfts.Object, Factory.Object, Access.Object, governance.Object, Configuration, Clock, ProtocolCosts.Object, WorkflowCosts.Object, authorities, ActionExecutions.Object);
    }

    internal ActionNodeExecution AuthorizeWorkflow(string frozen, ActionExternalOperationReference reference)
    {
        var input = JsonSerializer.Deserialize<ActionFrozenNodeInput>(frozen)!;
        var scopes = ActionSigningScopes.Required(input.NodeType, input.Config.GetRawText())
            ?? throw new InvalidOperationException("Fixture requires supported signing scopes.");
        var witness = new WorkflowSigningWitness(1, Credential(input.ActorUserId),
            new(input.RunId, input.NodeId, input.ActorUserId, input.ActingTenantId, reference.IntentHash),
            [AzoaScopes.ActionExecute, .. scopes], []);
        var execution = new ActionNodeExecution
        {
            Id = Guid.NewGuid(), RunId = input.RunId, NodeId = input.NodeId, State = ActionNodeState.Running,
            FrozenInput = frozen, ExternalOperation = reference, SigningAuthorityJson = JsonSerializer.Serialize(witness),
        };
        ActionExecutions.Setup(store => store.GetByRunAndNodeAsync(input.RunId, input.NodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => AZOAResult<ActionNodeExecution>.Success(execution));
        return execution;
    }

    internal async Task<NftStageAcceptance> Acceptance(string id)
    {
        var row = await Store.GetAsync(id) ?? throw new InvalidOperationException("Fixture lifecycle missing.");
        return new(Guid.NewGuid(), row.Version, row.State, row.Stage, row.PreparationGeneration);
    }

    internal IWallet AddWallet(Guid owner)
    {
        var wallet = Mock.Of<IWallet>(item => item.Id == Guid.NewGuid() && item.UserId == owner && item.ChainType == ChainType && item.Address == owner.ToString("N"));
        _wallets.Add(wallet);
        return wallet;
    }
    internal static UserNFTMintModel MintRequest(bool transferable = true) => new() { ChainType = "Algorand", MetadataURI = "ipfs://metadata", Name = "Test asset", IsTransferable = transferable };
    /// <summary>A credential live at the harness clock, for flows that advance time past earlier credentials.</summary>
    internal AZOA.WebAPI.Models.Signing.SigningCredentialWitness FreshCredential(Guid actor)
        => Credential(actor) with { IssuedAt = Clock.GetUtcNow().AddMinutes(-1), ExpiresAt = Clock.GetUtcNow().AddHours(1) };
    internal static AZOA.WebAPI.Models.Signing.SigningCredentialWitness Credential(Guid actor)
        => new(1, "Jwt", actor, null, null, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1),
            null, null, [], true, TokenUse: AzoaClaims.TokenUseLogin);
}

/// <summary>System time plus an adjustable offset for expiry-sensitive NFT flows.</summary>
internal sealed class ShiftedClock : TimeProvider
{
    internal TimeSpan Offset { get; set; }
    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
}
