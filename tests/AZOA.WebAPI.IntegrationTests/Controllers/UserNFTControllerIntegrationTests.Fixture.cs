using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.IntegrationTests.Http.Infrastructure;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SurrealForge.Client.Query;
using UserNFT = AZOA.WebAPI.Models.UserNFT;
using StoredUser = AZOA.WebAPI.Persistence.SurrealDb.Models.User;
using Wallet = AZOA.WebAPI.Models.Wallet;

namespace AZOA.WebAPI.IntegrationTests.Controllers;

public partial class UserNFTControllerIntegrationTests
{
    private readonly NftFlowWebApplicationFactory _flow;

    private async Task<UserNFT> SeedMintedAsync(string name)
    {
        var actor = Guid.Parse(TestAuthHandler.DefaultUserId);
        var executor = await CreateExecutorAsync(TestNamespace);
        await EnsureFixtureUserAsync(actor);
        var nft = new UserNFT
        {
            UserId = actor, ChainType = "Algorand", NFTContractAddress = NftVendorEdge.Contract, TokenStandard = "ASA",
            TokenId = Guid.NewGuid().ToString("N"), MetadataURI = "https://example.test/metadata", Name = name,
            CurrentOwner = "fixtureowner" + Guid.NewGuid().ToString("N"), IsActive = true, IsTransferable = true,
        };
        using var scope = Factory.Services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<INftStore>().UpsertUserNFTAsync(nft);
        saved.IsError.Should().BeFalse("the existing ownership fixture must be stored");
        var wallet = new Wallet { UserId = actor, ChainType = nft.ChainType, Address = nft.CurrentOwner!, WalletType = WalletType.Platform };
        (await scope.ServiceProvider.GetRequiredService<IWalletStore>().UpsertAsync(wallet)).IsError.Should().BeFalse();
        var journalId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var command = new NftLifecycleCommand(actor, actor, wallet.Id, wallet.Id, nft.CurrentOwner!, nft.CurrentOwner!,
            new UserNFTMintModel { ChainType = nft.ChainType, NFTContractAddress = nft.NFTContractAddress, TokenStandard = nft.TokenStandard,
                MetadataURI = nft.MetadataURI, Name = name }, -1, Guid.Empty,
            FeeQuote: new("Algorand", ChainNetwork.Devnet, "Mint", 1, "ALGO", 6, "1000", "fixturetreasury", actor));
        var requestJson = JsonSerializer.Serialize(command);
        (await executor.ExecuteAsync(SurrealWriter.Create(new NftLifecycle
        {
            Id = journalId, NftId = nft.Id.ToString("N"), ActorUserId = actor.ToString("N"), RecipientUserId = actor.ToString("N"),
            RequestJson = requestJson, RequestHash = NftLifecycleIdentity.Fingerprint(requestJson), Operation = "Mint", State = "Completed", Stage = "Create",
            ChainType = nft.ChainType, Network = "Devnet", AssetId = nft.TokenId, ProtocolIdentity = NftVendorEdge.Protocol,
            ContractAddress = NftVendorEdge.Contract, TokenStandard = "ASA", CreationTransactionId = journalId, FinalTransactionId = journalId,
            CreatedAt = now, UpdatedAt = now,
        }))).EnsureAllOk();
        (await executor.ExecuteAsync(SurrealWriter.Create(new NftAssetAuthority
        {
            Id = NftLifecycleIdentity.Fingerprint(JsonSerializer.Serialize(new[]
                { nft.ChainType, "Devnet", NftVendorEdge.Protocol, NftVendorEdge.Contract, nft.TokenId })),
            NftId = nft.Id.ToString("N"), ChainType = nft.ChainType, Network = "Devnet", ProtocolIdentity = NftVendorEdge.Protocol,
            ContractAddress = NftVendorEdge.Contract, AssetId = nft.TokenId, TokenStandard = "ASA", CreatorUserId = actor.ToString("N"),
            HolderUserId = actor.ToString("N"), OwnerAddress = nft.CurrentOwner!, CreationTransactionId = journalId,
            IssuanceLifecycleId = journalId, LatestLifecycleId = journalId, LatestTransactionId = journalId, UpdatedAt = now,
        }))).EnsureAllOk();
        _flow.Nfts.SeedAsset(nft.TokenId, nft.CurrentOwner!);
        return nft;
    }

    private async Task EnsureFixtureUserAsync(Guid actor)
    {
        var executor = await CreateExecutorAsync(TestNamespace);
        if (await executor.QuerySingleAsync<StoredUser>(SurrealQuery<StoredUser>.Key(actor.ToString("N"))) is null)
            (await executor.ExecuteAsync(SurrealWriter.Create(new StoredUser
            {
                Id = actor.ToString("N"), Username = actor.ToString("N"), Email = actor + "@example.test", PasswordHash = "fixture-only", IsActive = true,
            }))).EnsureAllOk();
    }

    private async Task<Wallet> SeedStoredWalletAsync(Guid owner, string address)
    {
        await EnsureFixtureUserAsync(owner);
        var wallet = new Wallet { UserId = owner, ChainType = "Algorand", Address = address, WalletType = WalletType.External };
        using var scope = Factory.Services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<IWalletStore>().UpsertAsync(wallet);
        saved.IsError.Should().BeFalse("the historical wallet arrangement must be persisted before the NFT request");
        saved.Result.Should().NotBeNull();
        saved.Result!.Id.Should().Be(wallet.Id);
        saved.Result.UserId.Should().Be(owner);
        return wallet;
    }

    private sealed record NftActor(Guid Id, Wallet Wallet, HttpClient Client) : IDisposable
    {
        /// <inheritdoc/>
        public void Dispose() => Client.Dispose();
    }

    private async Task<NftActor> VerifiedActorAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var email = "nft-" + suffix + "@example.test";
        const string password = "Nft-Fixture-Correct-Horse-9!";
        using var anonymous = Factory.CreateClient();
        using var registered = await anonymous.PostAsJsonAsync("api/user/register", new { username = "nft" + suffix, email, password }, JsonOptions);
        var person = await ReadPayloadAsync<UserProfileResponse>(registered);
        using var login = await anonymous.PostAsJsonAsync("api/user/login", new { email, password }, JsonOptions);
        var token = await ReadPayloadAsync<string>(login);
        new JwtSecurityTokenHandler().ReadJwtToken(token).ValidTo.Should().BeAfter(DateTime.UtcNow);
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var started = await client.PostAsync("api/kyc/session", null);
        await ReadPayloadAsync<KycSessionStartModel>(started);
        var vendor = _flow.Identomat.LatestIdentitySessionFor(person.Id)!;
        vendor.Should().NotBeNull();
        vendor.Verdict = IdentomatVerdict.Approved;
        using var callback = await anonymous.PostAsync("api/kyc/callback/identomat",
            new StringContent(IdentomatVendor.CallbackBody(_flow.CallbackApiKey, vendor.Id, "APPROVED"), System.Text.Encoding.UTF8, "application/json"));
        callback.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var status = await client.GetAsync("api/kyc/status");
        (await ReadPayloadAsync<KycSubmissionModel>(status)).Status.Should().Be(KycStatus.APPROVED);
        var wallet = new Wallet
        {
            UserId = person.Id, ChainType = "Algorand", Address = "fixturewallet" + suffix, WalletType = WalletType.Platform,
        };
        using var scope = Factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IWalletStore>().UpsertAsync(wallet)).IsError.Should().BeFalse();
        return new(person.Id, wallet, client);
    }

    private async Task<NftFeeAuthorization> FeeAsync(HttpClient client, string operation, Guid? nft = null)
    {
        using var response = await client.GetAsync($"api/UserNFT/fee-quote?chainType=Algorand&operation={operation}" + (nft is null ? "" : $"&nftId={nft}"));
        var quote = await ReadPayloadAsync<NftFeeQuote>(response);
        return new(quote.Revision, quote.NativeBaseUnits, quote.QuoteId);
    }

    private async Task<(NftLifecycleView View, UserNFT Nft, UserNFTMintModel Draft, string Key)> MintCompletedAsync(NftActor actor, string name, bool soulbound = false)
    {
        var key = "nft-" + Guid.NewGuid().ToString("N");
        var draft = new UserNFTMintModel
        {
            ChainType = "Algorand", WalletId = actor.Wallet.Id, NFTContractAddress = NftVendorEdge.Contract, TokenStandard = "ASA",
            MetadataURI = "https://example.test/metadata", Name = name, IsSoulbound = soulbound, IsTransferable = !soulbound,
            Description = "Integration test NFT", Attributes = new() { ["tier"] = "1", ["score"] = "100" },
            FeeAuthorization = await FeeAsync(actor.Client, "Mint"),
        };
        using var costResponse = await SendKeyedAsync(actor.Client, "api/UserNFT/mint-cost-quote", draft, key);
        draft.ProtocolCostAuthorization = (await ReadPayloadAsync<NftProtocolCostOffer>(costResponse)).Authorization;
        using var minted = await SendKeyedAsync(actor.Client, "api/UserNFT/mint", draft, key);
        var view = await CompleteAsync(actor.Client, await ReadPayloadAsync<NftLifecycleView>(minted));
        using var projection = await actor.Client.GetAsync($"api/UserNFT/{view.NftId}");
        return (view, await ReadPayloadAsync<UserNFT>(projection), draft, key);
    }

    private async Task<NftLifecycleView> CompleteAsync(HttpClient client, NftLifecycleView view)
    {
        for (var step = 0; step < 8 && view.State != "Completed"; step++)
        {
            using var response = view.State.StartsWith("Awaiting", StringComparison.Ordinal)
                ? await client.PostAsJsonAsync($"api/UserNFT/lifecycle/{view.OperationId}/accept",
                    new NftStageAcceptance(Guid.NewGuid(), view.Version, view.State, view.Stage, view.PreparationGeneration), JsonOptions)
                : await client.PostAsync($"api/UserNFT/lifecycle/{view.OperationId}/refresh", null);
            view = await ReadPayloadAsync<NftLifecycleView>(response);
        }
        view.State.Should().Be("Completed", "the deterministic module must converge through the real durable lifecycle");
        return view;
    }

    private async Task<HttpResponseMessage> SendKeyedAsync(HttpClient client, string path, object body, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: JsonOptions) };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private async Task<T> ReadPayloadAsync<T>(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadFromJsonAsync<AZOAResult<T>>(JsonOptions);
        response.StatusCode.Should().BeOneOf([HttpStatusCode.OK, HttpStatusCode.Accepted], "application code={0}", payload?.Code);
        payload.Should().NotBeNull();
        payload!.IsError.Should().BeFalse("application code={0}", payload.Code);
        payload.Result.Should().NotBeNull();
        return payload.Result!;
    }
}
