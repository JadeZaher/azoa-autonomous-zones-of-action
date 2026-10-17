using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using AZOA.WebAPI.Services.Signing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Solnet.Programs;
using Solnet.Rpc.Builders;
using Solnet.Rpc.Models;
using Solnet.Wallet;
using Account = Solnet.Wallet.Account;

namespace AZOA.WebAPI.IntegrationTests;

public sealed class SolanaNftProtocolTests
{
    [Fact]
    public async Task FrozenCredential_DelegateCannotBeRemoved_AndRecreatedAtaRequiresNewAcceptance()
    {
        using var rail = new LocalSolanaRail();
        await rail.InitializeAsync();
        var creation = rail.Intent(true);
        var created = await rail.PrepareAndConfirmAsync(creation);
        var mint = new PublicKey(JsonDocument.Parse(created.RecoveryMetadata!).RootElement.GetProperty("Mint").GetString()!);
        var accept = creation with { Kind = "Accept", AssetId = mint.Key, SignerAddress = rail.Holder.PublicKey.Key,
            SigningContext = rail.HolderContext, PreparationKey = creation.Authorization!.LifecycleId + ":Accept:0",
            Authorization = creation.Authorization with { Stage = "Accept", IsFinalEffect = false }, ContractAddress = created.ContractAddress };
        await rail.PrepareAndConfirmAsync(accept);
        var ata = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(rail.Holder.PublicKey, mint);
        Func<Task> removeDelegate = () => rail.SendInstructionsAsync(rail.Holder,
            TokenProgram.Revoke(ata, rail.Holder.PublicKey));
        await removeDelegate.Should().ThrowAsync<InvalidOperationException>();
        Func<Task> replaceDelegate = () => rail.SendInstructionsAsync(rail.Holder,
            TokenProgram.Approve(ata, rail.Holder.PublicKey, rail.Holder.PublicKey, 1));
        await replaceDelegate.Should().ThrowAsync<InvalidOperationException>();

        var deliver = creation with { Kind = "Deliver", AssetId = mint.Key, PreparationKey = creation.Authorization!.LifecycleId + ":Deliver:0",
            Authorization = creation.Authorization with { Stage = "Deliver", IsFinalEffect = true }, ContractAddress = created.ContractAddress };
        var ready = await rail.Provider.PrepareNftTransactionAsync(deliver);
        ready.IsError.Should().BeFalse(ready.Message);
        await rail.SendInstructionsAsync(rail.Holder, TokenProgram.CloseAccount(ata, rail.Holder.PublicKey, rail.Holder.PublicKey, TokenProgram.ProgramIdKey));
        await rail.SendInstructionsAsync(rail.Holder, AssociatedTokenAccountProgram.CreateAssociatedTokenAccount(rail.Holder.PublicKey, rail.Holder.PublicKey, mint));
        Func<Task> staleDelivery = () => rail.Provider.SubmitNftTransactionAsync(ready.Result!);
        await staleDelivery.Should().ThrowAsync<HttpRequestException>();
        var rejected = await rail.Provider.PrepareNftTransactionAsync(deliver);
        rejected.Code.Should().Be("NFT_RECIPIENT_ACCEPTANCE_REQUIRED");
        await rail.PrepareAndConfirmAsync(accept with { PreparationKey = creation.Authorization.LifecycleId + ":Accept:1",
            Authorization = accept.Authorization! with { PreparationGeneration = 1 } });
        await rail.PrepareAndConfirmAsync(deliver with { PreparationKey = creation.Authorization.LifecycleId + ":Deliver:1",
            Authorization = deliver.Authorization! with { PreparationGeneration = 1 } });
        var ownership = await rail.Provider.ReadNftOwnershipAsync(mint.Key, rail.Holder.PublicKey.Key, created.ProtocolIdentity, contractAddress: created.ContractAddress);
        ownership.IsError.Should().BeFalse(ownership.Message);
        ownership.Result!.Exists.Should().BeTrue(); ownership.Result.Soulbound.Should().BeTrue();
        Func<Task> holderTransfer = () => rail.SendInstructionsAsync(rail.Holder,
            TokenProgram.TransferChecked(ata, AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(rail.Platform.PublicKey, mint), 1, 0, rail.Holder.PublicKey, mint));
        await holderTransfer.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task NormalNft_ExecutesMasterEditionMintAndAtomicNativeFee()
    {
        using var rail = new LocalSolanaRail(); await rail.InitializeAsync();
        var before = await rail.BalanceAsync(rail.Treasury.PublicKey.Key);
        var intent = rail.Intent(false, "17000");
        var prepared = await rail.PrepareAndConfirmAsync(intent);
        prepared.TokenStandard.Should().Be("MetaplexNonFungible");
        var mint = JsonDocument.Parse(prepared.RecoveryMetadata!).RootElement.GetProperty("Mint").GetString()!;
        (await rail.BalanceAsync(rail.Treasury.PublicKey.Key)).Should().Be(before + 17000);
        var ownership = await rail.Provider.ReadNftOwnershipAsync(mint, rail.Actor.PublicKey.Key, prepared.ProtocolIdentity, contractAddress: prepared.ContractAddress);
        ownership.IsError.Should().BeFalse(ownership.Message);
        ownership.Result!.Exists.Should().BeTrue(); ownership.Result.Soulbound.Should().BeFalse();
    }

    private sealed class LocalSolanaRail : IDisposable
    {
        private readonly HttpClient _rpc = new() { BaseAddress = new Uri(Environment.GetEnvironmentVariable("AZOA_SOLANA_VALIDATOR_URL") ?? "http://127.0.0.1:18899"), Timeout = TimeSpan.FromSeconds(30) };
        internal Account Actor { get; } = new(); internal Account Holder { get; } = new();
        internal Account Platform { get; } = new(); internal Account Treasury { get; } = new();
        internal SigningContext ActorContext { get; } = SigningContext.ForUser(Guid.NewGuid(), Guid.NewGuid()).ForChain("Solana");
        internal SigningContext HolderContext { get; } = SigningContext.ForUser(Guid.NewGuid(), Guid.NewGuid()).ForChain("Solana");
        internal SolanaProvider Provider { get; private set; } = null!;

        internal async Task InitializeAsync()
        {
            var genesis = (await CallAsync("getGenesisHash")).GetString()!;
            foreach (var account in new[] { Actor, Holder, Platform, Treasury })
            {
                var signature = (await CallAsync("requestAirdrop", account.PublicKey.Key, 2_000_000_000UL)).GetString()!;
                await ConfirmAsync(signature);
            }
            var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").AddEnvironmentVariables().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZOA:Solana:Networks:Devnet:GenesisHash"] = genesis,
                ["AZOA:Solana:CredentialAuthorityAddress"] = Platform.PublicKey.Key,
                ["Blockchain:DefaultNetwork"] = "Devnet", ["Blockchain:Chains:0:ChainType"] = "Solana",
                ["Blockchain:Chains:0:Devnet:NodeUrl"] = _rpc.BaseAddress!.AbsoluteUri,
            }).Build();
            var custody = new LocalCustody(Platform, new() { [ActorContext] = Actor, [HolderContext] = Holder });
            Provider = new(config, NullLogger<SolanaProvider>.Instance,
                new TransactionSignerFactory(new ITransactionSigner[] { new SolanaTransactionSigner() }), custody, null, null);
        }
        internal NftTransactionIntent Intent(bool credential, string fee = "0")
        {
            var id = Guid.NewGuid().ToString("N");
            var quote = new NftFeeQuote("Solana", ChainNetwork.Devnet, "Mint", 1, "SOL", 9, fee, Treasury.PublicKey.Key, ActorContext.UserId);
            var authorization = new NftOperationAuthorization(id, "Mint", "Create", 0, ActorContext.UserId, Actor.PublicKey.Key, ActorContext,
                credential ? ActorContext.UserId : null, credential ? Actor.PublicKey.Key : null, credential ? ActorContext : null, !credential, quote);
            return new("Create", "Solana", ChainNetwork.Devnet, credential ? Platform.PublicKey.Key : Actor.PublicKey.Key,
                credential ? Holder.PublicKey.Key : Actor.PublicKey.Key, null, "Protocol fixture", "ipfs://azoa-protocol-fixture", credential,
                credential ? SigningContext.PlatformFor("Solana") : ActorContext, PreparationKey: id + ":Create:0",
                Fee: fee == "0" ? null : new(quote, Actor.PublicKey.Key, ActorContext), Authorization: authorization);
        }
        internal async Task<PreparedNftTransaction> PrepareAndConfirmAsync(NftTransactionIntent intent)
        {
            if (intent.Kind == NftTransactionKinds.Create)
            {
                var quoted = await Provider.QuoteNftProtocolCostsAsync(intent);
                quoted.IsError.Should().BeFalse(quoted.Message);
                var estimate = quoted.Result!;
                var now = DateTimeOffset.UtcNow;
                intent = intent with { ProtocolCostQuote = new(Guid.NewGuid().ToString("N"), ActorContext.UserId,
                    ActorContext.ActingTenantId, ActorContext.WalletId, Actor.PublicKey.Key, "Solana", ChainNetwork.Devnet,
                    estimate.PhysicalNetworkIdentity, intent.Authorization!.LifecycleId, CanonicalJson.Hash(intent), "Create", 0,
                    estimate.TokenStandard, estimate.PolicyEvidenceJson, estimate.Limits, now, now.AddMinutes(5)) };
            }
            var result = await Provider.PrepareNftTransactionAsync(intent);
            result.IsError.Should().BeFalse(result.Message);
            await Provider.SubmitNftTransactionAsync(result.Result!);
            await ConfirmAsync(result.Result!.TransactionId);
            var observation = await Provider.ObserveNftTransactionAsync(result.Result);
            observation.Result!.Status.Should().Be("Confirmed");
            return result.Result;
        }
        internal async Task SendInstructionsAsync(Account signer, params TransactionInstruction[] instructions)
        {
            var hash = (await CallAsync("getLatestBlockhash", new { commitment = "finalized" })).GetProperty("value").GetProperty("blockhash").GetString()!;
            var builder = new TransactionBuilder().SetRecentBlockHash(hash).SetFeePayer(signer);
            foreach (var instruction in instructions) builder.AddInstruction(instruction);
            var bytes = builder.Build(signer);
            var signature = (await CallAsync("sendTransaction", Convert.ToBase64String(bytes), new { encoding = "base64", preflightCommitment = "finalized" })).GetString()!;
            await ConfirmAsync(signature);
        }
        internal async Task<ulong> BalanceAsync(string account) => (await CallAsync("getBalance", account, new { commitment = "finalized" })).GetProperty("value").GetUInt64();
        private async Task ConfirmAsync(string signature)
        {
            for (var i = 0; i < 180; i++)
            {
                var status = (await CallAsync("getSignatureStatuses", new[] { signature }, new { searchTransactionHistory = true })).GetProperty("value")[0];
                if (status.ValueKind != JsonValueKind.Null && status.GetProperty("confirmationStatus").GetString() == "finalized")
                {
                    status.GetProperty("err").ValueKind.Should().Be(JsonValueKind.Null); return;
                }
                await Task.Delay(250);
            }
            throw new TimeoutException("Local validator did not finalize the protocol fixture.");
        }
        private async Task<JsonElement> CallAsync(string method, params object[] args)
        {
            using var response = await _rpc.PostAsJsonAsync("", new { jsonrpc = "2.0", id = 1, method, @params = args });
            response.EnsureSuccessStatusCode(); using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (document.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException("Local validator rejected " + method + ": " + error.GetRawText());
            return document.RootElement.GetProperty("result").Clone();
        }
        public void Dispose() => _rpc.Dispose();
    }

    private sealed class LocalCustody(Account platform, Dictionary<SigningContext, Account> wallets) : IKeyCustodyService
    {
        public Task<AZOAResult<T>> WithSigningKeyAsync<T>(Guid walletId, Guid userId, Func<byte[], Task<T>> sign) =>
            WithSigningKeyAsync(SigningContext.ForUser(userId, walletId).ForChain("Solana"), sign);
        public Task<AZOAResult<T>> WithSigningKeyAsync<T>(SigningContext context, Func<byte[], Task<T>> sign) =>
            wallets.TryGetValue(context, out var account) ? Sign(account, sign) : Task.FromResult(AZOAResult<T>.Failure("Unknown local wallet."));
        public Task<AZOAResult<T>> WithPlatformSigningKeyAsync<T>(bool permitted, Func<byte[], Task<T>> sign) =>
            permitted ? Sign(platform, sign) : Task.FromResult(AZOAResult<T>.Failure("Platform authority refused."));
        public Task<AZOAResult<T>> WithPlatformSigningKeyAsync<T>(bool permitted, SigningContext context, Func<byte[], Task<T>> sign) =>
            WithPlatformSigningKeyAsync(context.ChainType!, permitted, context, sign);
        public Task<AZOAResult<T>> WithPlatformSigningKeyAsync<T>(string chain, bool permitted, SigningContext context, Func<byte[], Task<T>> sign) =>
            WithPlatformSigningKeyAsync(permitted && chain == "Solana" && context.IsPlatform && !context.IsTenantDriven, sign);
        public Task<AZOAResult<bool>> CanSignAsync(Guid walletId, Guid userId) => Task.FromResult(AZOAResult<bool>.Success(wallets.Keys.Any(key => key.UserId == userId && key.WalletId == walletId)));
        public AZOAResult<IWallet> RewrapAsync(IWallet wallet, WalletKeyService oldKeyService, WalletKeyService newKeyService) =>
            AZOAResult<IWallet>.Failure("This protocol fixture owns ephemeral keys, not encrypted wallet storage.");
        private static async Task<AZOAResult<T>> Sign<T>(Account account, Func<byte[], Task<T>> sign)
        {
            var bytes = (byte[])account.PrivateKey.KeyBytes.Clone();
            try { return AZOAResult<T>.Success(await sign(bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }
}
