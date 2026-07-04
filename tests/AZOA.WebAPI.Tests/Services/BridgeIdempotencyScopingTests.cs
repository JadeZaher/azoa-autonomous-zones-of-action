using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.Security.Cryptography;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Bridge;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Bridge;

namespace AZOA.WebAPI.Tests.Services;

/// <summary>
/// Phase C items 5, 6, 8 — avatar-namespaced idempotency key scoping.
/// All tests use Moq mocks of IBridgeStore / IIdempotencyStore and capture
/// keys via Moq Callback. No shared fake files are required.
/// See Services/CrossChainBridgeService.cs for key construction formulas.
/// </summary>
public class BridgeIdempotencyScopingTests
{
    // ─── Key-format constants (must mirror CrossChainBridgeService exactly) ──
    // Trusted:   client → "{avatarId:N}:{clientKey}",  none → "bridge-trusted:{avatarId:N}:..."
    // WH init:   client → "{avatarId:N}:{clientKey}",  none → "bridge-wh-initiate:{avatarId:N}:..."
    // Redeem:    client → "{tx.AvatarId:N}:{clientKey}", none → "bridge-redeem:{tx.Id}:{digest}"
    // Reverse:   client → "{tx.AvatarId:N}:{clientKey}", none → "bridge-reverse:{tx.Id}:{addr}"

    // ─── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns an IIdempotencyStore mock that captures each TryClaimAsync key
    /// into <paramref name="capturedKeys"/> and always returns Won=true so the
    /// flow can advance further (avoiding "in progress" short-circuit on the
    /// second call in tests that make two sequential calls).
    /// Subsequent store methods (CompleteAsync etc.) are lenient stubs.
    /// </summary>
    private static Mock<IIdempotencyStore> ClaimCapture(List<string> capturedKeys)
    {
        var mock = new Mock<IIdempotencyStore>();
        mock.Setup(s => s.TryClaimAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string key, string _, CancellationToken __) => capturedKeys.Add(key))
            .ReturnsAsync((string key, string op, CancellationToken _) =>
                new IdempotencyClaim(true, new IdempotencyRecord
                {
                    Key = key,
                    OperationType = op,
                    State = IdempotencyState.InProgress,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }));

        // Lenient stubs so CompleteAsync / FailAsync don't throw.
        mock.Setup(s => s.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(s => s.FailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    /// <summary>
    /// Factory where every GetProvider returns a provider with ChainType "Solana".
    /// RealValueEnabled=true is used in all scoping tests (gate is not under test here).
    /// </summary>
    private static Mock<IBlockchainProviderFactory> SolanaFactory()
    {
        var provider = new Mock<IBlockchainProvider>();
        provider.Setup(p => p.ChainType).Returns("Solana");
        provider.Setup(p => p.SupportsBridging).Returns(true);

        var factory = new Mock<IBlockchainProviderFactory>();
        factory.Setup(f => f.GetProvider(It.IsAny<string>(), It.IsAny<ChainNetwork>()))
               .Returns(provider.Object);
        return factory;
    }

    /// <summary>
    /// Builds a service with RealValueEnabled=true (gate open), the given factory,
    /// the given idempotency mock, and a lenient bridge store mock.
    /// </summary>
    private static CrossChainBridgeService BuildService(
        Mock<IBlockchainProviderFactory> factory,
        Mock<IIdempotencyStore> idempotency,
        Mock<IBridgeStore>? bridgeStore = null)
    {
        var store = bridgeStore ?? new Mock<IBridgeStore>();

        // Lenient bridge store stubs for writes the trusted path performs.
        store.Setup(s => s.AddBridgeAsync(It.IsAny<BridgeTransactionResult>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);
        store.Setup(s => s.TryTransitionBridgeStatusAsync(
                It.IsAny<string>(), It.IsAny<BridgeStatus>(), It.IsAny<BridgeStatus>(),
                It.IsAny<BridgeStatusMutation?>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(1);
        store.Setup(s => s.GetBridgeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((BridgeTransactionResult?)null);
        store.Setup(s => s.GetBridgeByIdempotencyKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((BridgeTransactionResult?)null);

        return new CrossChainBridgeService(
            factory.Object,
            Mock.Of<IWormholeAdapter>(),
            Options.Create(new WormholeConfig { DefaultMode = BridgeMode.Trusted }),
            store.Object,
            idempotency.Object,
            Mock.Of<ILogger<CrossChainBridgeService>>(),
            Options.Create(new BridgeOptions { RealValueEnabled = true }),
            new ConfigurationBuilder().Build());
    }

    // ─── Test 8: Two avatars, same client key → distinct claim keys ──────────

    /// <summary>
    /// Avatar A and avatar B each call InitiateBridgeAsync with the SAME
    /// client Idempotency-Key. The captured keys must differ and each must
    /// embed its own avatar's N-format GUID (no {}-dashes), proving the
    /// avatar namespace prevents cross-avatar key collisions.
    /// </summary>
    [Fact]
    public async Task Initiate_TwoAvatars_SameClientKey_ProducesDistinctNamespacedKeys()
    {
        const string sharedClientKey = "my-idempotency-key";
        var avatarA = Guid.NewGuid();
        var avatarB = Guid.NewGuid();

        var capturedKeys = new List<string>();
        var idempotency = ClaimCapture(capturedKeys);
        var svc = BuildService(SolanaFactory(), idempotency);

        await svc.InitiateBridgeAsync(
            "Solana", "Algorand", "tok1", "addr", avatarA, 1, BridgeMode.Trusted,
            clientIdempotencyKey: sharedClientKey);
        await svc.InitiateBridgeAsync(
            "Solana", "Algorand", "tok1", "addr", avatarB, 1, BridgeMode.Trusted,
            clientIdempotencyKey: sharedClientKey);

        capturedKeys.Should().HaveCount(2, "one TryClaimAsync call per initiate");

        var keyA = capturedKeys[0];
        var keyB = capturedKeys[1];

        keyA.Should().NotBe(keyB, "different avatars must produce different claim keys");

        // Each key must embed its avatar id in N format (no dashes) before the client key.
        keyA.Should().Contain(avatarA.ToString("N"),
            "avatar A's key must be namespaced with its own guid");
        keyA.Should().Contain(sharedClientKey);
        keyA.Should().NotContain(avatarB.ToString("N"));

        keyB.Should().Contain(avatarB.ToString("N"),
            "avatar B's key must be namespaced with its own guid");
        keyB.Should().Contain(sharedClientKey);
        keyB.Should().NotContain(avatarA.ToString("N"));
    }

    // ─── Test 9: Same avatar replays same client key → identical key ─────────

    /// <summary>
    /// The same avatar calling InitiateBridgeAsync twice with the same client key
    /// must produce the same claim key both times — deduplication relies on the
    /// key being stable across retries by the same caller.
    /// </summary>
    [Fact]
    public async Task Initiate_SameAvatar_SameClientKey_ProducesIdenticalKey_DedupePreserved()
    {
        const string clientKey = "retry-key-123";
        var avatarId = Guid.NewGuid();

        var capturedKeys = new List<string>();
        var idempotency = ClaimCapture(capturedKeys);
        var svc = BuildService(SolanaFactory(), idempotency);

        await svc.InitiateBridgeAsync(
            "Solana", "Algorand", "tok1", "addr", avatarId, 1, BridgeMode.Trusted,
            clientIdempotencyKey: clientKey);
        await svc.InitiateBridgeAsync(
            "Solana", "Algorand", "tok1", "addr", avatarId, 1, BridgeMode.Trusted,
            clientIdempotencyKey: clientKey);

        capturedKeys.Should().HaveCount(2);
        capturedKeys[0].Should().Be(capturedKeys[1],
            "same avatar + same client key must produce the same claim key across retries");

        // The key is "{avatarId:N}:{clientKey}".
        capturedKeys[0].Should().Be($"{avatarId:N}:{clientKey}");
    }

    // ─── Test 10: Redeem/Reverse with client key → "{tx.AvatarId:N}:{clientKey}" ──

    /// <summary>
    /// RedeemWithVAAAsync with a client key uses tx.AvatarId (the bridge owner),
    /// not the caller's identity — the claim key is "{tx.AvatarId:N}:{clientKey}".
    /// ReverseBridgeAsync applies the same scoping from tx.AvatarId.
    /// </summary>
    [Fact]
    public async Task RedeemAndReverse_WithClientKey_KeyIsAvatarNamespaced()
    {
        var txAvatarId = Guid.NewGuid();
        const string clientKey = "client-supplied-key";

        var vaaBytes = "AQIDBA=="; // valid base64
        var vaaDigest = WormholeAdapter.ComputeVaaDigest(vaaBytes);

        var seededBridge = new BridgeTransactionResult
        {
            Id = "wh_bridge_scoping_test",
            AvatarId = txAvatarId,
            SourceChain = "Solana",
            TargetChain = "Algorand",
            SourceTokenId = "tok1",
            TargetAddress = "recipient",
            Amount = 1,
            Mode = BridgeMode.Wormhole,
            Status = BridgeStatus.VAAReady,
            VaaBytes = vaaBytes,
            VaaSignatureCount = 13,
            WormholeEmitterChainId = 1,
            WormholeEmitterAddress = "emitter",
            WormholeSequence = 42,
            CreatedAt = DateTime.UtcNow
        };

        var completedBridge = new BridgeTransactionResult
        {
            Id = "bridge_reverse_scoping",
            AvatarId = txAvatarId,
            SourceChain = "Solana",
            TargetChain = "Algorand",
            SourceTokenId = "tok1",
            TargetTokenId = "wrapped_tok",
            SourceAddress = "src",
            TargetAddress = "recipient",
            Amount = 1,
            Mode = BridgeMode.Trusted,
            Status = BridgeStatus.Completed,
            MintTxHash = "mint_tx",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            CompletedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        var capturedKeys = new List<string>();
        var idempotency = ClaimCapture(capturedKeys);

        var bridgeStore = new Mock<IBridgeStore>();
        bridgeStore.Setup(s => s.GetBridgeAsync("wh_bridge_scoping_test", It.IsAny<CancellationToken>()))
                   .ReturnsAsync(seededBridge);
        bridgeStore.Setup(s => s.GetBridgeAsync("bridge_reverse_scoping", It.IsAny<CancellationToken>()))
                   .ReturnsAsync(completedBridge);
        bridgeStore.Setup(s => s.TryTransitionBridgeStatusAsync(
                It.IsAny<string>(), It.IsAny<BridgeStatus>(), It.IsAny<BridgeStatus>(),
                It.IsAny<BridgeStatusMutation?>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(1);
        bridgeStore.Setup(s => s.TryInsertConsumedVaaAsync(
                It.IsAny<ConsumedVaaRecord>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(true);

        var svc = BuildService(SolanaFactory(), idempotency, bridgeStore);

        await svc.RedeemWithVAAAsync("wh_bridge_scoping_test", clientIdempotencyKey: clientKey);
        await svc.ReverseBridgeAsync("bridge_reverse_scoping", "refund_addr", clientIdempotencyKey: clientKey);

        capturedKeys.Should().HaveCount(2, "one TryClaimAsync per operation");

        var redeemKey = capturedKeys[0];
        var reverseKey = capturedKeys[1];

        // Redeem: "{tx.AvatarId:N}:{clientKey}"
        redeemKey.Should().Be($"{txAvatarId:N}:{clientKey}",
            "redeem key must be namespaced by tx.AvatarId in N format");

        // Reverse: "{tx.AvatarId:N}:{clientKey}"
        reverseKey.Should().Be($"{txAvatarId:N}:{clientKey}",
            "reverse key must be namespaced by tx.AvatarId in N format");
    }

    // ─── Test 11: No client key → deterministic content keys ─────────────────

    /// <summary>
    /// When no client key is supplied, each operation falls back to a
    /// deterministic content key whose exact format is specified in the
    /// production comments and must match here:
    ///   Trusted initiate: "bridge-trusted:{avatarId:N}:{source}:{target}:{token}:{addr}:{amount}"
    ///   Redeem:           "bridge-redeem:{tx.Id}:{vaaDigest}"
    ///   Reverse:          "bridge-reverse:{tx.Id}:{sourceRecipientAddr}"
    /// </summary>
    [Fact]
    public async Task Operations_NoClientKey_UseDeterministicContentKeys()
    {
        var avatarId = Guid.NewGuid();
        const string sourceChain = "Solana";
        const string targetChain = "Algorand";
        const string tokenId = "tok1";
        const string recipient = "addr-recipient";
        const int amount = 1;

        var vaaBytes = "AQIDBA==";
        var txId = "wh_bridge_nokey_test";
        var txAvatarId = Guid.NewGuid();
        var vaaDigest = WormholeAdapter.ComputeVaaDigest(vaaBytes);

        var seededBridge = new BridgeTransactionResult
        {
            Id = txId,
            AvatarId = txAvatarId,
            SourceChain = sourceChain,
            TargetChain = targetChain,
            SourceTokenId = tokenId,
            TargetAddress = recipient,
            Amount = amount,
            Mode = BridgeMode.Wormhole,
            Status = BridgeStatus.VAAReady,
            VaaBytes = vaaBytes,
            VaaSignatureCount = 13,
            WormholeEmitterChainId = 1,
            WormholeEmitterAddress = "emitter",
            WormholeSequence = 42,
            CreatedAt = DateTime.UtcNow
        };

        var completedBridge = new BridgeTransactionResult
        {
            Id = "bridge_nokey_reverse",
            AvatarId = txAvatarId,
            SourceChain = sourceChain,
            TargetChain = targetChain,
            SourceTokenId = tokenId,
            TargetTokenId = "wrapped_tok",
            SourceAddress = "src",
            TargetAddress = recipient,
            Amount = amount,
            Mode = BridgeMode.Trusted,
            Status = BridgeStatus.Completed,
            MintTxHash = "mint_tx",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            CompletedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        const string refundAddr = "src_refund_addr";

        var capturedKeys = new List<string>();
        var idempotency = ClaimCapture(capturedKeys);

        var bridgeStore = new Mock<IBridgeStore>();
        bridgeStore.Setup(s => s.AddBridgeAsync(It.IsAny<BridgeTransactionResult>(), It.IsAny<CancellationToken>()))
                   .Returns(Task.CompletedTask);
        bridgeStore.Setup(s => s.GetBridgeByIdempotencyKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync((BridgeTransactionResult?)null);
        bridgeStore.Setup(s => s.GetBridgeAsync(txId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(seededBridge);
        bridgeStore.Setup(s => s.GetBridgeAsync("bridge_nokey_reverse", It.IsAny<CancellationToken>()))
                   .ReturnsAsync(completedBridge);
        bridgeStore.Setup(s => s.TryTransitionBridgeStatusAsync(
                It.IsAny<string>(), It.IsAny<BridgeStatus>(), It.IsAny<BridgeStatus>(),
                It.IsAny<BridgeStatusMutation?>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(1);
        bridgeStore.Setup(s => s.TryInsertConsumedVaaAsync(
                It.IsAny<ConsumedVaaRecord>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(true);

        var svc = BuildService(SolanaFactory(), idempotency, bridgeStore);

        // 1) Trusted initiate (no client key).
        await svc.InitiateBridgeAsync(
            sourceChain, targetChain, tokenId, recipient, avatarId, amount, BridgeMode.Trusted);

        // 2) Redeem (no client key).
        await svc.RedeemWithVAAAsync(txId);

        // 3) Reverse (no client key).
        await svc.ReverseBridgeAsync("bridge_nokey_reverse", refundAddr);

        capturedKeys.Should().HaveCount(3);

        // Trusted initiate deterministic key.
        capturedKeys[0].Should().Be(
            $"bridge-trusted:{avatarId:N}:{sourceChain}:{targetChain}:{tokenId}:{recipient}:{amount}",
            "trusted initiate must use content-addressed deterministic key");

        // Redeem deterministic key.
        capturedKeys[1].Should().Be(
            $"bridge-redeem:{txId}:{vaaDigest}",
            "redeem must use bridge-id + vaa-digest as deterministic key");

        // Reverse deterministic key.
        capturedKeys[2].Should().Be(
            $"bridge-reverse:bridge_nokey_reverse:{refundAddr}",
            "reverse must use bridge-id + refund-address as deterministic key");
    }

    // ─── Test 12: Digest consistency — VAA bytes → WormholeAdapter.ComputeVaaDigest ──

    /// <summary>
    /// The digest embedded in the redeem claim key must equal
    /// WormholeAdapter.ComputeVaaDigest(vaaBytes): SHA-256 of the base64-decoded
    /// bytes, lowercase hex, 64 chars.
    ///
    /// Also covers the malformed-base64 rejection path: if VaaBytes is not valid
    /// base64, the service returns an error and no TryClaimAsync is called (the
    /// digest computation runs BEFORE the claim).
    /// </summary>
    [Fact]
    public async Task Redeem_DigestInClaimKey_MatchesWormholeAdapterComputeVaaDigest()
    {
        // ── Happy path: valid base64 → digest in key matches ComputeVaaDigest ──
        var vaaBytes = "VkFBLWRpZ2VzdC1zY29waW5n"; // "VAA-digest-scoping" in base64
        var expectedDigest = WormholeAdapter.ComputeVaaDigest(vaaBytes);
        expectedDigest.Should().MatchRegex("^[0-9a-f]{64}$",
            "ComputeVaaDigest must produce a 64-char lowercase hex SHA-256");

        var txId = "wh_bridge_digest_test";
        var txAvatarId = Guid.NewGuid();

        var seededBridge = new BridgeTransactionResult
        {
            Id = txId,
            AvatarId = txAvatarId,
            SourceChain = "Solana",
            TargetChain = "Algorand",
            SourceTokenId = "tok1",
            TargetAddress = "recipient",
            Amount = 1,
            Mode = BridgeMode.Wormhole,
            Status = BridgeStatus.VAAReady,
            VaaBytes = vaaBytes,
            VaaSignatureCount = 13,
            WormholeEmitterChainId = 1,
            WormholeEmitterAddress = "emitter",
            WormholeSequence = 99,
            CreatedAt = DateTime.UtcNow
        };

        var capturedKeys = new List<string>();
        var idempotency = ClaimCapture(capturedKeys);

        var bridgeStore = new Mock<IBridgeStore>();
        bridgeStore.Setup(s => s.GetBridgeAsync(txId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(seededBridge);
        bridgeStore.Setup(s => s.TryTransitionBridgeStatusAsync(
                It.IsAny<string>(), It.IsAny<BridgeStatus>(), It.IsAny<BridgeStatus>(),
                It.IsAny<BridgeStatusMutation?>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(1);
        bridgeStore.Setup(s => s.TryInsertConsumedVaaAsync(
                It.IsAny<ConsumedVaaRecord>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(true);

        var svc = BuildService(SolanaFactory(), idempotency, bridgeStore);
        await svc.RedeemWithVAAAsync(txId); // no client key → deterministic key used

        capturedKeys.Should().HaveCount(1);
        var capturedKey = capturedKeys[0];

        // Key format: "bridge-redeem:{txId}:{digest}"
        capturedKey.Should().Be($"bridge-redeem:{txId}:{expectedDigest}",
            "the digest segment of the claim key must equal WormholeAdapter.ComputeVaaDigest");

        // The digest itself is lowercase hex SHA-256 of the decoded bytes.
        var rawBytes = Convert.FromBase64String(vaaBytes);
        var expectedHex = Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();
        capturedKey.Should().Contain(expectedHex,
            "claim key digest must be lowercase-hex SHA-256 of the base64-decoded VAA bytes");
    }

    /// <summary>
    /// When VaaBytes is not valid base64, ComputeVaaDigest throws a FormatException.
    /// The service must catch it and return a typed error BEFORE any TryClaimAsync
    /// call — no state is mutated, no mint is possible.
    ///
    /// Note: this rejection path is identical to the malformed-VAA guard covered in
    /// CrossChainBridgeServiceTests; the scoping test specifically verifies the
    /// claim is never touched (idempotency store is MockBehavior.Strict).
    /// </summary>
    [Fact]
    public async Task Redeem_MalformedBase64VaaBytes_RejectsBeforeClaim_NoMint()
    {
        const string malformedVaa = "not_valid_base64!!!";

        var txId = "wh_bridge_malformed_vaa";
        var txAvatarId = Guid.NewGuid();

        var seededBridge = new BridgeTransactionResult
        {
            Id = txId,
            AvatarId = txAvatarId,
            SourceChain = "Solana",
            TargetChain = "Algorand",
            SourceTokenId = "tok1",
            TargetAddress = "recipient",
            Amount = 1,
            Mode = BridgeMode.Wormhole,
            Status = BridgeStatus.VAAReady,
            VaaBytes = malformedVaa,
            VaaSignatureCount = 13,
            WormholeEmitterChainId = 1,
            WormholeEmitterAddress = "emitter",
            WormholeSequence = 77,
            CreatedAt = DateTime.UtcNow
        };

        // Strict mock: any TryClaimAsync call will throw and fail the test.
        var idempotency = new Mock<IIdempotencyStore>(MockBehavior.Strict);

        var bridgeStore = new Mock<IBridgeStore>();
        bridgeStore.Setup(s => s.GetBridgeAsync(txId, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(seededBridge);

        var svc = BuildService(SolanaFactory(), idempotency, bridgeStore);
        var result = await svc.RedeemWithVAAAsync(txId);

        result.IsError.Should().BeTrue(
            "malformed base64 VaaBytes must be rejected before any claim or mint");
        result.Message.Should().Contain("malformed",
            "error message must indicate the VAA bytes were not valid base64");

        // MockBehavior.Strict already enforces no TryClaimAsync calls,
        // but explicit Verify adds a readable failure message.
        idempotency.Verify(s => s.TryClaimAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "TryClaimAsync must NOT be called when base64 decoding fails");
    }
}
