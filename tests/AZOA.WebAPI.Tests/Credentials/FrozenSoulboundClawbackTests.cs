// SPDX-License-Identifier: UNLICENSED

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using AZOA.WebAPI.Services.Signing;
using AlgoAccount = Algorand.Algod.Model.Account;

namespace AZOA.WebAPI.Tests.Credentials;

/// <summary>
/// AC4 — a revocation of the credential the production path actually issues, end to end,
/// against a <c>defaultFrozen: true</c> ASA with FROZEN holdings at both ends.
///
/// <para><b>Why this is not already covered.</b>
/// <c>Signing/AlgorandClawbackPreconditionTests</c> proves the provider tolerates a frozen
/// holding, and it is green. What nothing proves is that the RAIL reaches it: the mint writes
/// no <c>current_owner</c> and stamps the holder as issuer, so <c>CredentialManager</c>
/// refuses long before a clawback is built. A precondition that works on a path no request
/// can travel is the same inert-control class this codebase keeps shipping.</para>
///
/// <para>The frozen shape is not an edge case — <c>CreateSoulboundAsaAsync</c> mints
/// <c>defaultFrozen: true</c>, so it is the shape of EVERY credential this codebase issues.</para>
/// </summary>
public sealed class FrozenSoulboundClawbackTests
{
    private const ulong AssetIndex = 778899UL;
    private static readonly string AssetId = AssetIndex.ToString();

    private readonly AlgoAccount _platform = new();   // the ASA's clawback authority (and signer)
    private readonly AlgoAccount _holderB = new();    // the account the credential leaves
    private readonly AlgoAccount _issuerA = new();    // the account it returns to

    private int _submitCount;

    [Fact]
    public async Task RevokingAFrozenSoulboundCredential_ConfirmsEndToEnd()
    {
        var rail = new CredentialRailHarness();
        rail.HasWalletOnChain(rail.IssuerA, _issuerA.Address.EncodeAsString());
        rail.HasWalletOnChain(rail.RecipientB, _holderB.Address.EncodeAsString());

        using var algod = new FrozenSoulboundAlgodStub(this);
        rail.UseProvider(NewAlgorandProvider(algod));

        var issued = await rail.IssueAsync(AssetId);
        issued.SeamExists.Should().BeTrue(CredentialTrackSeams.MissingSeamMessage);
        issued.Outcome!.IsError.Should().BeFalse(issued.Outcome.Message);
        issued.Record!.HolderAddress.Should().Be(_holderB.Address.EncodeAsString(),
            "the clawback source address comes from current_owner, written at mint");

        var result = await rail.RevokeAsync(AssetId, callerUserId: rail.IssuerA);

        result.IsError.Should().BeFalse(result.Message);
        result.Message.Should().NotContain("frozen",
            "clawback is precisely the movement Algorand still permits on a frozen holding, "
            + "and every soulbound credential is frozen at both ends by construction");
        result.Result!.Status.Should().Be(OperationStatus.Completed);
        result.Result.TxHash.Should().NotBeNullOrWhiteSpace(
            "a confirmed revocation is evidenced by a transaction id, never by an empty success");
        _submitCount.Should().Be(1, "exactly one clawback reaches the network");
    }

    // ── Real provider over an in-process Algod ────────────────────────────────

    private AlgorandProvider NewAlgorandProvider(HttpMessageHandler handler)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZOA:WalletEncryptionKey"] = "unit-test-wallet-encryption-key-0123456789",
                ["AZOA:Algorand:PlatformMnemonic"] = _platform.ToMnemonic(),
                ["Blockchain:DefaultNetwork"] = "devnet",
                ["Blockchain:Chains:0:ChainType"] = "Algorand",
                ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
                ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://algod.test/",
                ["Blockchain:Chains:0:Devnet:TimeoutMs"] = "1000",
            })
            .Build();

        return new AlgorandProvider(
            config,
            NullLogger<AlgorandProvider>.Instance,
            new TransactionSignerFactory(new[] { new AlgorandTransactionSigner() }),
            new WalletKeyService(config),
            custodyService: null,
            custodyScopeFactory: null,
            faucet: null,
            httpMessageHandler: handler);
    }

    /// <summary>
    /// The production shape: a <c>defaultFrozen</c> ASA whose holder and destination holdings
    /// are both frozen, with the platform as clawback authority.
    /// </summary>
    private sealed class FrozenSoulboundAlgodStub : HttpMessageHandler
    {
        private readonly FrozenSoulboundClawbackTests _owner;

        public FrozenSoulboundAlgodStub(FrozenSoulboundClawbackTests owner) => _owner = owner;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/v2/transactions/params", StringComparison.Ordinal))
                return Task.FromResult(Json(new Dictionary<string, object?>
                {
                    ["fee"] = 0,
                    ["min-fee"] = 1000,
                    ["last-round"] = 100,
                    ["genesis-id"] = "devnet-v1.0",
                    ["genesis-hash"] = "SGO1GKSzyE7IEPItTxCByw9x8FmnrCDexi9/cOUJOiI=",
                }));

            if (path == $"/v2/assets/{AssetIndex}")
                return Task.FromResult(Json(new Dictionary<string, object?>
                {
                    ["index"] = AssetIndex,
                    ["params"] = new Dictionary<string, object?>
                    {
                        ["creator"] = _owner._platform.Address.EncodeAsString(),
                        ["total"] = 1,
                        ["decimals"] = 0,
                        ["manager"] = _owner._platform.Address.EncodeAsString(),
                        ["clawback"] = _owner._platform.Address.EncodeAsString(),
                        ["default-frozen"] = true,
                    },
                }));

            if (path == $"/v2/accounts/{_owner._holderB.Address.EncodeAsString()}")
                return Task.FromResult(FrozenHolding(units: 1));

            if (path == $"/v2/accounts/{_owner._issuerA.Address.EncodeAsString()}")
                return Task.FromResult(FrozenHolding(units: 0));

            if (path == "/v2/transactions")
            {
                Interlocked.Increment(ref _owner._submitCount);
                return Task.FromResult(Json(new Dictionary<string, object?> { ["txId"] = "FROZENCLAWBACKTX" }));
            }

            if (path.Contains("/v2/transactions/pending/", StringComparison.Ordinal))
                return Task.FromResult(Json(new Dictionary<string, object?>
                {
                    ["confirmed-round"] = 42,
                    ["pool-error"] = "",
                }));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        /// <summary>Opted in, and frozen — the only shape a soulbound holding ever has.</summary>
        private static HttpResponseMessage FrozenHolding(ulong units) => Json(new Dictionary<string, object?>
        {
            ["amount"] = 1_000_000,
            ["assets"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["asset-id"] = AssetIndex,
                    ["amount"] = units,
                    ["is-frozen"] = true,
                },
            },
        });

        private static HttpResponseMessage Json(Dictionary<string, object?> body)
        {
            var content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
