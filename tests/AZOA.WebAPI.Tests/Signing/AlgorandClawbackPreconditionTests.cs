using System.Net;
using System.Text.Json;
using Algorand.Algod.Model.Transactions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Services.Signing;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using Xunit;
using AlgoAccount = Algorand.Algod.Model.Account;

namespace AZOA.WebAPI.Tests.Signing;

/// <summary>
/// D19 clawback preconditions, driven against an in-process Algod stub.
///
/// <para>The keystone here is the FROZEN case. Every soulbound credential is minted
/// <c>defaultFrozen: true</c>, so both the holder's holding and the issuer's holding of
/// that ASA are frozen. Algorand's clawback is precisely the movement that still works
/// on a frozen holding — at both ends — so a destination proof borrowed from the
/// ordinary transfer path (which refuses a frozen holding) refuses EVERY legitimate
/// revocation, making the whole rail inert while looking safe.</para>
///
/// <para>The fix must not swing the other way: the destination's opt-in is still PROVEN
/// against the node, because Algorand rejects a movement into an account that never
/// opted in and only that account can sign its own opt-in.</para>
/// </summary>
public class AlgorandClawbackPreconditionTests
{
    private const ulong AssetIndex = 778899UL;

    private readonly AlgoAccount _platform = new();   // clawback authority (and signer)
    private readonly AlgoAccount _holder = new();     // the account the credential leaves
    private readonly AlgoAccount _issuer = new();     // the account it returns to

    private int _submitCount;
    private volatile byte[]? _lastSubmittedBody;

    private AlgorandProvider NewProvider(HttpMessageHandler handler)
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

        var keyService = new WalletKeyService(config);
        var signerFactory = new TransactionSignerFactory(new[] { new AlgorandTransactionSigner() });
        return new AlgorandProvider(
            config,
            NullLogger<AlgorandProvider>.Instance,
            signerFactory,
            keyService,
            custodyService: null,
            custodyScopeFactory: null,
            faucet: null,
            httpMessageHandler: handler);
    }

    private Task<AZOA.WebAPI.Models.Responses.AZOAResult<string>> Revoke(AlgorandProvider provider) =>
        provider.ClawbackAssetAsync(
            AssetIndex.ToString(),
            holderAddress: _holder.Address.EncodeAsString(),
            recipientAddress: _issuer.Address.EncodeAsString(),
            amount: RailAmount.OfChainAsset(1),
            signingContext: SigningContext.PlatformFor("Algorand"),
            clawbackAddress: null);

    // ── The keystone: a soulbound credential is frozen at BOTH ends ───────────

    /// <summary>
    /// The exact production shape: a defaultFrozen ASA, a frozen holder holding, and a
    /// frozen issuer holding. This must SUBMIT. If the destination proof ever refuses a
    /// frozen holding again, no credential this codebase mints can ever be revoked.
    /// </summary>
    [Fact]
    public async Task FrozenDestinationHolding_DoesNotBlockAClawback()
    {
        using var stub = new AlgodStub(this, holderFrozen: true, destinationFrozen: true);
        var provider = NewProvider(stub);

        var result = await Revoke(provider);

        result.IsError.Should().BeFalse(result.Message);
        result.Message.Should().NotContain("frozen",
            "clawback is the movement Algorand still permits on a frozen holding");
        _submitCount.Should().Be(1);

        // Wire bytes: the units must leave the HOLDER (AssetSender) and land on the
        // issuer, signed by the clawback authority — not a self-transfer.
        _lastSubmittedBody.Should().NotBeNullOrEmpty();
        var signed = Algorand.Utils.Encoder.DecodeFromMsgPack<SignedTransaction>(_lastSubmittedBody!);
        signed.Sig.Should().NotBeNull("the clawback must carry a real signature");
        var txn = signed.Tx.Should().BeOfType<AssetClawbackTransaction>().Subject;
        txn.XferAsset.Should().Be(AssetIndex);
        txn.AssetSender.EncodeAsString().Should().Be(_holder.Address.EncodeAsString());
        txn.AssetReceiver.EncodeAsString().Should().Be(_issuer.Address.EncodeAsString());
        txn.Sender.EncodeAsString().Should().Be(_platform.Address.EncodeAsString());
        txn.AssetAmount.Should().Be(1UL);
    }

    [Fact]
    public async Task FrozenHolderHolding_DoesNotBlockAClawback()
    {
        using var stub = new AlgodStub(this, holderFrozen: true, destinationFrozen: false);
        var provider = NewProvider(stub);

        var result = await Revoke(provider);

        result.IsError.Should().BeFalse(result.Message);
        _submitCount.Should().Be(1);
    }

    // ── …and the proof is still a PROOF, not an assumption ────────────────────

    [Fact]
    public async Task DestinationThatNeverOptedIn_IsRefused_AndNothingIsSubmitted()
    {
        using var stub = new AlgodStub(this, holderFrozen: true, destinationFrozen: false)
        {
            DestinationOptedIn = false,
        };
        var provider = NewProvider(stub);

        var result = await Revoke(provider);

        result.IsError.Should().BeTrue("the chain rejects a movement into an account with no opt-in");
        result.Message.Should().Contain("opted into");
        _submitCount.Should().Be(0, "nothing may be signed on an unproven precondition");
    }

    [Fact]
    public async Task DestinationAccountThatCannotBeRead_IsRefused_RatherThanAssumedReady()
    {
        using var stub = new AlgodStub(this, holderFrozen: true, destinationFrozen: false)
        {
            FailDestinationRead = true,
        };
        var provider = NewProvider(stub);

        var result = await Revoke(provider);

        result.IsError.Should().BeTrue("an unreadable destination is not a proven one");
        _submitCount.Should().Be(0);
    }

    [Fact]
    public async Task HolderThatDoesNotHoldTheCredential_IsRefused()
    {
        using var stub = new AlgodStub(this, holderFrozen: true, destinationFrozen: true)
        {
            HolderOptedIn = false,
        };
        var provider = NewProvider(stub);

        var result = await Revoke(provider);

        result.IsError.Should().BeTrue();
        _submitCount.Should().Be(0);
    }

    [Fact]
    public async Task SignerThatIsNotTheAssetsClawbackAuthority_IsRefused()
    {
        using var stub = new AlgodStub(this, holderFrozen: true, destinationFrozen: true)
        {
            ClawbackAuthority = _issuer.Address.EncodeAsString(),
        };
        var provider = NewProvider(stub);

        var result = await Revoke(provider);

        result.IsError.Should().BeTrue("only the registered clawback account may move another's asset");
        result.Message.Should().Contain("clawback authority");
        _submitCount.Should().Be(0);
    }

    // ── In-process Algod stub ─────────────────────────────────────────────────

    private sealed class AlgodStub : HttpMessageHandler
    {
        private readonly AlgorandClawbackPreconditionTests _owner;
        private readonly bool _holderFrozen;
        private readonly bool _destinationFrozen;

        public AlgodStub(AlgorandClawbackPreconditionTests owner, bool holderFrozen, bool destinationFrozen)
        {
            _owner = owner;
            _holderFrozen = holderFrozen;
            _destinationFrozen = destinationFrozen;
            ClawbackAuthority = owner._platform.Address.EncodeAsString();
        }

        public string ClawbackAuthority { get; init; }
        public bool HolderOptedIn { get; init; } = true;
        public bool DestinationOptedIn { get; init; } = true;
        public bool FailDestinationRead { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/v2/transactions/params", StringComparison.Ordinal))
                return Json(new Dictionary<string, object?>
                {
                    ["fee"] = 0,
                    ["min-fee"] = 1000,
                    ["last-round"] = 100,
                    ["genesis-id"] = "devnet-v1.0",
                    ["genesis-hash"] = "SGO1GKSzyE7IEPItTxCByw9x8FmnrCDexi9/cOUJOiI=",
                });

            if (path == $"/v2/assets/{AssetIndex}")
                return Json(new Dictionary<string, object?>
                {
                    ["index"] = AssetIndex,
                    ["params"] = new Dictionary<string, object?>
                    {
                        ["creator"] = _owner._platform.Address.EncodeAsString(),
                        ["total"] = 1,
                        ["decimals"] = 0,
                        ["manager"] = _owner._platform.Address.EncodeAsString(),
                        ["clawback"] = ClawbackAuthority,
                        // The soulbound shape: frozen at issuance, forever.
                        ["default-frozen"] = true,
                    },
                });

            if (path == $"/v2/accounts/{_owner._holder.Address.EncodeAsString()}")
                return Account(HolderOptedIn, _holderFrozen);

            if (path == $"/v2/accounts/{_owner._issuer.Address.EncodeAsString()}")
                // 404, not 5xx: a 5xx is classed as transient and would burn the retry
                // budget (and several seconds) before the refusal this test is about.
                return FailDestinationRead
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : Account(DestinationOptedIn, _destinationFrozen);

            if (path == "/v2/transactions")
            {
                _owner._lastSubmittedBody = request.Content is null
                    ? Array.Empty<byte>()
                    : await request.Content.ReadAsByteArrayAsync(cancellationToken);
                Interlocked.Increment(ref _owner._submitCount);
                return Json(new Dictionary<string, object?> { ["txId"] = "STUBCLAWBACKTX" });
            }

            if (path.Contains("/v2/transactions/pending/", StringComparison.Ordinal))
                return Json(new Dictionary<string, object?>
                {
                    ["confirmed-round"] = 42,
                    ["pool-error"] = "",
                });

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Account(bool optedIn, bool frozen)
        {
            var body = new Dictionary<string, object?> { ["amount"] = 1_000_000 };
            if (optedIn)
                body["assets"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["asset-id"] = AssetIndex,
                        ["amount"] = 1,
                        ["is-frozen"] = frozen,
                    },
                };
            return Json(body);
        }

        private static HttpResponseMessage Json(Dictionary<string, object?> body)
        {
            var content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
