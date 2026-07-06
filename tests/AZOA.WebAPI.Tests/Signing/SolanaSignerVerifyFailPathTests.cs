using System.Collections.Generic;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Services.Signing;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using Solnet.Rpc.Builders;
using Solnet.Rpc.Models;
using Solnet.Programs;
using Xunit;
using SolanaAccount = Solnet.Wallet.Account;

namespace AZOA.WebAPI.Tests.Signing;

/// <summary>
/// Test gap #1 (final-hardening-cutover G2): the defence-in-depth
/// `VerifySignatures()`-fail branch in <see cref="SolanaTransactionSigner"/>
/// (lines "Assembled Solana transaction failed signature verification.").
///
/// REACHABILITY ANALYSIS — the branch is NOT reachable through the public `Sign`
/// input once the preceding guards pass:
///   1. the key is exactly 64 bytes,
///   2. `Message.AccountKeys[0]` equals the signer's public key,
///   3. `account.Sign(canonicalTxn)` returns a fresh valid 64-byte Ed25519 signature
///      over the SAME `Message` bytes that `Transaction.Populate(message, [sig])`
///      then re-serializes and verifies.
/// Solnet signs and verifies over the message's own canonical serialization, so a
/// signature produced here always verifies. Even feeding non-canonical bytes
/// (trailing junk after a valid message) does not trigger it: Solnet's parser
/// normalises the message and `VerifySignatures()` still passes (empirically
/// confirmed — such input returns a valid transaction, not the fail branch).
/// Forcing `false` would require corrupting Solnet's internal signature/account
/// state via reflection, which would test the mock, not the signer. The guard is
/// therefore kept as belt-and-suspenders and this branch is intentionally left
/// uncovered (documented, not silently skipped).
///
/// What we CAN and DO assert positively: the guard is wired on the success path —
/// a legitimately signed transaction verifies — so the branch cannot silently pass
/// an unverifiable transaction. (The negative branch is proven unreachable above.)
/// </summary>
public class SolanaSignerVerifyFailPathTests
{
    private const string DummyBlockhash = "9ZNTfG4NyQgxy2SWjSiQoUyBPEvXT2xo7fKc5hPYYJ7b";

    private static (SolanaAccount from, byte[] secret) NewAccount()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZOA:WalletEncryptionKey"] = "unit-test-wallet-encryption-key-0123456789",
            })
            .Build();
        var svc = new WalletKeyService(config);
        var (_, privateKeyHex, _, _) = svc.GenerateKeypair("solana");
        var secret = Convert.FromHexString(privateKeyHex);
        var pub = new byte[32];
        Array.Copy(secret, 32, pub, 0, 32);
        return (new SolanaAccount(secret, pub), secret);
    }

    [Fact]
    public void Sign_success_path_passes_the_VerifySignatures_gate()
    {
        var (from, secret) = NewAccount();
        var to = new SolanaAccount();

        byte[] message = new TransactionBuilder()
            .SetRecentBlockHash(DummyBlockhash)
            .SetFeePayer(from)
            .AddInstruction(SystemProgram.Transfer(from.PublicKey, to.PublicKey, 1_000_000))
            .CompileMessage();

        using var key = new SigningKeyMaterial(secret);
        var result = new SolanaTransactionSigner().Sign(message, key);

        // The signer only returns Ok AFTER VerifySignatures() passes — so a non-error
        // result proves the gate was exercised and satisfied on the success path. The
        // returned transaction must itself independently verify (no unverifiable tx
        // can escape the gate).
        result.IsError.Should().BeFalse(result.Message);
        result.Result.Should().NotBeNull();
        Transaction.Deserialize(result.Result!).VerifySignatures().Should().BeTrue(
            "the VerifySignatures() gate guarantees only a verifiable transaction is returned");
    }

    [Fact(Skip = "VerifySignatures()-false branch is unreachable via public Sign input once the key-length + " +
                 "fee-payer-match + fresh-valid-signature guards pass; forcing it would require reflection-corrupting " +
                 "Solnet internal state (testing the mock, not the signer). See class-level reachability analysis.")]
    public void Sign_fails_closed_when_assembled_signature_does_not_verify()
    {
        // Intentionally unimplemented — documented unreachable. See [Skip] reason.
    }
}
