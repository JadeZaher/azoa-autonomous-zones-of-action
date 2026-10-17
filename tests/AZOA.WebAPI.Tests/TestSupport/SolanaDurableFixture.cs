using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Solnet.Rpc.Models;
using Solnet.Wallet.Utilities;
using Account = Solnet.Wallet.Account;

namespace AZOA.WebAPI.Tests.TestSupport;

internal sealed class SolanaDurableFixture : HttpMessageHandler
{
    internal const string Genesis = "EtWTRABZaYq6iMfeYKouRu166VU2xqa1wcaWoxPkrZBG";
    internal const string Blockhash = "9ZNTfG4NyQgxy2SWjSiQoUyBPEvXT2xo7fKc5hPYYJ7b";
    internal Account Actor { get; } = new();
    internal Account Recipient { get; } = new();
    internal Account Platform { get; } = new();
    internal Account Treasury { get; } = new();
    internal Guid ActorId { get; } = Guid.NewGuid();
    internal Guid RecipientId { get; } = Guid.NewGuid();
    internal Guid ActorWalletId { get; } = Guid.NewGuid();
    internal Guid RecipientWalletId { get; } = Guid.NewGuid();
    internal Dictionary<string, object> Accounts { get; } = new();
    internal SolanaProvider Provider { get; }
    internal IConfigurationRoot Configuration { get; }
    internal int Sends { get; private set; }
    internal long Height { get; set; } = 100;
    internal string GenesisResponse { get; set; } = Genesis;
    internal string? SubmittedWire { get; set; }
    internal bool Finalized { get; set; }
    internal bool Failed { get; set; }
    internal bool OmitHistory { get; set; }
    internal bool LoseResponse { get; set; }
    internal List<SigningContext> Consents { get; } = new();
    internal ulong MessageFee { get; set; } = 5000;
    private readonly ServiceProvider? _services;

    internal SolanaDurableFixture(INativePreparationStore? nativePreparations = null)
    {
        var configuration = new ConfigurationBuilder().AddJsonFile("appsettings.json").AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AZOA:Solana:CredentialAuthorityAddress"] = Platform.PublicKey.Key,
            ["AZOA:Solana:RpcRetryMaxAttempts"] = "0",
            ["Blockchain:DefaultNetwork"] = "Devnet",
            ["Blockchain:Chains:0:ChainType"] = "Solana",
            ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://solana.test/",
        }).Build();
        Configuration = configuration;
        var custody = new Mock<IKeyCustodyService>();
        custody.Setup(item => item.WithSigningKeyAsync(It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()))
            .Returns((SigningContext context, Func<byte[], Task<AZOAResult<byte[]>>> sign) => Sign(context, sign));
        custody.Setup(item => item.WithPlatformSigningKeyAsync(It.IsAny<bool>(), It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()))
            .Returns((bool _, SigningContext context, Func<byte[], Task<AZOAResult<byte[]>>> sign) => Sign(context, sign));
        if (nativePreparations is not null) _services = new ServiceCollection().AddSingleton(nativePreparations).BuildServiceProvider();
        Provider = new(configuration, NullLogger<SolanaProvider>.Instance,
            new TransactionSignerFactory(new ITransactionSigner[] { new SolanaTransactionSigner() }), custody.Object,
            _services?.GetRequiredService<IServiceScopeFactory>(), this);
    }

    private async Task<AZOAResult<AZOAResult<byte[]>>> Sign(SigningContext context, Func<byte[], Task<AZOAResult<byte[]>>> sign)
    {
        var account = context.IsPlatform ? Platform : context.UserId == ActorId && context.WalletId == ActorWalletId ? Actor
            : context.UserId == RecipientId && context.WalletId == RecipientWalletId ? Recipient : null;
        if (account is null) return AZOAResult<AZOAResult<byte[]>>.Failure("No authorized fixture wallet.");
        Consents.Add(context);
        var bytes = (byte[])account.PrivateKey.KeyBytes.Clone();
        try { return AZOAResult<AZOAResult<byte[]>>.Success(await sign(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal NftTransactionIntent Mint(bool credential = false, string fee = "0")
    {
        var context = SigningContext.ForUser(ActorId, ActorWalletId).ForChain("Solana");
        var id = "ffeeddccbbaa99887766554433221100";
        var quote = new NftFeeQuote("Solana", ChainNetwork.Devnet, "Mint", 1, "SOL", 9, fee, Treasury.PublicKey.Key, ActorId);
        var grant = new NftOperationAuthorization(id, "Mint", "Create", 0, ActorId, Actor.PublicKey.Key, context,
            credential ? ActorId : null, credential ? Actor.PublicKey.Key : null, credential ? context : null, !credential, quote);
        return new("Create", "Solana", ChainNetwork.Devnet, credential ? Platform.PublicKey.Key : Actor.PublicKey.Key,
            credential ? Recipient.PublicKey.Key : Actor.PublicKey.Key, null, "Membership", "ipfs://metadata", credential,
            credential ? SigningContext.PlatformFor("Solana") : context, PreparationKey: id + ":Create:0",
            Fee: !credential && fee != "0" ? new(quote, Actor.PublicKey.Key, context) : null, Authorization: grant);
    }

    internal async Task<NftTransactionIntent> QuotedMintAsync(bool credential = false, string fee = "0")
    {
        const string prefix = "AZOA:Solana:Nft:Networks:Devnet:";
        if (Configuration[prefix + "CostGuardProgram"] is null)
            SolanaProtocolCostFixture.ConfigureMetadata(this, prefix, "AZOA:Solana:Nft:ReviewedMetadataExecutables");
        var intent = Mint(credential, fee);
        var result = await Provider.QuoteNftProtocolCostsAsync(intent);
        if (result.IsError || result.Result is not { } estimate) throw new InvalidOperationException(result.Message);
        var now = DateTimeOffset.UtcNow;
        return intent with { ProtocolCostQuote = new(Guid.NewGuid().ToString("N"), ActorId, Guid.Empty,
            ActorWalletId, Actor.PublicKey.Key, "Solana", ChainNetwork.Devnet, estimate.PhysicalNetworkIdentity,
            intent.Authorization!.LifecycleId, CanonicalJson.Hash(intent), "Create", 0, estimate.TokenStandard,
            estimate.PolicyEvidenceJson, estimate.Limits, now, now.AddMinutes(5)) };
    }

    internal ValueTransferIntent Value() => new("Solana", ChainNetwork.Devnet, "SOL", Actor.PublicKey.Key,
        Recipient.PublicKey.Key, "123456789", SigningContext.ForUser(ActorId, ActorWalletId), "value:claim:0");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var root = document.RootElement; var method = root.GetProperty("method").GetString(); var args = root.GetProperty("params");
        object? result = method switch
        {
            "getGenesisHash" => GenesisResponse,
            "getLatestBlockhash" => new { context = new { slot = 90 }, value = new { blockhash = Blockhash, lastValidBlockHeight = 150 } },
            "getBlockHeight" => Height,
            "getMinimumBalanceForRentExemption" => 1461600UL,
            "getFeeForMessage" => new { context = new { slot = 90 }, value = MessageFee },
            "getAccountInfo" => new { context = new { slot = 90 }, value = Accounts.GetValueOrDefault(args[0].GetString()!) },
            "getMultipleAccounts" => new { context = new { slot = 90 }, value = args[0].EnumerateArray().Select(key => Accounts.GetValueOrDefault(key.GetString()!)).ToArray() },
            "getSignatureStatuses" => new { context = new { slot = 100 }, value = new object?[] { Finalized
                ? new { slot = 100, confirmationStatus = "finalized", err = Failed ? new { InstructionError = new object[] { 0, "InvalidArgument" } } : null } : null } },
            "getTransaction" => OmitHistory || SubmittedWire is null ? null : new { slot = 100, transaction = new[] { SubmittedWire, "base64" },
                meta = new { err = Failed ? new { InstructionError = new object[] { 0, "InvalidArgument" } } : null } },
            "sendTransaction" => Submit(args),
            _ => throw new InvalidOperationException("Unexpected fixture RPC: " + method),
        };
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt32(), result }), Encoding.UTF8, "application/json") };
    }

    private string Submit(JsonElement arguments)
    {
        Sends++; SubmittedWire = arguments[0].GetString();
        if (LoseResponse) throw new HttpRequestException("Lost submission response");
        return Encoders.Base58.EncodeData(Transaction.Deserialize(Convert.FromBase64String(SubmittedWire!)).Signatures[0].Signature);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _services?.Dispose();
        base.Dispose(disposing);
    }
}
