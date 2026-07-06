using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Services.Signing;
using Xunit;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>
/// final-hardening B5 rotation orchestration: dual-key readability, idempotent
/// resume, and — the critical safety property — all-or-nothing rollback when a
/// mid-batch persist fails, so wallets are never left half-rotated.
/// </summary>
public class KeyRotationServiceTests
{
    private const string KeyOld = "rotation-test-key-OLD-do-not-use-in-prod";
    private const string KeyNew = "rotation-test-key-NEW-rotated-target-key";

    private static WalletKeyService KeyService(string secret)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AZOA:WalletEncryptionKey"] = secret })
            .Build();
        return new WalletKeyService(config);
    }

    private static IConfiguration ConfigWithOldKey() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AZOA:WalletEncryptionKey"] = KeyOld })
            .Build();

    private static ITenantConsentGate AllowAllConsentGate()
    {
        var mock = new Mock<ITenantConsentGate>();
        mock.Setup(g => g.EnsureAllowedAsync(It.IsAny<SigningContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { Result = true });
        return mock.Object;
    }

    /// <summary>In-memory wallet store that can be told to fail the Nth upsert (1-based).</summary>
    private sealed class FakeWalletStore : IWalletStore
    {
        private readonly Dictionary<Guid, IWallet> _rows = new();
        public int FailOnUpsertNumber { get; set; } = -1;
        public int UpsertCalls { get; private set; }

        public FakeWalletStore(IEnumerable<IWallet> seed)
        {
            foreach (var w in seed) _rows[w.Id] = Clone(w);
        }

        private static IWallet Clone(IWallet w) => new Wallet
        {
            Id = w.Id, AvatarId = w.AvatarId, ChainType = w.ChainType, Address = w.Address,
            PublicKey = w.PublicKey, Label = w.Label, IsDefault = w.IsDefault, WalletType = w.WalletType,
            EncryptedPrivateKey = w.EncryptedPrivateKey, EncryptedSeedPhrase = w.EncryptedSeedPhrase,
            CreatedDate = w.CreatedDate,
        };

        public IWallet Snapshot(Guid id) => Clone(_rows[id]);

        public Task<AZOAResult<IEnumerable<IWallet>>> GetAllAsync(CancellationToken ct = default)
        {
            GetAllCalls++;
            if (GetAllCalls == CorruptOnGetAllCall && CorruptWalletId is { } id && _rows.TryGetValue(id, out var row))
                row.EncryptedPrivateKey = "corrupt-ciphertext-not-readable-under-any-key";
            return Task.FromResult(new AZOAResult<IEnumerable<IWallet>> { Result = _rows.Values.Select(Clone).ToList() });
        }

        public Task<AZOAResult<IWallet>> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(_rows.TryGetValue(id, out var w)
                ? new AZOAResult<IWallet> { Result = Clone(w) }
                : new AZOAResult<IWallet> { IsError = true, Message = "Wallet not found." });

        public Task<AZOAResult<IEnumerable<IWallet>>> GetByAvatarAsync(Guid avatarId, CancellationToken ct = default)
            => Task.FromResult(new AZOAResult<IEnumerable<IWallet>>
            { Result = _rows.Values.Where(w => w.AvatarId == avatarId).Select(Clone).ToList() });

        /// <summary>Corrupts this wallet's stored ciphertext on the Nth GetAllAsync call
        /// (<see cref="CorruptOnGetAllCall"/>), so the post-rotation readability sweep sees
        /// an unreadable wallet. Simulates a torn write / silent corruption the per-wallet
        /// path didn't surface. Inside RotateAllAsync the initial listing is one GetAll and
        /// the sweep is the next — so target the sweep, not the listing.</summary>
        public Guid? CorruptWalletId { get; set; }

        /// <summary>1-based GetAllAsync call number on which to apply the corruption.</summary>
        public int CorruptOnGetAllCall { get; set; } = -1;

        public int GetAllCalls { get; private set; }

        public Task<AZOAResult<IWallet>> UpsertAsync(IWallet wallet, CancellationToken ct = default)
        {
            UpsertCalls++;
            if (UpsertCalls == FailOnUpsertNumber)
                return Task.FromResult(new AZOAResult<IWallet> { IsError = true, Message = "Simulated persist failure." });
            _rows[wallet.Id] = Clone(wallet);
            return Task.FromResult(new AZOAResult<IWallet> { Result = Clone(wallet) });
        }

        public Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
        {
            _rows.Remove(id);
            return Task.FromResult(new AZOAResult<bool> { Result = true });
        }
    }

    private static IKeyCustodyService Custody(FakeWalletStore store) =>
        new KeyCustodyService(store, KeyService(KeyOld), ConfigWithOldKey(), AllowAllConsentGate());

    /// <summary>In-memory pending-rotation marker: records the last write, whether it was
    /// cleared, and can be told to fail its write (to prove the rotation refuses to start
    /// when the marker cannot be persisted — the HIGH-1 fail-closed guard).</summary>
    private sealed class FakePendingRotationKeyStore : IPendingRotationKeyStore
    {
        public PendingRotationRecord? Current { get; private set; }
        public bool WriteWasCalled { get; private set; }
        public bool ClearWasCalled { get; private set; }
        public bool FailWrite { get; set; }

        public Task<AZOAResult<bool>> WritePendingAsync(string verifierToken, int walletsInScope, CancellationToken ct = default)
        {
            WriteWasCalled = true;
            if (FailWrite)
                return Task.FromResult(new AZOAResult<bool> { IsError = true, Message = "Simulated marker write failure." });
            Current = new PendingRotationRecord
            {
                StartedUtc = System.DateTime.UtcNow.ToString("O"),
                VerifierToken = verifierToken,
                WalletsInScope = walletsInScope,
            };
            return Task.FromResult(new AZOAResult<bool> { Result = true });
        }

        public Task<AZOAResult<bool>> ClearAsync(CancellationToken ct = default)
        {
            ClearWasCalled = true;
            Current = null;
            return Task.FromResult(new AZOAResult<bool> { Result = true });
        }

        public Task<AZOAResult<PendingRotationRecord?>> ReadAsync(CancellationToken ct = default)
            => Task.FromResult(new AZOAResult<PendingRotationRecord?> { Result = Current });
    }

    private static Wallet PlatformWalletUnder(WalletKeyService svc, string clearPkHex)
        => new()
        {
            Id = Guid.NewGuid(),
            AvatarId = Guid.NewGuid(),
            ChainType = "Solana",
            Address = "ADDR",
            WalletType = WalletType.Platform,
            EncryptedPrivateKey = svc.EncryptPrivateKey(clearPkHex),
        };

    private static KeyRotationService NewService(FakeWalletStore store, IPendingRotationKeyStore? pending = null) =>
        new(store, Custody(store), pending ?? new FakePendingRotationKeyStore(),
            ConfigWithOldKey(), NullLogger<KeyRotationService>.Instance);

    // ─── Happy path: all wallets re-wrapped, readable under the new key ───

    [Fact]
    public async Task RotateAllAsync_rewraps_all_wallets_readable_under_new_key()
    {
        var old = KeyService(KeyOld);
        var pkA = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var pkB = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
        var store = new FakeWalletStore(new[] { PlatformWalletUnder(old, pkA), PlatformWalletUnder(old, pkB) });

        var report = await NewService(store).RotateAllAsync(KeyNew);

        report.IsError.Should().BeFalse(report.Message);
        report.Result!.Rewrapped.Should().Be(2);
        report.Result.RolledBack.Should().BeFalse();

        // Both wallets now decrypt under the NEW key and no longer under the old.
        var newSvc = KeyService(KeyNew);
        foreach (var w in (await store.GetAllAsync()).Result!)
            newSvc.DecryptPrivateKey(w.EncryptedPrivateKey!).Should().NotBeNullOrEmpty();
    }

    // ─── Idempotent / resumable: a re-run skips already-rotated wallets ───

    [Fact]
    public async Task RotateAllAsync_is_idempotent_on_rerun()
    {
        var old = KeyService(KeyOld);
        var store = new FakeWalletStore(new[]
        {
            PlatformWalletUnder(old, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
        });

        var first = await NewService(store).RotateAllAsync(KeyNew);
        first.Result!.Rewrapped.Should().Be(1);

        var second = await NewService(store).RotateAllAsync(KeyNew);
        second.IsError.Should().BeFalse();
        second.Result!.Rewrapped.Should().Be(0, "already-rotated wallets are skipped");
        second.Result.AlreadyRotated.Should().Be(1);
    }

    // ─── THE critical property: rollback on partial failure ───

    [Fact]
    public async Task RotateAllAsync_rolls_back_all_wallets_when_a_mid_batch_persist_fails()
    {
        var old = KeyService(KeyOld);
        var pkA = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var pkB = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
        var pkC = "aaaabbbbccccddddaaaabbbbccccddddaaaabbbbccccddddaaaabbbbccccdddd0";
        var store = new FakeWalletStore(new[]
        {
            PlatformWalletUnder(old, pkA), PlatformWalletUnder(old, pkB), PlatformWalletUnder(old, pkC),
        });

        // Capture the exact pre-rotation ciphertext of every wallet.
        var before = (await store.GetAllAsync()).Result!
            .ToDictionary(w => w.Id, w => w.EncryptedPrivateKey);

        // Fail the 3rd upsert (2 wallets rewrapped, then the batch aborts).
        // Upserts 1 and 2 are the rewrap saves; the failure triggers rollback
        // (2 restore upserts) — but the 3rd COUNTED upsert is the one that fails.
        store.FailOnUpsertNumber = 3;

        var report = await NewService(store).RotateAllAsync(KeyNew);

        report.IsError.Should().BeTrue();
        report.Result!.RolledBack.Should().BeTrue("a mid-batch failure must roll the whole batch back");

        // Every wallet is back to its ORIGINAL ciphertext (readable under the OLD key,
        // NOT the new one) — no wallet left half-rotated.
        var oldSvc = KeyService(KeyOld);
        var newSvc = KeyService(KeyNew);
        foreach (var w in (await store.GetAllAsync()).Result!)
        {
            w.EncryptedPrivateKey.Should().Be(before[w.Id], "ciphertext restored to pre-rotation value");
            var readOld = () => oldSvc.DecryptPrivateKey(w.EncryptedPrivateKey!);
            readOld.Should().NotThrow("restored wallets decrypt under the OLD key");
            var readNew = () => newSvc.DecryptPrivateKey(w.EncryptedPrivateKey!);
            readNew.Should().Throw<System.Exception>("restored wallets must NOT decrypt under the new key");
        }
    }

    // ─── Fail-closed guards ───

    [Fact]
    public async Task RotateAllAsync_rejects_identical_key()
    {
        var store = new FakeWalletStore(System.Array.Empty<IWallet>());
        var report = await NewService(store).RotateAllAsync(KeyOld);
        report.IsError.Should().BeTrue();
        report.Message.Should().Contain("identical");
    }

    [Fact]
    public async Task RotateAllAsync_rejects_empty_new_key()
    {
        var store = new FakeWalletStore(System.Array.Empty<IWallet>());
        var report = await NewService(store).RotateAllAsync("   ");
        report.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task RotateAllAsync_skips_external_wallets_with_no_ciphertext()
    {
        var store = new FakeWalletStore(new IWallet[]
        {
            new Wallet { Id = Guid.NewGuid(), AvatarId = Guid.NewGuid(), WalletType = WalletType.External },
        });
        var report = await NewService(store).RotateAllAsync(KeyNew);
        report.IsError.Should().BeFalse();
        report.Result!.Skipped.Should().Be(1);
        report.Result.Rewrapped.Should().Be(0);
    }

    // ─── HIGH-1: durable pending-rotation marker (key-loss prevention) ───

    [Fact]
    public async Task RotateAllAsync_writes_pending_marker_before_mutating_and_clears_on_success()
    {
        var old = KeyService(KeyOld);
        var store = new FakeWalletStore(new[]
        {
            PlatformWalletUnder(old, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
        });
        var pending = new FakePendingRotationKeyStore();

        var report = await NewService(store, pending).RotateAllAsync(KeyNew);

        report.IsError.Should().BeFalse(report.Message);
        pending.WriteWasCalled.Should().BeTrue("the marker must be persisted before the batch begins");
        pending.ClearWasCalled.Should().BeTrue("the marker is cleared only after a verified success");
        pending.Current.Should().BeNull("a fully-successful rotation leaves no pending marker");
    }

    [Fact]
    public async Task RotateAllAsync_pending_marker_verifier_token_identifies_the_new_key()
    {
        var old = KeyService(KeyOld);
        var store = new FakeWalletStore(new[]
        {
            PlatformWalletUnder(old, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
        });
        // Capture the marker at the moment it is written by making the store fail the
        // readability sweep, so the rotation aborts WITHOUT clearing the marker.
        var pending = new FakePendingRotationKeyStore();
        // GetAll #1 here; inside rotate: #2 = initial listing, #3 = post-rotation sweep.
        store.CorruptWalletId = (await store.GetAllAsync()).Result!.First().Id;
        store.CorruptOnGetAllCall = 3;

        var report = await NewService(store, pending).RotateAllAsync(KeyNew);

        report.IsError.Should().BeTrue("a failed readability sweep must fail the rotation loudly");
        pending.Current.Should().NotBeNull("a failed rotation KEEPS the marker for recovery");

        // The verifier token proves the new key's identity: it decrypts back to the
        // sentinel under the NEW key, and NOT under the old key.
        var token = pending.Current!.VerifierToken;
        KeyService(KeyNew).DecryptSeedPhrase(token).Should().Be("azoa-rotation-key-verifier-v1");
        var readOld = () => KeyService(KeyOld).DecryptSeedPhrase(token);
        readOld.Should().Throw<System.Exception>("only the correct new key reproduces the sentinel");
    }

    [Fact]
    public async Task RotateAllAsync_refuses_to_start_when_pending_marker_cannot_be_persisted()
    {
        var old = KeyService(KeyOld);
        var store = new FakeWalletStore(new[]
        {
            PlatformWalletUnder(old, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
        });
        var before = (await store.GetAllAsync()).Result!.Single().EncryptedPrivateKey;

        var pending = new FakePendingRotationKeyStore { FailWrite = true };
        var report = await NewService(store, pending).RotateAllAsync(KeyNew);

        report.IsError.Should().BeTrue("an unrecorded rotation is the key-loss hazard we defend against");
        report.Message.Should().Contain("pending-rotation marker");
        // No wallet was touched — the ciphertext is unchanged (still under the OLD key).
        (await store.GetAllAsync()).Result!.Single().EncryptedPrivateKey.Should().Be(before);
        store.UpsertCalls.Should().Be(0);
    }

    // ─── HIGH-1: post-rotation readability assertion sweep ───

    [Fact]
    public async Task RotateAllAsync_fails_loudly_when_post_rotation_wallet_is_unreadable()
    {
        var old = KeyService(KeyOld);
        var store = new FakeWalletStore(new[]
        {
            PlatformWalletUnder(old, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
        });
        // Corrupt the wallet's ciphertext at the post-rotation sweep read, so it is not
        // readable under the intended final key even though the rewrap "succeeded".
        // GetAll #1 here; inside rotate: #2 = initial listing, #3 = post-rotation sweep.
        store.CorruptWalletId = (await store.GetAllAsync()).Result!.First().Id;
        store.CorruptOnGetAllCall = 3;
        var pending = new FakePendingRotationKeyStore();

        var report = await NewService(store, pending).RotateAllAsync(KeyNew);

        report.IsError.Should().BeTrue();
        report.Message.Should().Contain("readability sweep");
        pending.Current.Should().NotBeNull("the marker is retained so recovery can use the verifier token");
        pending.ClearWasCalled.Should().BeFalse("a failed sweep must NOT clear the pending marker");
    }
}
