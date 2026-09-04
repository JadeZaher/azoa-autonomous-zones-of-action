using System.Net;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using Solnet.Programs;
using Solnet.Rpc.Models;
using Solnet.Wallet;
using Solnet.Wallet.Bip39;
using Xunit;
using SolanaAccount = Solnet.Wallet.Account;
using SolanaWallet = Solnet.Wallet.Wallet;

namespace AZOA.WebAPI.Tests.Signing;

/// <summary>
/// The Solana SPL build→sign→submit→confirm pipeline, driven against an in-process
/// JSON-RPC stub. Pins the invariants that make this path safe to put value on:
/// the signer's bytes are submitted unmodified, a missing recipient token account is
/// created rather than silently skipped, a confirmation timeout is PENDING and not a
/// failure, a broadcast is never auto-retried, and nothing reports success without a
/// real on-chain signature.
/// </summary>
public class SolanaProviderSplPipelineTests
{
    private static readonly Mnemonic PlatformMnemonic = new(WordList.English, WordCount.Twelve);
    private static readonly SolanaWallet PlatformWallet = new(PlatformMnemonic.ToString(), WordList.English);

    private static PublicKey PlatformKey => PlatformWallet.Account.PublicKey;
    private static string PlatformAddress => PlatformWallet.Account.PublicKey.Key;

    private readonly RecordingSolanaSigner _signer = new();
    private readonly PublicKey _mint = new SolanaAccount().PublicKey;
    private readonly PublicKey _recipient = new SolanaAccount().PublicKey;

    private static IConfiguration BuildConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AZOA:WalletEncryptionKey"] = "unit-test-wallet-encryption-key-0123456789",
            ["AZOA:Solana:PlatformMnemonic"] = PlatformMnemonic.ToString(),
            // Keep the confirmation poll and RPC retry loops short so a pending-path
            // test does not spend ten seconds sleeping.
            ["AZOA:Solana:ConfirmationPollAttempts"] = "2",
            ["AZOA:Solana:ConfirmationPollDelayMs"] = "1",
            ["AZOA:Solana:RpcRetryMaxAttempts"] = "0",
            ["AZOA:Solana:RpcRetryInitialDelayMs"] = "1",
            ["Blockchain:DefaultNetwork"] = "devnet",
            ["Blockchain:Chains:0:ChainType"] = "Solana",
            ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
            ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://solana.test/",
            ["Blockchain:Chains:0:Devnet:TimeoutMs"] = "2000",
        })
        .Build();

    private SolanaProvider NewProvider(
        SolanaRpcStub stub, IKeyCustodyService? custody = null, bool withSigner = true)
    {
        ITransactionSignerFactory? signerFactory = withSigner
            ? new TransactionSignerFactory(new ITransactionSigner[] { _signer })
            : null;

        return new SolanaProvider(
            BuildConfig(),
            NullLogger<SolanaProvider>.Instance,
            signerFactory,
            custody,
            custodyScopeFactory: null,
            stub);
    }

    private static PublicKey Ata(PublicKey owner, PublicKey mint) =>
        AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(owner, mint);

    /// <summary>A stub with both sides of a platform→recipient transfer already funded.</summary>
    private SolanaRpcStub StubWithBothTokenAccounts()
    {
        var stub = new SolanaRpcStub();
        stub.ExistingAccounts.Add(Ata(PlatformKey, _mint).Key);
        stub.ExistingAccounts.Add(Ata(_recipient, _mint).Key);
        return stub;
    }

    private Message SignedMessage() =>
        Message.Deserialize(_signer.LastCanonicalInput!);

    private static List<string> ProgramIdsOf(Message message) =>
        message.Instructions.Select(i => message.AccountKeys[i.ProgramIdIndex].Key).ToList();

    // ─── Transfer ───

    [Fact]
    public async Task Transfer_confirmed_returns_the_real_signature_and_submits_the_signers_bytes_unmodified()
    {
        using var stub = StubWithBothTokenAccounts();
        var provider = NewProvider(stub);

        var result = await provider.TransferAsync(_mint.Key, PlatformAddress, _recipient.Key, 25);

        result.IsError.Should().BeFalse(result.Message);
        stub.SendTransactionCount.Should().Be(1);

        // The canonical byte contract: whatever the signer returned is exactly what was
        // broadcast. Any re-serialization after signing would invalidate the signature.
        _signer.LastSignedOutput.Should().NotBeNull();
        stub.LastSubmittedBase64.Should().Be(Convert.ToBase64String(_signer.LastSignedOutput!));

        // The id is the signature inside those bytes — never a generated one.
        var submitted = Transaction.Deserialize(Convert.FromBase64String(stub.LastSubmittedBase64!));
        submitted.VerifySignatures().Should().BeTrue("the broadcast bytes must carry a valid signature");
        result.Result.Should().Be(
            Solnet.Wallet.Utilities.Encoders.Base58.EncodeData(submitted.Signatures[0].Signature));
    }

    [Fact]
    public async Task Transfer_signs_the_canonical_message_with_the_fee_payer_first()
    {
        using var stub = StubWithBothTokenAccounts();
        var provider = NewProvider(stub);

        var result = await provider.TransferAsync(_mint.Key, PlatformAddress, _recipient.Key, 1);

        result.IsError.Should().BeFalse(result.Message);

        // The signer's input must be a serialized MESSAGE (not a full transaction) whose
        // first account key is the fee payer — the shape SolanaTransactionSigner demands.
        var message = SignedMessage();
        message.AccountKeys[0].Key.Should().Be(PlatformAddress);
        message.RecentBlockhash.Should().NotBeNullOrWhiteSpace("the blockhash is signed as part of the message");
    }

    [Fact]
    public async Task Transfer_creates_the_recipient_token_account_when_it_is_missing()
    {
        using var stub = new SolanaRpcStub();
        stub.ExistingAccounts.Add(Ata(PlatformKey, _mint).Key); // sender funded, recipient not
        var provider = NewProvider(stub);

        var result = await provider.TransferAsync(_mint.Key, PlatformAddress, _recipient.Key, 7);

        result.IsError.Should().BeFalse(result.Message);

        // A transfer into a non-existent associated token account does not create one
        // implicitly — without this instruction the value would be lost.
        ProgramIdsOf(SignedMessage()).Should().Equal(
            AssociatedTokenAccountProgram.ProgramIdKey.Key,
            TokenProgram.ProgramIdKey.Key);
        SignedMessage().AccountKeys.Select(k => k.Key)
            .Should().Contain(Ata(_recipient, _mint).Key, "the derived recipient account must be in the transaction");
    }

    [Fact]
    public async Task Transfer_skips_the_create_instruction_when_the_recipient_account_exists()
    {
        using var stub = StubWithBothTokenAccounts();
        var provider = NewProvider(stub);

        var result = await provider.TransferAsync(_mint.Key, PlatformAddress, _recipient.Key, 7);

        result.IsError.Should().BeFalse(result.Message);
        ProgramIdsOf(SignedMessage()).Should().Equal(TokenProgram.ProgramIdKey.Key);
    }

    [Fact]
    public async Task Transfer_fails_closed_when_the_sender_has_no_token_account()
    {
        using var stub = new SolanaRpcStub();
        stub.ExistingAccounts.Add(Ata(_recipient, _mint).Key); // recipient only
        var provider = NewProvider(stub);

        var result = await provider.TransferAsync(_mint.Key, PlatformAddress, _recipient.Key, 7);

        result.IsError.Should().BeTrue("there is nothing to send from");
        result.Message.Should().Contain("no associated token account");
        stub.SendTransactionCount.Should().Be(0, "a doomed transfer must never reach the network");
    }

    [Fact]
    public async Task Transfer_confirmation_timeout_records_pending_not_failure()
    {
        using var stub = StubWithBothTokenAccounts();
        stub.SignatureFound = false; // broadcast succeeds; the cluster never reports a status
        var provider = NewProvider(stub);

        var result = await provider.TransferAsync(_mint.Key, PlatformAddress, _recipient.Key, 3);

        // The transaction WAS broadcast. Reporting failure would strand a valid transfer
        // without a TxHash and invite a re-broadcast.
        result.IsError.Should().BeFalse("a confirmation timeout is not a failure");
        result.Message.Should().StartWith(OperationStatus.PendingConfirmationMarker);
        stub.SendTransactionCount.Should().Be(1);

        var submitted = Transaction.Deserialize(Convert.FromBase64String(stub.LastSubmittedBase64!));
        result.Result.Should().Be(
            Solnet.Wallet.Utilities.Encoders.Base58.EncodeData(submitted.Signatures[0].Signature),
            "the pending marker must carry the REAL signature so reconciliation can settle it");
    }

    [Fact]
    public async Task Transfer_broadcast_failure_is_not_retried_double_spend_guard()
    {
        using var stub = StubWithBothTokenAccounts();
        stub.SendTransactionHttpStatus = 500;
        var provider = NewProvider(stub);

        var result = await provider.TransferAsync(_mint.Key, PlatformAddress, _recipient.Key, 3);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("reconcile against chain truth");
        stub.SendTransactionCount.Should().Be(1, "a post-send 5xx is ambiguous and must not be auto-retried");
    }

    [Fact]
    public async Task Transfer_onchain_failure_is_reported_as_an_error()
    {
        using var stub = StubWithBothTokenAccounts();
        stub.SignatureError = new Dictionary<string, object?> { ["InstructionError"] = new object[] { 0, "Custom" } };
        var provider = NewProvider(stub);

        var result = await provider.TransferAsync(_mint.Key, PlatformAddress, _recipient.Key, 3);

        result.IsError.Should().BeTrue("a transaction that landed and reverted is a real failure");
        result.Message.Should().Contain("landed on chain but FAILED");
    }

    [Fact]
    public async Task Transfer_without_a_signer_fails_closed_before_broadcast()
    {
        using var stub = StubWithBothTokenAccounts();
        var provider = NewProvider(stub, withSigner: false);

        var result = await provider.TransferAsync(_mint.Key, PlatformAddress, _recipient.Key, 3);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("no transaction signer is configured");
        stub.SendTransactionCount.Should().Be(0);
    }

    [Fact]
    public async Task Transfer_per_user_context_signs_with_the_users_key_via_custody()
    {
        using var stub = new SolanaRpcStub();
        var user = new SolanaWallet(new Mnemonic(WordList.English, WordCount.Twelve).ToString(), WordList.English).Account;
        stub.ExistingAccounts.Add(Ata(user.PublicKey, _mint).Key);
        stub.ExistingAccounts.Add(Ata(_recipient, _mint).Key);

        var avatarId = Guid.NewGuid();
        var walletId = Guid.NewGuid();

        var custody = new Mock<IKeyCustodyService>();
        custody.Setup(c => c.WithSigningKeyAsync(
                It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()))
            .Returns(async (SigningContext _, Func<byte[], Task<AZOAResult<byte[]>>> sign) =>
            {
                var inner = await sign((byte[])user.PrivateKey.KeyBytes.Clone());
                return new AZOAResult<AZOAResult<byte[]>> { Result = inner };
            });
        custody.Setup(c => c.WithPlatformSigningKeyAsync(
                It.IsAny<bool>(), It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()))
            .ThrowsAsync(new InvalidOperationException("a per-user transfer must NOT resolve the platform key"));

        var provider = NewProvider(stub, custody.Object);

        var result = await provider.TransferAsync(
            _mint.Key, user.PublicKey.Key, _recipient.Key, 5,
            SigningContext.ForUser(avatarId, walletId));

        result.IsError.Should().BeFalse(result.Message);
        SignedMessage().AccountKeys[0].Key.Should().Be(user.PublicKey.Key, "the user's key is the fee payer");

        custody.Verify(c => c.WithSigningKeyAsync(
            It.Is<SigningContext>(x => x.WalletId == walletId && x.AvatarId == avatarId),
            It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()), Times.Once);
        custody.Verify(c => c.WithPlatformSigningKeyAsync(
            It.IsAny<bool>(), It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()), Times.Never);
    }

    [Fact]
    public async Task Transfer_per_user_context_without_custody_never_falls_back_to_the_platform_key()
    {
        using var stub = StubWithBothTokenAccounts();
        var provider = NewProvider(stub); // platform mnemonic IS configured; it must not be used

        var result = await provider.TransferAsync(
            _mint.Key, PlatformAddress, _recipient.Key, 5,
            SigningContext.ForUser(Guid.NewGuid(), Guid.NewGuid()));

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Refusing to fall back to the platform key");
        stub.SendTransactionCount.Should().Be(0);
    }

    [Fact]
    public async Task Platform_key_of_the_wrong_shape_fails_closed_naming_the_real_cause()
    {
        using var stub = StubWithBothTokenAccounts();

        // The shared custody platform door resolves an Algorand-format key today.
        var custody = new Mock<IKeyCustodyService>();
        custody.Setup(c => c.WithPlatformSigningKeyAsync(
                It.IsAny<bool>(), It.IsAny<SigningContext>(), It.IsAny<Func<byte[], Task<AZOAResult<byte[]>>>>()))
            .Returns(async (bool _, SigningContext _, Func<byte[], Task<AZOAResult<byte[]>>> sign) =>
            {
                var inner = await sign(new byte[32]);
                return new AZOAResult<AZOAResult<byte[]>> { Result = inner };
            });

        var provider = NewProvider(stub, custody.Object);

        var result = await provider.TransferAsync(_mint.Key, PlatformAddress, _recipient.Key, 5);

        result.IsError.Should().BeTrue("a 32-byte key cannot sign a Solana transaction");
        result.Message.Should().Contain("32 bytes").And.Contain("64-byte Solana secret");
        result.Message.Should().NotContain("stub", "the signer is complete; the message must name the real cause");
        stub.SendTransactionCount.Should().Be(0);
    }

    // ─── Mint ───

    [Fact]
    public async Task Mint_confirmed_returns_the_real_mint_address_and_builds_the_full_spl_sequence()
    {
        using var stub = new SolanaRpcStub();
        var provider = NewProvider(stub);

        var result = await provider.MintAsync("ipfs://meta", 500, "BADGE", PlatformAddress);

        result.IsError.Should().BeFalse(result.Message);
        stub.SendTransactionCount.Should().Be(1);

        // create mint account → initialize mint → create the authority's ATA → mint to it
        ProgramIdsOf(SignedMessage()).Should().Equal(
            SystemProgram.ProgramIdKey.Key,
            TokenProgram.ProgramIdKey.Key,
            AssociatedTokenAccountProgram.ProgramIdKey.Key,
            TokenProgram.ProgramIdKey.Key);

        // The returned id is the real mint account address, and it is an account of the
        // transaction that created it — not a synthetic operation id.
        SignedMessage().AccountKeys.Select(k => k.Key).Should().Contain(result.Result);
        Solnet.Wallet.Utilities.Encoders.Base58.DecodeData(result.Result!).Should().HaveCount(32);
        result.Message.Should().Contain("is not written on chain", "the SPL mint stores no token URI");
    }

    [Fact]
    public async Task Mint_confirmation_timeout_returns_the_real_signature_under_the_pending_marker()
    {
        using var stub = new SolanaRpcStub { SignatureFound = false };
        var provider = NewProvider(stub);

        var result = await provider.MintAsync("ipfs://meta", 500, "BADGE", PlatformAddress);

        result.IsError.Should().BeFalse("the mint transaction was broadcast");
        result.Message.Should().StartWith(OperationStatus.PendingConfirmationMarker);

        var submitted = Transaction.Deserialize(Convert.FromBase64String(stub.LastSubmittedBase64!));
        result.Result.Should().Be(
            Solnet.Wallet.Utilities.Encoders.Base58.EncodeData(submitted.Signatures[0].Signature),
            "an unconfirmed mint has no proven mint account yet — report the signature to reconcile");
    }

    [Fact]
    public async Task Mint_rejects_a_zero_amount_before_touching_the_network()
    {
        using var stub = new SolanaRpcStub();
        var provider = NewProvider(stub);

        var result = await provider.MintAsync("ipfs://meta", 0, "BADGE", PlatformAddress);

        result.IsError.Should().BeTrue();
        stub.SendTransactionCount.Should().Be(0);
    }

    // ─── Burn ───

    [Fact]
    public async Task Burn_confirmed_returns_the_real_signature()
    {
        using var stub = new SolanaRpcStub();
        stub.ExistingAccounts.Add(Ata(PlatformKey, _mint).Key);
        var provider = NewProvider(stub);

        var result = await provider.BurnAsync(_mint.Key, 10, PlatformAddress);

        result.IsError.Should().BeFalse(result.Message);
        ProgramIdsOf(SignedMessage()).Should().Equal(TokenProgram.ProgramIdKey.Key);

        var submitted = Transaction.Deserialize(Convert.FromBase64String(stub.LastSubmittedBase64!));
        result.Result.Should().Be(
            Solnet.Wallet.Utilities.Encoders.Base58.EncodeData(submitted.Signatures[0].Signature));
    }

    [Fact]
    public async Task Burn_fails_closed_when_the_holder_has_no_token_account()
    {
        using var stub = new SolanaRpcStub(); // no accounts exist
        var provider = NewProvider(stub);

        var result = await provider.BurnAsync(_mint.Key, 10, PlatformAddress);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("holds no associated token account");
        stub.SendTransactionCount.Should().Be(0);
    }

    // ─── Reads ───

    [Fact]
    public async Task GetBalance_reports_an_rpc_failure_as_an_error_not_a_zero_balance()
    {
        using var stub = new SolanaRpcStub { TokenAccountsRpcError = "node is unavailable" };
        var provider = NewProvider(stub);

        var result = await provider.GetBalanceAsync(PlatformAddress, _mint.Key);

        result.IsError.Should().BeTrue("an unreachable node is not an empty holding");
        result.Result.Should().NotBe("0");
    }

    [Fact]
    public async Task GetBalance_sums_every_token_account_for_the_mint_in_base_units()
    {
        using var stub = new SolanaRpcStub();
        stub.TokenAccountAmounts.Add("40");
        stub.TokenAccountAmounts.Add("2");
        var provider = NewProvider(stub);

        var result = await provider.GetBalanceAsync(PlatformAddress, _mint.Key);

        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().Be("42");
    }

    [Fact]
    public async Task GetTransactionStatus_never_reports_an_unknown_transaction_as_successful()
    {
        using var stub = new SolanaRpcStub { TransactionFound = false };
        var provider = NewProvider(stub);

        var result = await provider.GetTransactionStatusAsync("4pT3xUnknownSignature");

        // meta?.Err == null is vacuously true when there is no transaction at all, so a
        // success payload here would classify an unseen tx as CONFIRMED.
        result.IsError.Should().BeTrue();
        ChainTxClassifier.Classify(result).Should().Be(ChainConfirmation.Unknown);
    }

    // ─── Bridge capability is ABSENT, which is the whole refusal ───

    [Fact]
    public void Bridge_legs_are_absent_so_there_is_no_primitive_to_fabricate_success_with()
    {
        using var stub = new SolanaRpcStub();
        var provider = NewProvider(stub);

        // Neither leg is implemented, so there is no lock/mint/burn/release body on this
        // provider at all — nothing that could regress into a fabricated Ok on a value path.
        provider.TryGetModule<IBridgeSourceLeg>(out _).Should().BeFalse(
            "no reviewed Solana bridge lifecycle exists");
        provider.TryGetModule<IBridgeTargetLeg>(out _).Should().BeFalse(
            "no reviewed Solana wrapped-asset lifecycle exists");
        stub.SendTransactionCount.Should().Be(0);
    }

    [Fact]
    public void Bridge_absence_is_a_lifecycle_gap_and_never_blames_the_signer()
    {
        // SolanaTransactionSigner is genuinely complete; the SPL capability module is present.
        // The missing piece is the bridge lifecycle, never the signer.
        using var stub = new SolanaRpcStub();
        var provider = NewProvider(stub);

        provider.TryGetModule<ISolanaSPLModule>(out var spl).Should().BeTrue(
            "the real SPL surface is unaffected by the absent bridge capability");
        spl.Should().NotBeNull();
    }

    // ─── Container wiring ───

    [Fact]
    public void Container_selects_the_constructor_that_wires_a_real_signer()
    {
        // Program.cs registers the provider with a bare AddTransient<SolanaProvider>(),
        // so constructor selection decides whether the value path can sign at all. It
        // must also stay resolvable from the ROOT provider: BlockchainProviderFactory is
        // a singleton that caches one provider per chain/network, so a scoped
        // IKeyCustodyService constructor parameter would be a captive dependency.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(BuildConfig());
        services.AddSingleton<ITransactionSignerFactory>(
            new TransactionSignerFactory(new ITransactionSigner[] { new SolanaTransactionSigner() }));
        services.AddScoped(_ => new Mock<IKeyCustodyService>().Object);
        services.AddTransient<SolanaProvider>();

        using var root = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        var provider = root.GetRequiredService<SolanaProvider>();

        var signerFactory = typeof(SolanaProvider)
            .GetField("_signerFactory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(provider);
        signerFactory.Should().NotBeNull("a container-built provider that cannot sign fails every value op");

        var scopeFactory = typeof(SolanaProvider)
            .GetField("_custodyScopeFactory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(provider);
        scopeFactory.Should().NotBeNull("custody must be resolvable per signing op");
    }

    // ─── Test doubles ───

    /// <summary>Wraps the real signer so a test can assert on the exact bytes in and out.</summary>
    private sealed class RecordingSolanaSigner : ITransactionSigner
    {
        private readonly SolanaTransactionSigner _inner = new();

        public byte[]? LastCanonicalInput { get; private set; }
        public byte[]? LastSignedOutput { get; private set; }

        public string ChainType => "Solana";

        public AZOAResult<byte[]> Sign(byte[] canonicalTxn, SigningKeyMaterial key)
        {
            LastCanonicalInput = (byte[])canonicalTxn.Clone();
            var result = _inner.Sign(canonicalTxn, key);
            if (!result.IsError && result.Result is not null)
                LastSignedOutput = (byte[])result.Result.Clone();
            return result;
        }
    }

    /// <summary>In-process Solana JSON-RPC stub — no cluster is ever contacted.</summary>
    private sealed class SolanaRpcStub : HttpMessageHandler
    {
        public string Blockhash { get; set; } = "9zsTF6xNbEKD8yXwsjyXn7dqNKLXbRRvYYqMSGmUJcLL";
        public HashSet<string> ExistingAccounts { get; } = new(StringComparer.Ordinal);
        public int MintDecimals { get; set; }
        public ulong RentExemptLamports { get; set; } = 1_461_600;
        public int? SendTransactionHttpStatus { get; set; }
        public bool SignatureFound { get; set; } = true;
        public string ConfirmationStatus { get; set; } = "confirmed";
        public object? SignatureError { get; set; }
        public string? TokenAccountsRpcError { get; set; }
        public List<string> TokenAccountAmounts { get; } = new();
        public bool TransactionFound { get; set; } = true;

        public int SendTransactionCount { get; private set; }
        public string? LastSubmittedBase64 { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? "{}"
                : await request.Content.ReadAsStringAsync(cancellationToken);

            string method;
            int id;
            string firstParam;
            using (var document = JsonDocument.Parse(body))
            {
                method = document.RootElement.GetProperty("method").GetString() ?? "";
                id = document.RootElement.GetProperty("id").GetInt32();
                var parameters = document.RootElement.GetProperty("params");
                firstParam = parameters.GetArrayLength() > 0 && parameters[0].ValueKind == JsonValueKind.String
                    ? parameters[0].GetString() ?? ""
                    : "";
            }

            switch (method)
            {
                case "getLatestBlockhash":
                    return Result(id, new { value = new { blockhash = Blockhash, lastValidBlockHeight = 1000L } });

                case "getMinimumBalanceForRentExemption":
                    return Result(id, RentExemptLamports);

                case "getAccountInfo":
                    object? account = ExistingAccounts.Contains(firstParam)
                        ? new { lamports = 2_039_280L, owner = TokenProgram.ProgramIdKey.Key, executable = false }
                        : null;
                    return Result(id, new { value = account });

                case "getTokenSupply":
                    return Result(id, new
                    {
                        value = new
                        {
                            amount = "1000",
                            decimals = MintDecimals,
                            uiAmount = 1000.0m,
                            uiAmountString = "1000",
                        },
                    });

                case "sendTransaction":
                    SendTransactionCount++;
                    LastSubmittedBase64 = firstParam;
                    if (SendTransactionHttpStatus.HasValue)
                        return new HttpResponseMessage((HttpStatusCode)SendTransactionHttpStatus.Value);
                    return Result(id, SignatureOf(firstParam));

                case "getSignatureStatuses":
                    object? signatureStatus = SignatureFound || SignatureError is not null
                        ? new
                        {
                            slot = 4242L,
                            confirmations = (long?)null,
                            err = SignatureError,
                            confirmationStatus = ConfirmationStatus,
                        }
                        : null;
                    return Result(id, new { value = new[] { signatureStatus } });

                case "getTokenAccountsByOwner":
                    if (TokenAccountsRpcError is not null)
                        return RpcError(id, TokenAccountsRpcError);
                    return Result(id, new
                    {
                        value = TokenAccountAmounts.Select(amount => new
                        {
                            pubkey = "TokenAccountPubkey",
                            account = new
                            {
                                data = new
                                {
                                    parsed = new
                                    {
                                        info = new
                                        {
                                            mint = "MintAddress",
                                            owner = "OwnerAddress",
                                            state = "initialized",
                                            tokenAmount = new
                                            {
                                                amount,
                                                decimals = MintDecimals,
                                                uiAmount = 0m,
                                                uiAmountString = amount,
                                            },
                                        },
                                        type = "account",
                                    },
                                    program = "spl-token",
                                },
                            },
                        }).ToArray(),
                    });

                case "getTransaction":
                    return TransactionFound
                        ? Result(id, new { slot = 4242L, blockTime = 1_700_000_000L, meta = new { err = (object?)null, fee = 5000L } })
                        : Result(id, null);

                case "getBalance":
                    return Result(id, new { value = 1_000_000_000L });

                default:
                    return RpcError(id, $"unstubbed method {method}");
            }
        }

        /// <summary>The signature the cluster echoes back: read out of the submitted bytes.</summary>
        private static string SignatureOf(string base64Transaction)
        {
            var transaction = Transaction.Deserialize(Convert.FromBase64String(base64Transaction));
            return Solnet.Wallet.Utilities.Encoders.Base58.EncodeData(transaction.Signatures[0].Signature);
        }

        private static HttpResponseMessage Result(int id, object? result) =>
            Envelope(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });

        private static HttpResponseMessage RpcError(int id, string message) =>
            Envelope(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new Dictionary<string, object?> { ["code"] = -32602, ["message"] = message },
            });

        private static HttpResponseMessage Envelope(Dictionary<string, object?> payload)
        {
            var content = new StringContent(
                JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
