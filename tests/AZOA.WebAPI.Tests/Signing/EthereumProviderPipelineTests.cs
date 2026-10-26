using System.Net;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nethereum.Model;
using Nethereum.Signer;
using Nethereum.Util;
using Xunit;

namespace AZOA.WebAPI.Tests.Signing;

/// <summary>
/// The Ethereum EIP-1559 build→sign→submit→confirm pipeline, driven against an in-process
/// JSON-RPC stub. Pins the invariants that make this path safe to put value on: the
/// signer's bytes are broadcast unmodified, the transaction id is the real keccak hash,
/// a confirmation timeout is PENDING and not a failure, a broadcast is never auto-retried,
/// an unknown transaction is never reported confirmed, a per-user context never falls back
/// to the platform key, and no wei ever transits a 64-bit integer.
/// </summary>
public class EthereumProviderPipelineTests
{
    private const string SepoliaChainId = "11155111";
    private static readonly BigInteger TwentyEtherInWei = BigInteger.Parse("20000000000000000000");

    private static readonly EthECKey PlatformKey = EthECKey.GenerateKey();
    private static string PlatformAddress => PlatformKey.GetPublicAddress();

    private const string Recipient = "0x5B38Da6a701c568545dCfcB03FcB875f56beddC4";
    private const string TokenContract = "0xdAC17F958D2ee523a2206206994597C13D831ec7";

    private readonly RecordingEthereumSigner _signer = new();
    private readonly FakeEthereumPreparationStore _preparations = new();

    private static IConfiguration BuildConfig(string? chainIdMetadata = SepoliaChainId) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZOA:WalletEncryptionKey"] = "unit-test-wallet-encryption-key-0123456789",
                ["AZOA:Ethereum:PlatformPrivateKey"] = "0x" + Convert.ToHexStringLower(PlatformKey.GetPrivateKeyAsBytes()),
                ["AZOA:Ethereum:PlatformTokenContract"] = TokenContract,
                ["AZOA:Ethereum:AllocatorActivationEpoch"] = "pipeline-fixture",
                // Keep the confirmation poll and RPC retry loops short so a pending-path
                // test does not spend half a minute sleeping.
                ["AZOA:Ethereum:ConfirmationPollAttempts"] = "2",
                ["AZOA:Ethereum:ConfirmationPollDelayMs"] = "1",
                ["AZOA:Ethereum:RpcRetryMaxAttempts"] = "0",
                ["AZOA:Ethereum:RpcRetryInitialDelayMs"] = "1",
                ["Blockchain:DefaultNetwork"] = "devnet",
                ["Blockchain:Chains:0:ChainType"] = "Ethereum",
                ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
                ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://ethereum.test/",
                ["Blockchain:Chains:0:Devnet:TimeoutMs"] = "2000",
                ["Blockchain:Chains:0:Devnet:ChainId"] = chainIdMetadata,
            })
            .Build();

    private EthereumProvider NewProvider(
        EthereumRpcStub stub,
        IKeyCustodyService? custody = null,
        bool withSigner = true,
        string? chainIdMetadata = SepoliaChainId)
    {
        _preparations.SeedCheckpoint("eip155:" + SepoliaChainId, PlatformAddress, "pipeline-fixture");
        ITransactionSignerFactory? signerFactory = withSigner
            ? new TransactionSignerFactory(new ITransactionSigner[] { _signer })
            : null;

        return new EthereumProvider(
            BuildConfig(chainIdMetadata),
            NullLogger<EthereumProvider>.Instance,
            signerFactory,
            custody,
            custodyScopeFactory: null,
            stub,
            preparationStore: _preparations);
    }

    /// <summary>The type-2 transaction the stub actually received, decoded from the wire bytes.</summary>
    private static Transaction1559 Broadcast(EthereumRpcStub stub) =>
        Transaction1559Encoder.Current.Decode(Convert.FromHexString(stub.LastSubmittedRaw![2..]));

    private static string CallDataOf(Transaction1559 transaction) =>
        string.IsNullOrEmpty(transaction.Data) ? "0x" // Nethereum decodes empty calldata as null
            : transaction.Data.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? transaction.Data : "0x" + transaction.Data;

    // ─── Native transfer ───

    [Fact]
    public async Task Transfer_confirmed_returns_the_real_hash_and_broadcasts_the_signers_bytes_unmodified()
    {
        using var stub = new EthereumRpcStub();
        var provider = NewProvider(stub);

        var result = await provider.TransferAsync(null!, PlatformAddress, Recipient, 1_000_000);

        result.IsError.Should().BeFalse(result.Message);
        stub.SendTransactionCount.Should().Be(1);

        // The canonical byte contract: whatever the signer returned is exactly what was
        // broadcast. Any re-encoding after signing would invalidate the signature.
        _signer.LastSignedOutput.Should().NotBeNull();
        stub.LastSubmittedRaw.Should().Be("0x" + Convert.ToHexStringLower(_signer.LastSignedOutput!));

        // The id is keccak256 over those exact bytes — never a generated one.
        result.Result.Should().Be(
            "0x" + Convert.ToHexStringLower(new Sha3Keccack().CalculateHash(_signer.LastSignedOutput!)));

        // ...and it recovers to the account the caller named as the sender.
        EthereumAddress.AreSame(
                TransactionVerificationAndRecovery.GetSenderAddress(Broadcast(stub)), PlatformAddress)
            .Should().BeTrue();
    }

    [Fact]
    public async Task Transfer_builds_a_type_two_transaction_with_eip1559_fee_fields()
    {
        using var stub = new EthereumRpcStub { BaseFeePerGas = 20_000_000_000, PriorityFee = 2_000_000_000 };
        var provider = NewProvider(stub);

        var result = await provider.TransferAsync("ETH", PlatformAddress, Recipient, 5);

        result.IsError.Should().BeFalse(result.Message);

        var broadcast = Broadcast(stub);
        broadcast.TransactionType.Should().Be(TransactionType.EIP1559);
        broadcast.MaxPriorityFeePerGas.Should().Be(new BigInteger(2_000_000_000));
        // maxFee = baseFee × 2 + tip, the standard headroom for the base-fee ramp.
        broadcast.MaxFeePerGas.Should().Be(new BigInteger(42_000_000_000));
        broadcast.ChainId.Should().Be(BigInteger.Parse(SepoliaChainId));
    }

    [Fact]
    public async Task Transfer_takes_its_gas_limit_from_the_node_estimate_and_never_hardcodes_one()
    {
        using var stub = new EthereumRpcStub { GasEstimate = 40_000 };
        var provider = NewProvider(stub);

        var result = await provider.TransferAsync(null!, PlatformAddress, Recipient, 5);

        result.IsError.Should().BeFalse(result.Message);
        stub.Methods.Should().Contain("eth_estimateGas");
        // 40 000 × the default 125% buffer.
        Broadcast(stub).GasLimit.Should().Be(new BigInteger(50_000));
    }

    [Fact]
    public async Task Transfer_refuses_before_broadcast_when_the_node_says_the_call_would_revert()
    {
        using var stub = new EthereumRpcStub { EstimateGasError = "execution reverted: insufficient funds" };
        var provider = NewProvider(stub);

        var result = await provider.TransferAsync(null!, PlatformAddress, Recipient, 5);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("execution reverted");
        stub.SendTransactionCount.Should().Be(0, "a transaction already known to fail must not burn its gas limit");
    }

    [Fact]
    public async Task Transfer_refuses_a_pre_1559_chain_rather_than_falling_back_to_a_legacy_gas_price()
    {
        using var stub = new EthereumRpcStub { HeadBlockHasBaseFee = false };
        var provider = NewProvider(stub);

        var result = await provider.TransferAsync(null!, PlatformAddress, Recipient, 5);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("baseFeePerGas");
        result.Message.Should().Contain("will not fall back to a legacy gasPrice");
        stub.SendTransactionCount.Should().Be(0);
    }

    [Fact]
    public async Task Transfer_confirmation_timeout_records_pending_with_the_real_hash_not_failure()
    {
        using var stub = new EthereumRpcStub { ReceiptFound = false, TransactionInMempool = true };
        var provider = NewProvider(stub);

        var result = await provider.TransferAsync(null!, PlatformAddress, Recipient, 5);

        // The transaction WAS broadcast. Reporting failure would strand a valid transfer
        // without a hash and invite a re-broadcast.
        result.IsError.Should().BeFalse("a confirmation timeout is not a failure");
        result.Message.Should().StartWith(OperationStatus.PendingConfirmationMarker);
        result.Result.Should().Be(
            "0x" + Convert.ToHexStringLower(new Sha3Keccack().CalculateHash(_signer.LastSignedOutput!)),
            "the pending marker must carry the REAL hash so reconciliation can settle it");
        stub.SendTransactionCount.Should().Be(1);
    }

    [Fact]
    public async Task Transfer_broadcast_failure_is_not_retried_double_spend_guard()
    {
        using var stub = new EthereumRpcStub { SendTransactionHttpStatus = 500 };
        var provider = NewProvider(stub);

        var result = await provider.TransferAsync(null!, PlatformAddress, Recipient, 5);

        result.IsError.Should().BeFalse();
        result.Message.Should().StartWith(OperationStatus.PendingConfirmationMarker);
        result.Result.Should().Be(_preparations.Snapshot.Single().TransactionId);
        stub.SendTransactionCount.Should().Be(
            1, "a post-send 5xx preserves the recorded wire for explicit reconciliation");
    }

    [Fact]
    public async Task Transfer_node_rejection_is_reported_without_a_resubmit()
    {
        using var stub = new EthereumRpcStub { SendTransactionRpcError = "nonce too low" };
        var provider = NewProvider(stub);

        var result = await provider.TransferAsync(null!, PlatformAddress, Recipient, 5);

        result.IsError.Should().BeFalse();
        result.Message.Should().StartWith(OperationStatus.PendingConfirmationMarker);
        result.Result.Should().Be(_preparations.Snapshot.Single().TransactionId);
        stub.SendTransactionCount.Should().Be(1);
    }

    [Fact]
    public async Task Transfer_refuses_when_the_node_echoes_a_hash_that_is_not_ours()
    {
        using var stub = new EthereumRpcStub
        {
            EchoedHashOverride = "0x" + new string('a', 64),
        };
        var provider = NewProvider(stub);

        var result = await provider.TransferAsync(null!, PlatformAddress, Recipient, 5);

        result.IsError.Should().BeFalse();
        result.Message.Should().StartWith(OperationStatus.PendingConfirmationMarker);
        result.Result.Should().Be(_preparations.Snapshot.Single().TransactionId).And.NotBe(stub.EchoedHashOverride);
        stub.SendTransactionCount.Should().Be(1);
    }

    [Fact]
    public async Task Transfer_reverted_on_chain_is_reported_as_an_error()
    {
        using var stub = new EthereumRpcStub { ReceiptStatus = 0 };
        var provider = NewProvider(stub);

        var result = await provider.TransferAsync(null!, PlatformAddress, Recipient, 5);

        result.IsError.Should().BeTrue("a transaction that landed and reverted is a real failure");
        result.Message.Should().Contain("reverted in a finalized block");
    }

    [Fact]
    public async Task Transfer_without_a_signer_fails_closed_before_broadcast()
    {
        using var stub = new EthereumRpcStub();
        var provider = NewProvider(stub, withSigner: false);

        var result = await provider.TransferAsync(null!, PlatformAddress, Recipient, 5);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("signer is unavailable");
        stub.SendTransactionCount.Should().Be(0);
    }

    [Fact]
    public async Task Transfer_refuses_when_the_node_chain_id_contradicts_the_configured_one()
    {
        using var stub = new EthereumRpcStub { ChainId = 1 }; // mainnet, while config pins Sepolia
        var provider = NewProvider(stub);

        var result = await provider.TransferAsync(null!, PlatformAddress, Recipient, 5);

        result.IsError.Should().BeTrue("the chain id is signed INSIDE the payload");
        result.Message.Should().Contain("Chain id mismatch");
        stub.SendTransactionCount.Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0x1")]
    [InlineData(" 11155111")]
    public async Task Transfer_requires_a_positive_explicit_network_pin_before_signing(string? pin)
    {
        using var stub = new EthereumRpcStub();
        var provider = NewProvider(stub, chainIdMetadata: pin);
        var result = await provider.TransferAsync(null!, PlatformAddress, Recipient, 5);
        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("per-network Ethereum ChainId");
        stub.SendTransactionCount.Should().Be(0);
    }

    // ─── Nonce ───

    [Fact]
    public async Task Transfer_uses_the_nodes_pending_nonce()
    {
        using var stub = new EthereumRpcStub { PendingNonce = 42 };
        var provider = NewProvider(stub);

        var result = await provider.TransferAsync(null!, PlatformAddress, Recipient, 5);

        result.IsError.Should().BeFalse(result.Message);
        stub.LastTransactionCountBlockTag.Should().Be("pending");
        Broadcast(stub).Nonce.Should().Be(new BigInteger(42));
    }

    [Fact]
    public async Task Concurrent_transfers_from_one_account_never_reuse_a_nonce()
    {
        // The stub's pending count deliberately never advances — exactly the node-lag
        // window in which two racing ops would otherwise both read nonce 7 and produce
        // two transactions of which only one can mine (a REPLACEMENT, not two transfers).
        using var stub = new EthereumRpcStub { PendingNonce = 7 };
        var provider = NewProvider(stub);
        var otherProvider = NewProvider(stub);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(index =>
                (index % 2 == 0 ? provider : otherProvider).TransferNativeAsync(PlatformAddress, Recipient, 5)));

        results.Should().OnlyContain(r => !r.IsError);
        stub.SubmittedRaws.Should().HaveCount(4);

        var nonces = stub.SubmittedRaws
            .Select(raw => Transaction1559Encoder.Current.Decode(Convert.FromHexString(raw[2..])).Nonce)
            .ToList();

        nonces.Should().OnlyHaveUniqueItems("a reused nonce replaces a pending transfer instead of adding one");
        nonces.Should().BeEquivalentTo(new BigInteger?[] { 7, 8, 9, 10 });
    }

    // ─── Custody / signing context ───

    [Fact]
    public async Task Transfer_per_user_context_signs_with_the_users_key_via_custody()
    {
        using var stub = new EthereumRpcStub();
        var userKey = EthECKey.GenerateKey();
        _preparations.SeedCheckpoint("eip155:" + SepoliaChainId, userKey.GetPublicAddress(), "pipeline-fixture");
        var userId = Guid.NewGuid();
        var walletId = Guid.NewGuid();

        var custody = new Mock<IKeyCustodyService>();
        custody.Setup(c => c.WithSigningKeyAsync(
                It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()))
            .Returns(async (SigningContext _, Func<byte[], Task<AZOAResult<byte[]>>> sign) =>
            {
                var inner = await sign(userKey.GetPrivateKeyAsBytes());
                return new AZOAResult<AZOAResult<byte[]>> { Result = inner };
            });
        custody.Setup(c => c.WithPlatformSigningKeyAsync(
                It.IsAny<bool>(), It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()))
            .ThrowsAsync(new InvalidOperationException("a per-user transfer must NOT resolve the platform key"));

        var provider = NewProvider(stub, custody.Object);

        var result = await provider.TransferAsync(
            null!, userKey.GetPublicAddress(), Recipient, 5, SigningContext.ForUser(userId, walletId));

        result.IsError.Should().BeFalse(result.Message);
        EthereumAddress.AreSame(
                TransactionVerificationAndRecovery.GetSenderAddress(Broadcast(stub)), userKey.GetPublicAddress())
            .Should().BeTrue("the user's own key is the sender and the fee payer");

        custody.Verify(c => c.WithSigningKeyAsync(
            It.Is<SigningContext>(x => x.WalletId == walletId && x.UserId == userId),
            It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()), Times.Once);
        custody.Verify(c => c.WithPlatformSigningKeyAsync(
            It.IsAny<bool>(), It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()), Times.Never);
    }

    [Fact]
    public async Task Transfer_per_user_context_without_custody_never_falls_back_to_the_platform_key()
    {
        using var stub = new EthereumRpcStub();
        var provider = NewProvider(stub); // the platform key IS configured; it must not be used

        var result = await provider.TransferAsync(
            null!, PlatformAddress, Recipient, 5, SigningContext.ForUser(Guid.NewGuid(), Guid.NewGuid()));

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Refusing to fall back to the platform key");
        stub.SendTransactionCount.Should().Be(0);
    }

    [Fact]
    public async Task Platform_context_through_the_algorand_only_custody_door_fails_closed_naming_that_cause()
    {
        using var stub = new EthereumRpcStub();

        // The shared custody platform door resolves an Algorand Ed25519 seed. It is ALSO
        // 32 bytes, so only the sender-binding check can catch it — a length test cannot.
        var algorandShapedKey = EthECKey.GenerateKey().GetPrivateKeyAsBytes();
        var custody = new Mock<IKeyCustodyService>();
        custody.Setup(c => c.WithPlatformSigningKeyAsync(
                It.IsAny<bool>(), It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()))
            .Returns(async (bool _, SigningContext _, Func<byte[], Task<AZOAResult<byte[]>>> sign) =>
            {
                var inner = await sign(algorandShapedKey);
                return new AZOAResult<AZOAResult<byte[]>> { Result = inner };
            });

        var provider = NewProvider(stub, custody.Object);

        var result = await provider.TransferAsync(null!, PlatformAddress, Recipient, 5);

        result.IsError.Should().BeTrue();
        result.Message.Should().ContainEquivalentOf("does not control " + PlatformAddress);
        result.Message.Should().Contain(KeyCustodyService.PlatformMnemonicConfigPath);
        result.Message.Should().Contain(EthereumProvider.PlatformPrivateKeyConfigPath);
        result.Message.Should().NotContain("stub", "the signer is complete; the message must name the real cause");
        stub.SendTransactionCount.Should().Be(0);
    }

    [Fact]
    public async Task Platform_key_of_the_wrong_length_fails_closed_naming_the_custody_door()
    {
        using var stub = new EthereumRpcStub();
        var custody = new Mock<IKeyCustodyService>();
        custody.Setup(c => c.WithPlatformSigningKeyAsync(
                It.IsAny<bool>(), It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()))
            .Returns(async (bool _, SigningContext _, Func<byte[], Task<AZOAResult<byte[]>>> sign) =>
            {
                var inner = await sign(new byte[64]);
                return new AZOAResult<AZOAResult<byte[]>> { Result = inner };
            });

        var provider = NewProvider(stub, custody.Object);

        var result = await provider.TransferAsync(null!, PlatformAddress, Recipient, 5);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("64 bytes").And.Contain("32-byte secp256k1 private key");
        stub.SendTransactionCount.Should().Be(0);
    }

    // ─── Amount boundary ───

    [Fact]
    public async Task Native_transfer_above_the_ulong_ceiling_is_encoded_exactly_through_the_biginteger_surface()
    {
        using var stub = new EthereumRpcStub();
        var provider = NewProvider(stub);

        // 20 ETH in wei cannot be expressed by the IBlockchainProvider `ulong` amount.
        // The BigInteger-native module carries it, and it must survive intact into the
        // signed bytes — no 64-bit hop anywhere in between.
        var result = await provider.TransferNativeAsync(PlatformAddress, Recipient, TwentyEtherInWei);

        result.IsError.Should().BeFalse(result.Message);
        Broadcast(stub).Amount.Should().Be(TwentyEtherInWei);
        TwentyEtherInWei.Should().BeGreaterThan(new BigInteger(ulong.MaxValue),
            "the point of the assertion is that this amount was unexpressible before D13");
    }

    [Fact]
    public async Task Transfer_above_the_old_ulong_ceiling_reaches_the_signer_intact_through_the_interface()
    {
        // D13 end to end: the amount now crosses the SHARED IBlockchainProvider surface —
        // not just the BigInteger-native module — and must arrive in the signed RLP whole.
        using var stub = new EthereumRpcStub();
        var provider = NewProvider(stub);

        var result = await provider.TransferAsync(
            null!, PlatformAddress, Recipient, RailAmount.OfChainAsset(TwentyEtherInWei));

        result.IsError.Should().BeFalse(result.Message);
        Broadcast(stub).Amount.Should().Be(TwentyEtherInWei,
            "no 64-bit hop may exist anywhere between the interface and the wire");
        _signer.LastCanonicalInput.Should().NotBeNull("the value path must have reached the real signer");
        Transaction1559Encoder.Current.Decode(_signer.LastSignedOutput!).Amount
            .Should().Be(TwentyEtherInWei, "the amount must survive into the SIGNED bytes");
    }

    [Fact]
    public async Task Transfer_refuses_a_fiat_denominated_amount_rather_than_reading_it_as_wei()
    {
        // D15: the dimension travels with the amount, so a fiat payout mistakenly routed at
        // a chain provider is refused instead of moving 500 wei.
        using var stub = new EthereumRpcStub();
        var provider = NewProvider(stub);

        var result = await provider.TransferAsync(
            null!, PlatformAddress, Recipient, RailAmount.OfUsdMinorUnits(500));

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("USD");
        result.Message.Should().Contain("does not settle");
        stub.SendTransactionCount.Should().Be(0, "a mis-denominated amount must never broadcast");
    }

    [Fact]
    public async Task GetBalance_reports_a_holding_above_the_old_ulong_ceiling_exactly()
    {
        using var stub = new EthereumRpcStub { NativeBalanceWei = TwentyEtherInWei };
        var provider = NewProvider(stub);

        var result = await provider.GetBalanceAsync(PlatformAddress);

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().Be("20000000000000000000", "the balance must not be truncated to fit a ulong");
        // There is no longer a ceiling to warn about: after D13 the shared interface can
        // MOVE this holding, so the old PENDING-INTERFACE-WIDENING note is gone by design.
        result.Message.Should().NotContain("PENDING-INTERFACE-WIDENING");
        result.Message.Should().Contain("20000000000000000000");
    }

    [Fact]
    public async Task GetBalance_reports_an_rpc_failure_as_an_error_not_a_zero_balance()
    {
        using var stub = new EthereumRpcStub { BalanceRpcError = "node is unavailable" };
        var provider = NewProvider(stub);

        var result = await provider.GetBalanceAsync(PlatformAddress);

        result.IsError.Should().BeTrue("an unreachable node is not an empty account");
        result.Result.Should().NotBe("0");
    }

    // ─── ERC-20 ───

    [Fact]
    public async Task Transfer_erc20_encodes_the_canonical_transfer_call_to_the_contract()
    {
        using var stub = new EthereumRpcStub();
        stub.ContractsWithCode.Add(TokenContract.ToLowerInvariant());
        var provider = NewProvider(stub);

        var result = await provider.TransferAsync(TokenContract, PlatformAddress, Recipient, 250_000);

        result.IsError.Should().BeFalse(result.Message);

        var broadcast = Broadcast(stub);
        EthereumAddress.AreSame(broadcast.ReceiverAddress, TokenContract)
            .Should().BeTrue("an ERC-20 transfer is a call to the token contract");
        broadcast.Amount.Should().Be(BigInteger.Zero, "no ether moves in an ERC-20 transfer");
        CallDataOf(broadcast).Should().Be(
            EthereumAbi.EncodeCall(EthereumAbi.TransferSignature, Recipient, new BigInteger(250_000)));
        CallDataOf(broadcast).Should().StartWith("0xa9059cbb");
    }

    [Fact]
    public async Task Transfer_erc20_fails_closed_when_the_contract_address_holds_no_code()
    {
        // A call to a codeless address SUCCEEDS and does nothing — this would otherwise
        // report a token movement that never happened.
        using var stub = new EthereumRpcStub(); // no contracts registered
        var provider = NewProvider(stub);

        var result = await provider.TransferAsync(TokenContract, PlatformAddress, Recipient, 250_000);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("holds no code");
        stub.SendTransactionCount.Should().Be(0);
    }

    [Fact]
    public async Task Mint_calls_mint_on_the_contract_named_by_the_token_uri()
    {
        using var stub = new EthereumRpcStub();
        stub.ContractsWithCode.Add(TokenContract.ToLowerInvariant());
        var provider = NewProvider(stub);

        var result = await provider.MintAsync(TokenContract, 500, "BADGE", Recipient);

        result.IsError.Should().BeFalse(result.Message);
        CallDataOf(Broadcast(stub)).Should().Be(
            EthereumAbi.EncodeCall(EthereumAbi.MintSignature, Recipient, new BigInteger(500)));
        EthereumAddress.AreSame(
                TransactionVerificationAndRecovery.GetSenderAddress(Broadcast(stub)), PlatformAddress)
            .Should().BeTrue("the platform account holds the minter authority");
        result.Message.Should().Contain("BADGE");
    }

    [Fact]
    public async Task Mint_fails_closed_when_the_contract_has_no_mint_entry_point()
    {
        using var stub = new EthereumRpcStub { EstimateGasError = "execution reverted" };
        stub.ContractsWithCode.Add(TokenContract.ToLowerInvariant());
        var provider = NewProvider(stub);

        var result = await provider.MintAsync(TokenContract, 500, "BADGE", Recipient);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("execution reverted");
        stub.SendTransactionCount.Should().Be(0);
    }

    [Fact]
    public async Task Mint_without_any_resolvable_contract_fails_closed_and_invents_no_token_id()
    {
        using var stub = new EthereumRpcStub();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:DefaultNetwork"] = "devnet",
                ["Blockchain:Chains:0:ChainType"] = "Ethereum",
                ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
                ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://ethereum.test/",
            })
            .Build();

        var provider = new EthereumProvider(
            config, NullLogger<EthereumProvider>.Instance,
            new TransactionSignerFactory(new ITransactionSigner[] { _signer }),
            custodyService: null, custodyScopeFactory: null, stub);

        var result = await provider.MintAsync("ipfs://metadata.json", 500, "BADGE", Recipient);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("no chain-native asset-creation primitive");
        result.Message.Should().Contain(EthereumProvider.PlatformTokenContractConfigPath);
        stub.SendTransactionCount.Should().Be(0);
    }

    [Fact]
    public async Task Burn_calls_burn_uint256_from_the_holder()
    {
        using var stub = new EthereumRpcStub();
        stub.ContractsWithCode.Add(TokenContract.ToLowerInvariant());
        var provider = NewProvider(stub);

        var result = await provider.BurnAsync(TokenContract, 25, PlatformAddress);

        result.IsError.Should().BeFalse(result.Message);
        CallDataOf(Broadcast(stub)).Should().Be(
            EthereumAbi.EncodeCall(EthereumAbi.BurnSignature, new BigInteger(25)));
    }

    [Fact]
    public async Task GetErc20Balance_decodes_the_uint256_return()
    {
        using var stub = new EthereumRpcStub();
        stub.ContractsWithCode.Add(TokenContract.ToLowerInvariant());
        stub.CallReturns[EthereumAbi.Selector(EthereumAbi.BalanceOfSignature)] = Uint256(TwentyEtherInWei);
        var provider = NewProvider(stub);

        var result = await provider.GetBalanceAsync(PlatformAddress, TokenContract);

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().Be("20000000000000000000");
    }

    // ─── Status / confirmation ───

    [Fact]
    public async Task GetTransactionStatus_never_reports_an_unknown_transaction_as_successful()
    {
        using var stub = new EthereumRpcStub { ReceiptFound = false, TransactionInMempool = false };
        var provider = NewProvider(stub);

        var hash = "0x" + new string('b', 64);
        var result = await provider.GetTransactionStatusAsync(hash);

        // `receipt?.status == null` is vacuously true when there is no receipt at all, so
        // a success payload here would classify an unseen transaction as CONFIRMED.
        result.IsError.Should().BeTrue();
        ChainTxClassifier.Classify(result).Should().Be(ChainConfirmation.Unknown);
        (await provider.GetTransactionConfirmationAsync(hash)).Result.Should().Be(ChainConfirmation.Unknown);
    }

    [Fact]
    public async Task GetTransactionStatus_treats_a_receipt_without_a_status_field_as_unknown()
    {
        // A pre-Byzantium node or a trimmed proxy response cannot distinguish success from
        // revert. Guessing "success" here would settle a reverted transfer.
        using var stub = new EthereumRpcStub { ReceiptOmitsStatus = true };
        var provider = NewProvider(stub);
        stub.KnownHashes.Add("0x" + new string('c', 64));

        var result = await provider.GetTransactionStatusAsync("0x" + new string('c', 64));

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("no readable `status` field");
        ChainTxClassifier.Classify(result).Should().Be(ChainConfirmation.Unknown);
    }

    [Fact]
    public async Task GetTransactionConfirmation_separates_an_in_flight_transaction_from_an_unknown_one()
    {
        using var stub = new EthereumRpcStub { ReceiptFound = false, TransactionInMempool = true };
        var provider = NewProvider(stub);

        var hash = "0x" + new string('d', 64);
        var confirmation = await provider.GetTransactionConfirmationAsync(hash);
        var status = await provider.GetTransactionStatusAsync(hash);

        confirmation.Result.Should().Be(ChainConfirmation.Pending, "a mempool transaction is safe to wait on");
        status.IsError.Should().BeFalse();
        status.Result.Should().NotContainKey("success", "a pending transaction has no outcome yet");
        ChainTxClassifier.Classify(status).Should().Be(ChainConfirmation.Pending);
    }

    [Fact]
    public async Task GetTransactionConfirmation_reports_a_reverted_receipt_as_failed_on_chain()
    {
        using var stub = new EthereumRpcStub { ReceiptStatus = 0 };
        var provider = NewProvider(stub);
        var hash = "0x" + new string('e', 64);
        stub.KnownHashes.Add(hash);

        var confirmation = await provider.GetTransactionConfirmationAsync(hash);
        var status = await provider.GetTransactionStatusAsync(hash);

        confirmation.Result.Should().Be(ChainConfirmation.FailedOnChain);
        ChainTxClassifier.Classify(status).Should().Be(ChainConfirmation.FailedOnChain);
    }

    [Fact]
    public async Task GetTransactionConfirmation_folds_an_rpc_failure_into_unknown()
    {
        using var stub = new EthereumRpcStub { ReceiptRpcError = "node is unavailable" };
        var provider = NewProvider(stub);

        var confirmation = await provider.GetTransactionConfirmationAsync("0x" + new string('f', 64));

        confirmation.Result.Should().Be(
            ChainConfirmation.Unknown, "a flaky RPC must never look like a confirmed failure");
    }

    // ─── Address validation ───

    [Fact]
    public async Task ValidateAddress_rejects_a_mixed_case_address_with_a_broken_checksum()
    {
        using var stub = new EthereumRpcStub();
        var provider = NewProvider(stub);

        var result = await provider.ValidateAddressAsync("0x5B38Da6a701c568545dCfcB03FcB875f56beddC5");

        result.IsError.Should().BeTrue();
        result.Result.Should().BeFalse();
        result.Message.Should().Contain("EIP-55");
    }

    [Fact]
    public async Task ValidateAddress_accepts_a_case_free_address_and_reports_its_checksummed_form()
    {
        using var stub = new EthereumRpcStub();
        var provider = NewProvider(stub);

        var result = await provider.ValidateAddressAsync(Recipient.ToLowerInvariant());

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().BeTrue();
        result.Message.Should().Contain(Recipient);
    }

    // ─── Metadata / enumeration ───

    [Fact]
    public async Task GetTokenMetadata_reads_the_erc20_surface()
    {
        using var stub = new EthereumRpcStub();
        stub.ContractsWithCode.Add(TokenContract.ToLowerInvariant());
        stub.CallReturns[EthereumAbi.Selector(EthereumAbi.NameSignature)] = AbiString("Tether USD");
        stub.CallReturns[EthereumAbi.Selector(EthereumAbi.SymbolSignature)] = AbiString("USDT");
        stub.CallReturns[EthereumAbi.Selector(EthereumAbi.DecimalsSignature)] = Uint256(6);
        stub.CallReturns[EthereumAbi.Selector(EthereumAbi.TotalSupplySignature)] = Uint256(TwentyEtherInWei);
        var provider = NewProvider(stub);

        var result = await provider.GetTokenMetadataAsync(TokenContract);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!["name"].Should().Be("Tether USD");
        result.Result["symbol"].Should().Be("USDT");
        result.Result["decimals"].Should().Be(6);
        result.Result["totalSupply"].Should().Be("20000000000000000000");
    }

    [Fact]
    public async Task GetTokenMetadata_refuses_an_address_with_no_code()
    {
        using var stub = new EthereumRpcStub();
        var provider = NewProvider(stub);

        var result = await provider.GetTokenMetadataAsync(TokenContract);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("holds no code");
    }

    [Fact]
    public async Task GetTokensByOwner_fails_closed_because_ethereum_has_no_holdings_index()
    {
        using var stub = new EthereumRpcStub();
        var provider = NewProvider(stub);

        var result = await provider.GetTokensByOwnerAsync(PlatformAddress);

        result.IsError.Should().BeTrue("an empty list would be mistaken for 'this account holds nothing'");
        result.Message.Should().Contain("no chain-native index");
        result.Message.Should().NotContain("signer", "the refusal is about indexing, not signing");
    }

    // ─── Contracts ───

    [Fact]
    public async Task Explicit_read_only_contract_query_decodes_declared_outputs_without_broadcast()
    {
        using var stub = new EthereumRpcStub();
        stub.CallReturns[EthereumAbi.Selector(EthereumAbi.BalanceOfSignature)] = Uint256(TwentyEtherInWei);
        var provider = NewProvider(stub);
        var result = await provider.QueryContractAsync(new(TokenContract, EthereumAbi.BalanceOfSignature,
            [JsonSerializer.SerializeToElement(PlatformAddress)], ["uint256"], PlatformAddress));
        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().BeOfType<Dictionary<string, object>>().Subject["decoded"]
            .Should().BeEquivalentTo(new List<object?> { "20000000000000000000" });
        stub.SendTransactionCount.Should().Be(0);
        provider.TryGetModule<INativeOperationModule>(out _).Should().BeTrue();
        provider.TryGetModule<IReadOnlyContractModule>(out _).Should().BeTrue();
    }

    [Fact]
    public async Task Querying_a_write_signature_only_simulates_and_never_allocates_a_signer()
    {
        using var stub = new EthereumRpcStub();
        var provider = NewProvider(stub);
        var result = await provider.QueryContractAsync(new(TokenContract, EthereumAbi.TransferSignature,
            [JsonSerializer.SerializeToElement(Recipient), JsonSerializer.SerializeToElement("1")], [], PlatformAddress));
        result.IsError.Should().BeFalse(result.Message);
        stub.SendTransactionCount.Should().Be(0);
    }

    // ─── Bridge capability is ABSENT, which is the whole refusal ───

    [Fact]
    public void Bridge_legs_are_absent_so_there_is_no_primitive_to_fabricate_success_with()
    {
        using var stub = new EthereumRpcStub();
        var provider = NewProvider(stub);

        // No refusal body exists to regress into an Ok: the capability is simply not
        // implemented, so nothing on this provider can lock, mint, burn, or release.
        provider.TryGetModule<IBridgeSourceLeg>(out _).Should().BeFalse(
            "no reviewed Ethereum bridge lifecycle exists");
        provider.TryGetModule<IBridgeTargetLeg>(out _).Should().BeFalse(
            "no canonical wrapped-token contract or mint authority exists");
        stub.SendTransactionCount.Should().Be(0);
    }

    [Fact]
    public void Bridge_absence_is_a_lifecycle_gap_and_never_blames_the_signer()
    {
        // The EIP-1559 signer is complete. Proving the value pipeline still signs and submits
        // is what keeps "no bridge" from being misread as "no signer".
        using var stub = new EthereumRpcStub();
        var provider = NewProvider(stub);

        provider.TryGetModule<IEthereumValueModule>(out var valueModule).Should().BeTrue(
            "the real value surface is unaffected by the absent bridge capability");
        valueModule.Should().NotBeNull();
    }

    [Fact]
    public void Provider_does_not_advertise_a_faucet()
    {
        using var stub = new EthereumRpcStub();
        var provider = NewProvider(stub);

        provider.SupportsFaucet.Should().BeFalse();
    }

    [Fact]
    public async Task Faucet_fails_closed_with_the_external_faucet_reason()
    {
        using var stub = new EthereumRpcStub();
        var provider = NewProvider(stub);

        var result = await provider.DispenseFromFaucetAsync(Recipient, 1m);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("external third-party faucets");
    }

    // ─── Capability module ───

    [Fact]
    public void Provider_exposes_the_biginteger_native_value_module()
    {
        using var stub = new EthereumRpcStub();
        var provider = NewProvider(stub);

        provider.TryGetModule<IEthereumValueModule>(out var module).Should().BeTrue();
        module!.CapabilityName.Should().Be("Ethereum.Value");
        module.ChainType.Should().Be("Ethereum");
    }

    // ─── Container wiring ───

    [Fact]
    public void Container_selects_the_constructor_that_wires_a_real_signer()
    {
        // Registration is a bare AddTransient<EthereumProvider>(), so constructor selection
        // decides whether the value path can sign at all. It must also stay resolvable from
        // the ROOT provider: BlockchainProviderFactory is a singleton caching one provider
        // per chain/network, so a scoped IKeyCustodyService parameter would be captive.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(BuildConfig());
        services.AddSingleton<ITransactionSignerFactory>(
            new TransactionSignerFactory(new ITransactionSigner[] { new EthereumTransactionSigner() }));
        services.AddScoped(_ => new Mock<IKeyCustodyService>().Object);
        services.AddTransient<EthereumProvider>();

        using var root = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        var provider = root.GetRequiredService<EthereumProvider>();

        typeof(EthereumProvider)
            .GetField("_signerFactory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(provider)
            .Should().NotBeNull("a container-built provider that cannot sign fails every value op");

        typeof(EthereumProvider)
            .GetField("_custodyScopeFactory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(provider)
            .Should().NotBeNull("custody must be resolvable per signing op");
    }

    // ─── Test doubles ───

    private static string Uint256(BigInteger value) =>
        "0x" + Convert.ToHexStringLower(value.ToByteArray(isUnsigned: true, isBigEndian: true)).PadLeft(64, '0');

    private static string AbiString(string text) =>
        "0x" + Convert.ToHexStringLower(
            new Nethereum.ABI.ABIEncode().GetABIEncoded(new Nethereum.ABI.ABIValue("string", text)));

    /// <summary>Wraps the real signer so a test can assert on the exact bytes in and out.</summary>
    private sealed class RecordingEthereumSigner : ITransactionSigner
    {
        private readonly EthereumTransactionSigner _inner = new();

        public byte[]? LastCanonicalInput { get; private set; }
        public byte[]? LastSignedOutput { get; private set; }

        public string ChainType => "Ethereum";

        public AZOAResult<byte[]> Sign(byte[] canonicalTxn, SigningKeyMaterial key)
        {
            LastCanonicalInput = (byte[])canonicalTxn.Clone();
            var result = _inner.Sign(canonicalTxn, key);
            if (!result.IsError && result.Result is not null)
                LastSignedOutput = (byte[])result.Result.Clone();
            return result;
        }
    }

    /// <summary>In-process Ethereum JSON-RPC stub — no node is ever contacted.</summary>
    private sealed class EthereumRpcStub : HttpMessageHandler
    {
        public BigInteger ChainId { get; set; } = BigInteger.Parse(SepoliaChainId);
        public BigInteger BaseFeePerGas { get; set; } = 20_000_000_000;
        public BigInteger PriorityFee { get; set; } = 1_500_000_000;
        public BigInteger GasEstimate { get; set; } = 21_000;
        public BigInteger PendingNonce { get; set; } = 7;
        public BigInteger NativeBalanceWei { get; set; } = 1_000_000_000_000_000_000;
        public BigInteger BlockNumber { get; set; } = 4_242;
        public BigInteger ReceiptStatus { get; set; } = 1;
        private static string BlockHash => "0x" + new string('b', 64);

        public bool HeadBlockHasBaseFee { get; set; } = true;
        public bool ReceiptFound { get; set; } = true;
        public bool ReceiptOmitsStatus { get; set; }
        public bool TransactionInMempool { get; set; }
        public string? DeployedContractAddress { get; set; }

        public string? EstimateGasError { get; set; }
        public string? BalanceRpcError { get; set; }
        public string? ReceiptRpcError { get; set; }
        public string? SendTransactionRpcError { get; set; }
        public int? SendTransactionHttpStatus { get; set; }
        public string? EchoedHashOverride { get; set; }

        public HashSet<string> ContractsWithCode { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> CallReturns { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> KnownHashes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Methods { get; } = new();
        public List<string> SubmittedRaws { get; } = new();
        public int SendTransactionCount { get; private set; }
        public string? LastSubmittedRaw { get; private set; }
        public string? LastTransactionCountBlockTag { get; private set; }

        private readonly object _gate = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? "{}"
                : await request.Content.ReadAsStringAsync(cancellationToken);

            string method;
            int id;
            JsonElement parameters;
            using (var document = JsonDocument.Parse(body))
            {
                method = document.RootElement.GetProperty("method").GetString() ?? string.Empty;
                id = document.RootElement.GetProperty("id").GetInt32();
                parameters = document.RootElement.GetProperty("params").Clone();
            }

            lock (_gate)
                Methods.Add(method);

            string StringParam(int index) =>
                parameters.GetArrayLength() > index && parameters[index].ValueKind == JsonValueKind.String
                    ? parameters[index].GetString() ?? string.Empty
                    : string.Empty;

            switch (method)
            {
                case "eth_chainId":
                    return Result(id, Quantity(ChainId));

                case "eth_blockNumber":
                    return Result(id, Quantity(BlockNumber));

                case "eth_getBalance":
                    return BalanceRpcError is not null
                        ? RpcError(id, BalanceRpcError)
                        : Result(id, Quantity(NativeBalanceWei));

                case "eth_getTransactionCount":
                    LastTransactionCountBlockTag = StringParam(1);
                    return Result(id, Quantity(PendingNonce));

                case "eth_getBlockByNumber":
                    return Result(id, HeadBlockHasBaseFee
                        ? new Dictionary<string, object?>
                        {
                            ["number"] = Quantity(BlockNumber),
                            ["hash"] = BlockHash,
                            ["baseFeePerGas"] = Quantity(BaseFeePerGas),
                        }
                        : new Dictionary<string, object?> { ["number"] = Quantity(BlockNumber), ["hash"] = BlockHash });

                case "eth_maxPriorityFeePerGas":
                    return Result(id, Quantity(PriorityFee));

                case "eth_estimateGas":
                    return EstimateGasError is not null
                        ? RpcError(id, EstimateGasError)
                        : Result(id, Quantity(GasEstimate));

                case "eth_getCode":
                    return Result(id, ContractsWithCode.Contains(StringParam(0)) ? "0x60806040" : "0x");

                case "eth_call":
                {
                    var data = parameters[0].TryGetProperty("data", out var dataElement)
                        ? dataElement.GetString() ?? "0x"
                        : "0x";
                    var selector = data.Length >= 10 ? data[2..10] : string.Empty;
                    return Result(id, CallReturns.TryGetValue(selector, out var returnData) ? returnData : "0x");
                }

                case "eth_sendRawTransaction":
                {
                    lock (_gate)
                    {
                        SendTransactionCount++;
                        LastSubmittedRaw = StringParam(0);
                        // A JSON-RPC refusal means the node never admitted the bytes; a lost HTTP response may have.
                        if (SendTransactionRpcError is null)
                        {
                            SubmittedRaws.Add(LastSubmittedRaw);
                            // Keep the pending nonce fixed to simulate node lag.
                            KnownHashes.Add(HashOf(LastSubmittedRaw));
                        }
                    }

                    if (SendTransactionHttpStatus.HasValue)
                        return new HttpResponseMessage((HttpStatusCode)SendTransactionHttpStatus.Value);
                    if (SendTransactionRpcError is not null)
                        return RpcError(id, SendTransactionRpcError);

                    return Result(id, EchoedHashOverride ?? HashOf(LastSubmittedRaw!));
                }

                case "eth_getTransactionReceipt":
                {
                    if (ReceiptRpcError is not null)
                        return RpcError(id, ReceiptRpcError);

                    var hash = StringParam(0);
                    lock (_gate)
                    {
                        if (!ReceiptFound || !KnownHashes.Contains(hash))
                            return Result(id, null);
                    }

                    var receipt = new Dictionary<string, object?>
                    {
                        ["transactionHash"] = hash,
                        ["blockHash"] = BlockHash,
                        ["blockNumber"] = Quantity(BlockNumber),
                        ["gasUsed"] = Quantity(GasEstimate),
                    };
                    if (!ReceiptOmitsStatus)
                        receipt["status"] = Quantity(ReceiptStatus);
                    if (DeployedContractAddress is not null)
                        receipt["contractAddress"] = DeployedContractAddress;

                    return Result(id, receipt);
                }

                case "eth_getTransactionByHash":
                {
                    string? raw;
                    lock (_gate) raw = SubmittedRaws.FirstOrDefault(candidate => string.Equals(HashOf(candidate), StringParam(0), StringComparison.OrdinalIgnoreCase));
                    if (raw is not null)
                    {
                        var transaction = Transaction1559Encoder.Current.Decode(Convert.FromHexString(raw[2..]));
                        return Result(id, new Dictionary<string, object?>
                        {
                            ["hash"] = HashOf(raw),
                            ["from"] = TransactionVerificationAndRecovery.GetSenderAddress(transaction),
                            ["to"] = string.IsNullOrEmpty(transaction.ReceiverAddress) ? null : transaction.ReceiverAddress,
                            ["nonce"] = Quantity(transaction.Nonce ?? BigInteger.Zero),
                            ["value"] = Quantity(transaction.Amount ?? BigInteger.Zero),
                            ["input"] = CallDataOf(transaction),
                            ["chainId"] = Quantity(transaction.ChainId),
                            ["blockNumber"] = ReceiptFound ? Quantity(BlockNumber) : null,
                            ["blockHash"] = ReceiptFound ? BlockHash : null,
                        });
                    }
                    return Result(id, TransactionInMempool
                        ? new Dictionary<string, object?> { ["hash"] = StringParam(0), ["blockNumber"] = null }
                        : null);
                }

                default:
                    return RpcError(id, $"unstubbed method {method}");
            }
        }

        /// <summary>The hash the node echoes back: keccak-256 over the submitted bytes.</summary>
        private static string HashOf(string rawTransactionHex) =>
            "0x" + Convert.ToHexStringLower(
                new Sha3Keccack().CalculateHash(Convert.FromHexString(rawTransactionHex[2..])));

        private static string Quantity(BigInteger value) =>
            value.IsZero
                ? "0x0"
                : "0x" + Convert.ToHexStringLower(value.ToByteArray(isUnsigned: true, isBigEndian: true)).TrimStart('0');

        private static HttpResponseMessage Result(int id, object? result) =>
            Envelope(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });

        private static HttpResponseMessage RpcError(int id, string message) =>
            Envelope(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new Dictionary<string, object?> { ["code"] = -32000, ["message"] = message },
            });

        private static HttpResponseMessage Envelope(Dictionary<string, object?> payload)
        {
            var content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
