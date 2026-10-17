using System.Buffers.Binary;
using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using FluentAssertions;
using Solnet.Programs;
using Solnet.Rpc.Builders;
using Solnet.Wallet;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed partial class JupiterProviderQuoteTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Cleanup_reconstructs_original_wire_and_releases_only_attributed_rent_after_complete_ancestry(bool missingBlock, bool closedAndReopened)
    {
        using var fixture = new Fixture();
        fixture.Accounts.Remove(fixture.Route.Destination);
        var quoted = await fixture.Provider.QuoteSwapAsync(fixture.Terms);
        quoted.IsError.Should().BeFalse(quoted.Message);
        var terms = quoted.Result!;
        var intent = new SwapIntent(1, "cleanup-operation", "quote", CanonicalJson.Hash(terms), terms, "request");
        var planned = await fixture.Provider.PlanSwapStageAsync(intent, "Swap", 0);
        planned.IsError.Should().BeFalse(planned.Message);
        var original = Sign(planned.Result!);
        var originalTx = Transaction(original, 100, false);
        PreparedSwapStage? cleanup = null;
        object? cleanupTx = null;
        object? externalTx = null;
        string? externalId = null;
        fixture.ObservationResponse = (method, args) => method switch
        {
            "getTransaction" => args[0].GetString() == original.TransactionIds[0] ? originalTx : cleanupTx,
            "getBlock" => Block(args[0].GetUInt64(), args[1].GetProperty("transactionDetails").GetString()!),
            _ => throw new InvalidOperationException("Unexpected cleanup RPC " + method)
        };
        var settled = await fixture.Provider.ObserveSwapStageAsync(intent, original);
        settled.Result!.Verdict.Should().Be("Confirmed");
        settled.Result.LockedBalance.Should().Be("2039280");
        var holding = new byte[165]; new PublicKey(fixture.Route.Output).KeyBytes.CopyTo(holding, 0);
        fixture.Actor.PublicKey.KeyBytes.CopyTo(holding, 32); holding[108] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(holding.AsSpan(64), 0);
        fixture.Accounts[fixture.Route.Destination] = Fixture.Envelope(holding, TokenProgram.ProgramIdKey.Key);
        var consent = new SwapCleanupConsent(1, intent.OperationId, intent.QuoteHash, new string('a', 64), terms.ActorUserId,
            null, terms.Owner, terms.OutputAsset.Id, "CloseCurrentZeroHolding", "100000", true);
        var cleanPlan = await fixture.Provider.PlanSwapCleanupAsync(intent, [new(original, settled.Result)], consent, "CloseAta", 0);
        cleanPlan.IsError.Should().BeFalse(cleanPlan.Message);
        cleanup = Sign(cleanPlan.Result!);
        cleanupTx = Transaction(cleanup, 120, true);
        if (closedAndReopened)
        {
            var owner = fixture.Actor.PublicKey;
            var account = new PublicKey(fixture.Route.Destination);
            var externalMessage = new TransactionBuilder().SetFeePayer(owner).SetRecentBlockHash(Fixture.BlockHash(110))
                .AddInstruction(TokenProgram.CloseAccount(account, owner, owner, TokenProgram.ProgramIdKey))
                .AddInstruction(JupiterCompiledMessage.ToInstruction(JupiterSetupInstructions.CreateOwnerAta(owner.Key, fixture.Route.Output)))
                .CompileMessage();
            var externalWire = SolanaMessageEnvelope.Populate(externalMessage, [fixture.Actor.Sign(externalMessage)]);
            externalId = SolanaMessageEnvelope.VerifyWire(externalWire, externalMessage);
            externalTx = new { transaction = new[] { Convert.ToBase64String(externalWire), "base64" },
                meta = new { err = (object?)null, innerInstructions = Array.Empty<object>() } };
        }
        var observed = await fixture.Provider.ObserveSwapStageAsync(intent, cleanup);
        observed.Result!.Verdict.Should().Be("Confirmed");
        observed.Result.LockedBalance.Should().Be("0", "closure does not itself credit the historical allocation");
        string? checkpoint = null;
        SwapRentReleaseProgress? progress = null;
        for (var pass = 0; pass < 3; pass++)
        {
            var scan = await fixture.Provider.ScanSwapRentReleaseAsync(intent, original, cleanup, checkpoint);
            if (missingBlock)
            {
                scan.IsError.Should().BeTrue(); scan.Result.Should().BeNull(); break;
            }
            scan.IsError.Should().BeFalse(scan.Message);
            if (pass == 0)
            {
                var repeated = await fixture.Provider.ScanSwapRentReleaseAsync(intent, original, cleanup, checkpoint);
                repeated.IsError.Should().BeFalse(repeated.Message);
                repeated.Result.Should().BeEquivalentTo(scan.Result, "lost progress replays the same fixed finalized history page");
            }
            progress = scan.Result!; checkpoint = progress.CheckpointJson;
            progress.Complete.Should().Be(pass == 2);
            if (!progress.Complete) progress.ReleasedLockedBalance.Should().BeNull();
        }
        if (!missingBlock)
        {
            progress!.ReleasedLockedBalance.Should().Be("2039280");
            using var evidence = JsonDocument.Parse(progress.EvidenceJson!);
            evidence.RootElement.GetProperty("Earliest").GetProperty("Signature").GetString()
                .Should().Be(closedAndReopened ? externalId : cleanup.TransactionIds[0]);
        }
        fixture.Sent.Should().BeEmpty();

        PreparedSwapStage Sign(SwapStagePlan plan)
        {
            var message = Convert.FromBase64String(JsonSerializer.Deserialize<string>(plan.UnsignedPayloadJson)!);
            var wire = SolanaMessageEnvelope.Populate(message, [fixture.Actor.Sign(message)]);
            var result = fixture.Provider.ValidateSignedSwapStage(intent, plan, JsonSerializer.Serialize(Convert.ToBase64String(wire)));
            result.IsError.Should().BeFalse(result.Message);
            return result.Result!;
        }

        object Transaction(PreparedSwapStage stage, ulong slot, bool close)
        {
            var message = Convert.FromBase64String(JsonSerializer.Deserialize<string>(stage.Plan.UnsignedPayloadJson)!);
            var parsed = SolanaMessageEnvelope.Read(message);
            var keys = SolanaMessageEnvelope.Resolve(parsed, close ? [] : [fixture.Table]);
            var addresses = keys.Select(key => key.Key).ToArray();
            var ownerIndex = Array.IndexOf(addresses, terms.Owner.Address);
            var destinationIndex = Array.IndexOf(addresses, fixture.Route.Destination);
            var pre = Enumerable.Repeat(10_000_000UL, keys.Count).ToArray(); var post = pre.ToArray();
            pre[destinationIndex] = close ? 2039280UL : 0;
            post[destinationIndex] = close ? 0 : 2039280UL;
            post[ownerIndex] = close ? pre[ownerIndex] + 2039280 - 5000 : pre[ownerIndex] - 2039280 - 5000;
            object Balance(string address, string mint, string owner, string amount) => new
            {
                accountIndex = Array.IndexOf(addresses, address), mint, owner, programId = TokenProgram.ProgramIdKey.Key,
                uiTokenAmount = new { amount, decimals = 6 }
            };
            var treasury = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(new PublicKey(terms.PlatformFee.TreasuryAddress), new PublicKey(fixture.Route.Input)).Key;
            var before = close ? new[] { Balance(fixture.Route.Destination, fixture.Route.Output, terms.Owner.Address, "0") }
                : new[] { Balance(fixture.Route.Source, fixture.Route.Input, terms.Owner.Address, "100000"), Balance(treasury, fixture.Route.Input, terms.PlatformFee.TreasuryAddress, "0") };
            var after = close ? Array.Empty<object>() : new[] { Balance(fixture.Route.Source, fixture.Route.Input, terms.Owner.Address, "98990"),
                Balance(treasury, fixture.Route.Input, terms.PlatformFee.TreasuryAddress, "10"), Balance(fixture.Route.Destination, fixture.Route.Output, terms.Owner.Address, "2000") };
            var loaded = keys.Skip(parsed.StaticKeys.Count).Select(key => key.Key).ToArray();
            var writable = parsed.Lookups.Sum(table => table.WritableIndices.Length);
            return new { slot, transaction = new[] { JsonSerializer.Deserialize<string>(stage.SignedPayloadJson)!, "base64" },
                meta = new { err = (object?)null, fee = 5000UL, preBalances = pre, postBalances = post, preTokenBalances = before,
                    postTokenBalances = after, innerInstructions = Array.Empty<object>(),
                    loadedAddresses = new { writable = loaded.Take(writable).ToArray(), @readonly = loaded.Skip(writable).ToArray() } } };
        }

        object? Block(ulong slot, string details)
        {
            if (missingBlock && slot == 119 && details == "full") return null;
            var rows = slot == 100 ? new[] { originalTx } : slot == 120 && cleanupTx is not null
                ? externalTx is null ? new[] { cleanupTx } : new[] { externalTx, cleanupTx } : Array.Empty<object>();
            var signatures = slot == 100 ? original.TransactionIds : slot == 120 && cleanupTx is not null && cleanup is not null
                ? externalId is null ? cleanup.TransactionIds : new[] { externalId, cleanup.TransactionIds[0] } : [];
            return new { blockhash = Fixture.BlockHash(slot), previousBlockhash = Fixture.BlockHash(slot - 1), parentSlot = slot - 1,
                blockHeight = slot, transactions = rows, signatures };
        }
    }
}
