using System.Net;
using System.Text.Json;
using Algorand.Algod.Model.Transactions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Blockchain.Bridge;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Services.Signing;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using Xunit;
using AlgoAccount = Algorand.Algod.Model.Account;

namespace AZOA.WebAPI.Tests.Signing;

/// <summary>
/// The bridge value primitives must either broadcast a REAL transaction or fail CLOSED — never
/// return a fabricated-success Ok on a value path.
///
/// <para>Capability is structural: lock/release live on <see cref="IBridgeSourceLeg"/> and wrapped
/// mint/burn on <see cref="IBridgeTargetLeg"/>, both resolved through <c>TryGetModule</c>. A chain
/// that cannot bridge does not implement the module, so this file asserts MODULE ABSENCE rather
/// than a boolean. That is a strictly stronger guarantee: there is no refusal body left that could
/// regress into an <c>Ok</c>, and no <c>SupportsBridging</c> flag that could be set true by mistake.
/// Solana and Ethereum are absent on BOTH legs; Algorand is present on the source leg and
/// deliberately absent on the target leg, an asymmetry a single boolean could not express.</para>
///
/// <para>Also proves the always-true provider verifier is gone (the interface declares no
/// <c>VerifyBridgeProofAsync</c> — enforced at compile time by this file referencing
/// <see cref="IBlockchainProvider"/> without it).</para>
/// </summary>
public class BridgePrimitivesTests
{
    private const string BaseUrl = "http://algod.test/";
    private const ulong LockedAsset = 4242UL;
    private const ulong OtherAsset = 9999UL;

    private readonly AlgoAccount _platform = new();
    private readonly AlgoAccount _vault = new();
    private readonly AlgoAccount _recipient = new();

    private int _submitCount;
    private volatile byte[]? _lastSubmittedBody;

    private AlgorandProvider NewAlgorandProvider(
        bool withPlatformMnemonic = true,
        HttpMessageHandler? handler = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AZOA:WalletEncryptionKey"] = "unit-test-wallet-encryption-key-0123456789",
            ["Blockchain:DefaultNetwork"] = "devnet",
            ["Blockchain:Chains:0:ChainType"] = "Algorand",
            ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
            ["Blockchain:Chains:0:Devnet:NodeUrl"] = BaseUrl,
            ["Blockchain:Chains:0:Devnet:TimeoutMs"] = "1000",
        };
        if (withPlatformMnemonic)
            settings["AZOA:Algorand:PlatformMnemonic"] = _platform.ToMnemonic();

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
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

    private static SolanaProvider NewSolanaProvider()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:DefaultNetwork"] = "devnet",
                ["Blockchain:Chains:0:ChainType"] = "Solana",
                ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
                ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://127.0.0.1:1/",
                ["Blockchain:Chains:0:Devnet:TimeoutMs"] = "2000",
            })
            .Build();
        return new SolanaProvider(config, NullLogger<SolanaProvider>.Instance);
    }

    private static EthereumProvider NewEthereumProvider()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:DefaultNetwork"] = "devnet",
                ["Blockchain:Chains:0:ChainType"] = "Ethereum",
                ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
                ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://127.0.0.1:1/",
                ["Blockchain:Chains:0:Devnet:TimeoutMs"] = "2000",
            })
            .Build();
        return new EthereumProvider(config, NullLogger<EthereumProvider>.Instance);
    }

    private IBridgeSourceLeg AlgorandSourceLeg(HttpMessageHandler? handler = null, bool withPlatformMnemonic = true)
    {
        var provider = NewAlgorandProvider(withPlatformMnemonic, handler);
        provider.TryGetModule<IBridgeSourceLeg>(out var leg)
            .Should().BeTrue("Algorand ships a proven bridge source leg");
        return leg!;
    }

    // ─── Focused test 1: confirmed submission (the lock genuinely broadcasts) ───

    [Fact]
    public async Task Algorand_lock_broadcasts_real_asset_transfer_to_an_opted_in_vault()
    {
        const int amount = 5;
        using var stub = RunStub(confirmedRound: 9);

        var result = await AlgorandSourceLeg(stub).LockAsync(new BridgeLockRequest(
            TokenId: LockedAsset.ToString(),
            VaultAddress: _vault.Address.EncodeAsString(),
            Amount: RailAmount.OfChainAsset(amount),
            TargetChain: "Solana",
            TargetRecipient: _platform.Address.EncodeAsString()));

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.TransactionId.Should().NotBeNullOrWhiteSpace(
            "a real tx id must be returned, not a fabricated op-id");
        result.Result.PendingConfirmation.Should().BeFalse(
            "the stub confirms the transaction, so the lock is settled, not pending");
        _submitCount.Should().Be(1, "the lock must broadcast exactly one real transaction");

        _lastSubmittedBody.Should().NotBeNullOrEmpty("a signed transaction must have been posted");
        var signedTxn = Algorand.Utils.Encoder.DecodeFromMsgPack<SignedTransaction>(_lastSubmittedBody!);
        signedTxn.Sig.Should().NotBeNull("the lock tx must carry a real Ed25519 signature");
        var xfer = signedTxn.Tx.Should().BeOfType<AssetTransferTransaction>().Subject;
        xfer.XferAsset.Should().Be(LockedAsset, "the locked ASA id must reach the wire");
        xfer.AssetAmount.Should().Be((ulong)amount, "the locked amount must reach the wire");
        xfer.AssetReceiver.EncodeAsString().Should().Be(
            _vault.Address.EncodeAsString(), "value must move into the bridge vault");
    }

    // ─── Focused test 2: pending-confirmation recovery (submitted ≠ settled) ───

    [Fact]
    public async Task Algorand_lock_reports_a_broadcast_but_unconfirmed_transaction_as_pending()
    {
        using var stub = RunStub(confirmedRound: 0);

        var result = await AlgorandSourceLeg(stub).LockAsync(new BridgeLockRequest(
            TokenId: LockedAsset.ToString(),
            VaultAddress: _vault.Address.EncodeAsString(),
            Amount: RailAmount.OfChainAsset(5),
            TargetChain: "Solana",
            TargetRecipient: _platform.Address.EncodeAsString()));

        result.IsError.Should().BeFalse(
            "the transaction WAS broadcast — calling that an error would false-fail a slow but valid lock");
        _submitCount.Should().Be(1, "exactly one broadcast, never a re-send after a confirm timeout");
        result.Result!.TransactionId.Should().NotBeNullOrWhiteSpace(
            "the tx id must be surfaced so reconciliation can settle it from chain truth");
        result.Result.PendingConfirmation.Should().BeTrue(
            "an unconfirmed lock must never be readable as settled value");
        result.Message.Should().Contain(OperationStatus.PendingConfirmationMarker);
    }

    // ─── Focused test 3: ownership / opt-in are PROVEN, not assumed ───

    [Fact]
    public async Task Algorand_release_refuses_when_the_recipient_has_not_opted_in()
    {
        using var stub = RunStub(confirmedRound: 9, recipientOptedIn: false);

        var result = await AlgorandSourceLeg(stub).ReleaseAsync(new BridgeReleaseRequest(
            TokenId: LockedAsset.ToString(),
            VaultAddress: _platform.Address.EncodeAsString(),
            Amount: RailAmount.OfChainAsset(5),
            RecipientAddress: _recipient.Address.EncodeAsString()));

        result.IsError.Should().BeTrue(
            "Algorand rejects a transfer to a non-opted-in account; submitting anyway and reporting "
            + "success would strand the locked value");
        result.Message.Should().Contain("opted into");
        _submitCount.Should().Be(0, "nothing may be broadcast once the recipient is proven unable to receive");
    }

    [Fact]
    public async Task Algorand_release_refuses_when_the_vault_holds_less_than_the_requested_amount()
    {
        using var stub = RunStub(confirmedRound: 9, vaultUnits: 3);

        var result = await AlgorandSourceLeg(stub).ReleaseAsync(new BridgeReleaseRequest(
            TokenId: LockedAsset.ToString(),
            VaultAddress: _platform.Address.EncodeAsString(),
            Amount: RailAmount.OfChainAsset(5),
            RecipientAddress: _recipient.Address.EncodeAsString()));

        result.IsError.Should().BeTrue("the vault balance is read, not assumed");
        result.Message.Should().Contain("holds 3 units");
        _submitCount.Should().Be(0);
    }

    [Fact]
    public async Task Algorand_destroy_refuses_when_the_platform_does_not_hold_the_full_supply()
    {
        // Creator holds 900 of a 1000-unit supply: 100 units are outstanding elsewhere, so
        // AssetDestroy cannot succeed on chain.
        using var stub = RunStub(confirmedRound: 9, assetTotal: 1000, vaultUnits: 900);
        var provider = NewAlgorandProvider(handler: stub);

        var result = await provider.BurnAsync(
            LockedAsset.ToString(), RailAmount.OfChainAsset(1000), _platform.Address.EncodeAsString());

        result.IsError.Should().BeTrue(
            "destroy is only legal when the creator holds every unit — that must be verified, not assumed");
        result.Message.Should().Contain("900 of 1000 units");
        _submitCount.Should().Be(0, "no destroy may be broadcast against supply the platform does not own");
    }

    [Fact]
    public async Task Algorand_destroy_refuses_when_the_platform_is_not_the_asset_creator()
    {
        using var stub = RunStub(
            confirmedRound: 9, assetTotal: 1000, vaultUnits: 1000,
            creatorAddress: _recipient.Address.EncodeAsString());
        var provider = NewAlgorandProvider(handler: stub);

        var result = await provider.BurnAsync(
            LockedAsset.ToString(), RailAmount.OfChainAsset(1000), _platform.Address.EncodeAsString());

        result.IsError.Should().BeTrue("only the creator may destroy an ASA");
        result.Message.Should().Contain("created by");
        _submitCount.Should().Be(0);
    }

    // ─── Focused test 4: failure without fabricated success ───

    [Fact]
    public async Task Algorand_release_refuses_when_the_vault_is_not_the_release_signer()
    {
        using var stub = RunStub(confirmedRound: 9);

        // The configured vault is an account this provider holds no key for.
        var result = await AlgorandSourceLeg(stub).ReleaseAsync(new BridgeReleaseRequest(
            TokenId: LockedAsset.ToString(),
            VaultAddress: _vault.Address.EncodeAsString(),
            Amount: RailAmount.OfChainAsset(5),
            RecipientAddress: _recipient.Address.EncodeAsString()));

        result.IsError.Should().BeTrue(
            "an unproven vault must never be released from — the signer does not control it");
        result.Message.Should().Contain("not the platform signing account");
        _submitCount.Should().Be(0, "no release may be broadcast against a vault we cannot prove we control");
    }

    [Fact]
    public async Task Algorand_release_refuses_when_the_vault_is_rekeyed_away_from_the_release_signer()
    {
        using var stub = RunStub(confirmedRound: 9, vaultAuthAddress: _recipient.Address.EncodeAsString());

        var result = await AlgorandSourceLeg(stub).ReleaseAsync(new BridgeReleaseRequest(
            TokenId: LockedAsset.ToString(),
            VaultAddress: _platform.Address.EncodeAsString(),
            Amount: RailAmount.OfChainAsset(5),
            RecipientAddress: _recipient.Address.EncodeAsString()));

        result.IsError.Should().BeTrue(
            "a rekeyed vault is signed by a DIFFERENT key; same-address reasoning would be false");
        result.Message.Should().Contain("rekeyed");
        _submitCount.Should().Be(0);
    }

    [Fact]
    public async Task Algorand_release_broadcasts_a_real_transfer_once_every_precondition_is_proven()
    {
        using var stub = RunStub(confirmedRound: 12);

        var result = await AlgorandSourceLeg(stub).ReleaseAsync(new BridgeReleaseRequest(
            TokenId: LockedAsset.ToString(),
            VaultAddress: _platform.Address.EncodeAsString(),
            Amount: RailAmount.OfChainAsset(5),
            RecipientAddress: _recipient.Address.EncodeAsString()));

        result.IsError.Should().BeFalse(result.Message);
        _submitCount.Should().Be(1, "a proven release broadcasts exactly one real transaction");

        var signedTxn = Algorand.Utils.Encoder.DecodeFromMsgPack<SignedTransaction>(_lastSubmittedBody!);
        signedTxn.Sig.Should().NotBeNull("the release must carry a real Ed25519 signature");
        var xfer = signedTxn.Tx.Should().BeOfType<AssetTransferTransaction>().Subject;
        xfer.Sender.EncodeAsString().Should().Be(
            _platform.Address.EncodeAsString(), "value leaves the proven vault");
        xfer.AssetReceiver.EncodeAsString().Should().Be(
            _recipient.Address.EncodeAsString(), "value reaches the release recipient");
        xfer.AssetAmount.Should().Be(5UL);
    }

    [Fact]
    public async Task Algorand_lock_fails_closed_without_platform_mnemonic()
    {
        using var stub = RunStub(confirmedRound: 9);
        var result = await AlgorandSourceLeg(stub, withPlatformMnemonic: false).LockAsync(
            new BridgeLockRequest(
                LockedAsset.ToString(), _vault.Address.EncodeAsString(), RailAmount.OfChainAsset(5),
                "Solana", _platform.Address.EncodeAsString()));

        result.IsError.Should().BeTrue("a missing platform signer must fail closed, never fake a lock");
        _submitCount.Should().Be(0, "no transaction may be broadcast without a signer");
    }

    [Fact]
    public async Task Algorand_lock_rejects_non_numeric_asset_id()
    {
        using var stub = RunStub(confirmedRound: 9);
        var result = await AlgorandSourceLeg(stub).LockAsync(new BridgeLockRequest(
            "not-a-number", _vault.Address.EncodeAsString(), RailAmount.OfChainAsset(5),
            "Solana", _platform.Address.EncodeAsString()));

        result.IsError.Should().BeTrue();
        _submitCount.Should().Be(0);
    }

    [Fact]
    public async Task Algorand_lock_refuses_when_the_vault_has_not_opted_into_the_asset()
    {
        using var stub = RunStub(confirmedRound: 9);

        var result = await AlgorandSourceLeg(stub).LockAsync(new BridgeLockRequest(
            TokenId: OtherAsset.ToString(),   // the stub account holds LockedAsset only
            VaultAddress: _vault.Address.EncodeAsString(),
            Amount: RailAmount.OfChainAsset(5),
            TargetChain: "Solana",
            TargetRecipient: _platform.Address.EncodeAsString()));

        result.IsError.Should().BeTrue("a transfer into a non-opted-in vault is rejected on chain");
        result.Message.Should().Contain("opted into");
        _submitCount.Should().Be(0);
    }

    // ─── Module absence IS the refusal ───

    [Fact]
    public void Algorand_ships_a_source_leg_and_deliberately_no_target_leg()
    {
        var provider = NewAlgorandProvider();

        provider.TryGetModule<IBridgeSourceLeg>(out var sourceLeg).Should().BeTrue(
            "Algorand lock and release are real, broadcast, and proven");
        sourceLeg.Should().NotBeNull();

        provider.TryGetModule<IBridgeTargetLeg>(out var targetLeg).Should().BeFalse(
            "no canonical platform-controlled wrapped ASA lifecycle exists, so there is no wrapped "
            + "mint or burn here to fabricate — absence IS the fail-closed refusal");
        targetLeg.Should().BeNull();
    }

    [Fact]
    public void Solana_implements_neither_bridge_leg()
    {
        var provider = NewSolanaProvider();

        provider.TryGetModule<IBridgeSourceLeg>(out _).Should().BeFalse(
            "no reviewed Solana bridge lifecycle exists — the capability is absent, not refused at runtime");
        provider.TryGetModule<IBridgeTargetLeg>(out _).Should().BeFalse();
    }

    [Fact]
    public void Ethereum_implements_neither_bridge_leg()
    {
        var provider = NewEthereumProvider();

        provider.TryGetModule<IBridgeSourceLeg>(out _).Should().BeFalse(
            "no reviewed Ethereum bridge lifecycle exists — the capability is absent, not refused at runtime");
        provider.TryGetModule<IBridgeTargetLeg>(out _).Should().BeFalse();
    }

    [Fact]
    public void A_chain_without_a_bridge_leg_exposes_no_primitive_that_could_fabricate_success()
    {
        // The compile-time point of the module seam: there is no LockForBridgeAsync /
        // MintWrappedAsync / BurnWrappedAsync / ReleaseFromBridgeAsync on IBlockchainProvider at
        // all, so a non-bridging provider has NOTHING to call and NO refusal body to regress.
        typeof(IBlockchainProvider).GetMethods()
            .Select(method => method.Name)
            .Should().NotContain(new[]
            {
                "LockForBridgeAsync", "MintWrappedAsync", "BurnWrappedAsync",
                "ReleaseFromBridgeAsync", "VerifyBridgeProofAsync",
            });

        typeof(IBlockchainProvider).GetProperties()
            .Select(property => property.Name)
            .Should().NotContain("SupportsBridging",
                "a boolean that can be set true is exactly the lie the module seam removes");
    }

    // ─── In-process Algod stub (mirrors AlgorandProviderTransactTests) ───

    private StubScope RunStub(
        long confirmedRound,
        bool recipientOptedIn = true,
        ulong vaultUnits = 100,
        string? vaultAuthAddress = null,
        ulong assetTotal = 100,
        string? creatorAddress = null)
        => new(this, confirmedRound, recipientOptedIn, vaultUnits, vaultAuthAddress,
               assetTotal, creatorAddress ?? _platform.Address.EncodeAsString());

    private static HttpResponseMessage JsonResponse(Dictionary<string, object?> dict)
    {
        var content = new StringContent(JsonSerializer.Serialize(dict), System.Text.Encoding.UTF8);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class StubScope : HttpMessageHandler
    {
        private readonly BridgePrimitivesTests _owner;
        private readonly long _confirmedRound;
        private readonly bool _recipientOptedIn;
        private readonly ulong _vaultUnits;
        private readonly string? _vaultAuthAddress;
        private readonly ulong _assetTotal;
        private readonly string _creatorAddress;

        public StubScope(
            BridgePrimitivesTests owner, long confirmedRound, bool recipientOptedIn, ulong vaultUnits,
            string? vaultAuthAddress, ulong assetTotal, string creatorAddress)
        {
            _owner = owner;
            _confirmedRound = confirmedRound;
            _recipientOptedIn = recipientOptedIn;
            _vaultUnits = vaultUnits;
            _vaultAuthAddress = vaultAuthAddress;
            _assetTotal = assetTotal;
            _creatorAddress = creatorAddress;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/v2/transactions/params", StringComparison.Ordinal))
            {
                return JsonResponse(new Dictionary<string, object?>
                {
                    ["fee"] = 0,
                    ["min-fee"] = 1000,
                    ["last-round"] = 100,
                    ["genesis-id"] = "devnet-v1.0",
                    ["genesis-hash"] = "SGO1GKSzyE7IEPItTxCByw9x8FmnrCDexi9/cOUJOiI=",
                });
            }

            if (path == "/v2/transactions")
            {
                _owner._lastSubmittedBody = request.Content is null
                    ? Array.Empty<byte>()
                    : await request.Content.ReadAsByteArrayAsync(cancellationToken);
                Interlocked.Increment(ref _owner._submitCount);
                return JsonResponse(new Dictionary<string, object?> { ["txId"] = "STUBTXID" });
            }

            if (path.Contains("/v2/transactions/pending/", StringComparison.Ordinal))
            {
                return JsonResponse(new Dictionary<string, object?>
                {
                    ["confirmed-round"] = _confirmedRound,
                    ["asset-index"] = null,
                    ["pool-error"] = "",
                });
            }

            if (path.StartsWith($"/v2/assets/{LockedAsset}", StringComparison.Ordinal))
            {
                return JsonResponse(new Dictionary<string, object?>
                {
                    ["index"] = LockedAsset,
                    ["params"] = new Dictionary<string, object?>
                    {
                        ["creator"] = _creatorAddress,
                        ["total"] = _assetTotal,
                        ["decimals"] = 0,
                        ["default-frozen"] = false,
                    },
                });
            }

            if (path.StartsWith("/v2/accounts/", StringComparison.Ordinal))
                return JsonResponse(AccountFor(path["/v2/accounts/".Length..]));

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private Dictionary<string, object?> AccountFor(string address)
        {
            var isRecipient = string.Equals(
                address, _owner._recipient.Address.EncodeAsString(), StringComparison.Ordinal);
            var isPlatform = string.Equals(
                address, _owner._platform.Address.EncodeAsString(), StringComparison.Ordinal);

            // Every account in this stub holds LockedAsset only; the recipient's opt-in is the
            // knob a test flips to prove the release refuses an account that cannot receive.
            var holdings = new List<Dictionary<string, object?>>();
            if (!isRecipient || _recipientOptedIn)
            {
                holdings.Add(new Dictionary<string, object?>
                {
                    ["asset-id"] = LockedAsset,
                    ["amount"] = _vaultUnits,
                    ["is-frozen"] = false,
                });
            }

            var account = new Dictionary<string, object?>
            {
                ["amount"] = 10_000_000,
                ["assets"] = holdings,
            };

            if (isPlatform && !string.IsNullOrWhiteSpace(_vaultAuthAddress))
                account["auth-addr"] = _vaultAuthAddress;

            return account;
        }
    }
}
