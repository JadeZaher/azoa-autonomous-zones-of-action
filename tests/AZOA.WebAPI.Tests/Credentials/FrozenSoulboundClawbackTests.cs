// SPDX-License-Identifier: UNLICENSED

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Blockchain;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using Moq;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using AZOA.WebAPI.Services.Signing;
using AlgoAccount = Algorand.Algod.Model.Account;

namespace AZOA.WebAPI.Tests.Credentials;

/// <summary>Real signed credential issuance, frozen clawback, atomic fee, and canonical-wire regression.</summary>
public sealed class FrozenSoulboundClawbackTests
{
    private const ulong AssetIndex = 778899UL;
    private static readonly string AssetId = AssetIndex.ToString();

    private readonly AlgoAccount _platform = new();   // the ASA's clawback authority (and signer)
    private readonly AlgoAccount _holderB = new();    // the account the credential leaves
    private readonly AlgoAccount _issuerA = new();    // the account it returns to

    private int _submitCount;
    private Guid _issuerUser;
    private Guid _holderUser;
    private readonly Dictionary<string, SignedTransaction> _sent = new();
    private readonly List<PaymentTransaction> _issuerAuthorizations = new();
    private readonly Dictionary<string, ulong> _holdings = new();

    [Fact]
    public async Task RevokingAFrozenSoulboundCredential_ConfirmsEndToEnd()
    {
        var rail = new CredentialRailHarness();
        _issuerUser = rail.IssuerA;
        _holderUser = rail.RecipientB;
        rail.HasWalletOnChain(rail.IssuerA, _issuerA.Address.EncodeAsString());
        rail.HasWalletOnChain(rail.RecipientB, _holderB.Address.EncodeAsString());

        using var algod = new FrozenSoulboundAlgodStub(this);
        var provider = NewAlgorandProvider(algod);
        rail.UseProvider(provider);

        var issued = await rail.IssueAsync(AssetId);
        issued.SeamExists.Should().BeTrue(CredentialTrackSeams.MissingSeamMessage);
        issued.Outcome!.IsError.Should().BeFalse(issued.Outcome.Message);
        issued.Record!.HolderAddress.Should().Be(_holderB.Address.EncodeAsString(),
            "the clawback source address comes from current_owner, written at mint");

        _submitCount.Should().Be(3, "creation, recipient opt-in, and delivery are distinct confirmed stages");
        _issuerAuthorizations.Should().HaveCount(2, "platform creation and delivery require actual issuer signatures");
        _issuerAuthorizations.Should().OnlyContain(payment => payment.Amount == 0 && payment.Sender.EncodeAsString() == _issuerA.Address.EncodeAsString());
        _holdings[_issuerA.Address.EncodeAsString()] = 0;

        var treasury = new AlgoAccount().Address.EncodeAsString();
        rail.Rail.Configuration["NftFees:Policies:0:RevokeNativeBaseUnits"] = "30";
        rail.Rail.Configuration["NftFees:Policies:0:TreasuryAddress"] = treasury;
        var quote = (await rail.Rail.Service.QuoteFeeAsync(rail.IssuerA, "Algorand", "Revoke", nftId: issued.Record.Id)).Result!;
        var result = await rail.Manager.RevokeAsync(AssetId, new() { ChainType = "Algorand", ContractAddress = "ASA",
            FeeAuthorization = new(quote.Revision, "30", quote.QuoteId), Reason = "Membership expired",
            AuthenticationWitness = AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(rail.IssuerA) }, rail.IssuerA, "revoke");

        result.IsError.Should().BeFalse(result.Message);
        result.Message.Should().NotContain("frozen",
            "clawback is precisely the movement Algorand still permits on a frozen holding, "
            + "and every soulbound credential is frozen at both ends by construction");
        result.Result!.Status.Should().Be(OperationStatus.Completed);
        result.Result.TxHash.Should().NotBeNullOrWhiteSpace(
            "a confirmed revocation is evidenced by a transaction id, never by an empty success");
        _submitCount.Should().Be(4, "one revocation is added to the three issuance stages");
        _issuerAuthorizations.Should().HaveCount(3);
        _issuerAuthorizations.Last().Amount.Should().Be(30);
        _issuerAuthorizations.Last().Receiver.EncodeAsString().Should().Be(treasury);
        _holdings[_holderB.Address.EncodeAsString()].Should().Be(0);
        _holdings[_issuerA.Address.EncodeAsString()].Should().Be(1);
        var journal = await rail.Rail.Store.GetAsync(result.Result.LifecycleId!);
        var prepared = JsonSerializer.Deserialize<PreparedNftTransaction>(journal!.PreparedTransactionJson!)!;
        var wire = Convert.FromBase64String(prepared.SignedTransactionBase64);
        var appendedPrimary = prepared with { SignedTransactionBase64 = Convert.ToBase64String(wire.Concat(wire).ToArray()) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SubmitNftTransactionAsync(appendedPrimary));
        var feeWire = Convert.FromBase64String(prepared.AdditionalSignedTransactionsBase64![0]);
        var appendedFee = prepared with { AdditionalSignedTransactionsBase64 = [Convert.ToBase64String(feeWire.Concat(wire).ToArray())] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SubmitNftTransactionAsync(appendedFee));
        _submitCount.Should().Be(4, "tampered canonical envelopes never reach transport");
    }

    // ── Real provider over an in-process Algod ────────────────────────────────

    private AlgorandProvider NewAlgorandProvider(HttpMessageHandler handler)
    {
        var custody = new Mock<IKeyCustodyService>();
        custody.Setup(service => service.WithSigningKeyAsync(It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()))
            .Returns(async (SigningContext context, Func<byte[], Task<AZOAResult<byte[]>>> sign) =>
            {
                new[] { _issuerUser, _holderUser }.Should().Contain(context.UserId);
                var account = context.UserId == _issuerUser ? _issuerA : _holderB;
                return AZOAResult<AZOAResult<byte[]>>.Success(await sign((byte[])account.KeyPair.ClearTextPrivateKey.Clone()));
            });
        custody.Setup(service => service.WithPlatformSigningKeyAsync(true, It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()))
            .Returns(async (bool _, SigningContext _, Func<byte[], Task<AZOAResult<byte[]>>> sign)
                => AZOAResult<AZOAResult<byte[]>>.Success(await sign((byte[])_platform.KeyPair.ClearTextPrivateKey.Clone())));
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZOA:WalletEncryptionKey"] = "unit-test-wallet-encryption-key-0123456789",
                ["AZOA:Algorand:PlatformMnemonic"] = _platform.ToMnemonic(),
                ["Blockchain:DefaultNetwork"] = "devnet",
                ["Blockchain:Chains:0:ChainType"] = "Algorand",
                ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
                ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://algod.test/",
                ["Blockchain:Chains:0:Devnet:IndexerUrl"] = "http://indexer.test/",
                ["Blockchain:Chains:0:Devnet:TimeoutMs"] = "1000",
            })
            .Build();

        return new AlgorandProvider(
            config,
            NullLogger<AlgorandProvider>.Instance,
            new TransactionSignerFactory(new[] { new AlgorandTransactionSigner() }),
            new WalletKeyService(config),
            custodyService: custody.Object,
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

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "indexer.test") return Json(new Dictionary<string, object?> { ["current-round"] = 110, ["transactions"] = Array.Empty<object>() });
            if (path == "/v2/transactions/params") return Json(new Dictionary<string, object?>
            {
                ["fee"] = 0, ["min-fee"] = 1000, ["last-round"] = 100,
                ["genesis-id"] = "devnet-v1.0", ["genesis-hash"] = "SGO1GKSzyE7IEPItTxCByw9x8FmnrCDexi9/cOUJOiI=",
            });
            if (path == "/v2/status") return Json(new Dictionary<string, object?> { ["last-round"] = 110 });
            if (path == $"/v2/assets/{AssetIndex}") return Json(new Dictionary<string, object?>
            {
                ["index"] = AssetIndex, ["params"] = new Dictionary<string, object?>
                {
                    ["creator"] = _owner._platform.Address.EncodeAsString(), ["total"] = 1, ["decimals"] = 0,
                    ["manager"] = _owner._platform.Address.EncodeAsString(), ["clawback"] = _owner._platform.Address.EncodeAsString(),
                    ["freeze"] = _owner._platform.Address.EncodeAsString(), ["default-frozen"] = true,
                },
            });
            if (path.StartsWith("/v2/accounts/", StringComparison.Ordinal))
            {
                var address = path["/v2/accounts/".Length..];
                return _owner._holdings.TryGetValue(address, out var units) ? FrozenHolding(units)
                    : Json(new Dictionary<string, object?> { ["amount"] = 10000000, ["assets"] = Array.Empty<object>() });
            }
            if (path == "/v2/transactions" && request.Method == HttpMethod.Post)
            {
                var wire = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                var primary = Encoder.DecodeFromMsgPack<SignedTransaction>(wire);
                var primaryWire = Encoder.EncodeToMsgPackOrdered(primary);
                wire.Take(primaryWire.Length).Should().Equal(primaryWire);
                if (wire.Length > primaryWire.Length)
                {
                    var secondaryWire = wire[primaryWire.Length..];
                    var secondary = Encoder.DecodeFromMsgPack<SignedTransaction>(secondaryWire);
                    Encoder.EncodeToMsgPackOrdered(secondary).Should().Equal(secondaryWire, "the group has exactly two canonical envelopes");
                    var payment = secondary.Tx.Should().BeOfType<PaymentTransaction>().Subject;
                    payment.Group.Should().Be(primary.Tx.Group);
                    payment.Note.Should().Equal(primary.Tx.Note);
                    _owner._issuerAuthorizations.Add(payment);
                }
                _owner._sent[primary.Tx.TxID()] = primary;
                Interlocked.Increment(ref _owner._submitCount);
                switch (primary.Tx)
                {
                    case AssetCreateTransaction:
                        _owner._holdings[_owner._platform.Address.EncodeAsString()] = 1;
                        break;
                    case AssetClawbackTransaction clawback:
                        _owner._holdings[clawback.AssetSender.EncodeAsString()] = 0;
                        _owner._holdings[clawback.AssetReceiver.EncodeAsString()] = 1;
                        break;
                    case AssetAcceptTransaction accept:
                        _owner._holdings[accept.AssetReceiver.EncodeAsString()] = 0;
                        break;
                    default: throw new InvalidOperationException("Unexpected frozen credential transaction.");
                }
                return Json(new Dictionary<string, object?> { ["txId"] = primary.Tx.TxID() });
            }
            if (path.StartsWith("/v2/transactions/pending/", StringComparison.Ordinal))
            {
                var id = path["/v2/transactions/pending/".Length..];
                return _owner._sent.ContainsKey(id) ? Json(new Dictionary<string, object?>
                    { ["confirmed-round"] = 105, ["asset-index"] = AssetIndex, ["pool-error"] = "" })
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
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
