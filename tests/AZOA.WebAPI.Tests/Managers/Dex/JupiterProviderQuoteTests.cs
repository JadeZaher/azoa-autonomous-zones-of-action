using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Solnet.Programs;
using Solnet.Wallet;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed partial class JupiterProviderQuoteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_settlement_requires_dedicated_account_closed_and_exact_owner_SOL_conservation(bool retainTemporary)
    {
        using var fixture = new Fixture(nativeInput: true);
        fixture.Accounts.Remove(fixture.Route.Source);
        var quote = await fixture.Provider.QuoteSwapAsync(fixture.Terms);
        quote.IsError.Should().BeFalse(quote.Message);
        var terms = quote.Result!;
        var parsed = SolanaMessageEnvelope.Read(fixture.Simulated.Single());
        var keys = SolanaMessageEnvelope.Resolve(parsed, [fixture.Table]);
        var addresses = keys.Select(key => key.Key).ToArray();
        var native = JupiterNativeAccount.Create(terms.Owner.Address,
            terms.ProtocolEvidence.GetProperty("executionKey").GetString()!, 2039280, 1000);
        var pre = Enumerable.Repeat(10000000UL, keys.Count).ToArray();
        pre[Array.IndexOf(addresses, native.Address)] = 0;
        var post = pre.ToArray();
        post[Array.IndexOf(addresses, terms.Owner.Address)] -= 6010;
        post[Array.IndexOf(addresses, terms.PlatformFee.TreasuryAddress)] += 10;
        if (retainTemporary) post[Array.IndexOf(addresses, native.Address)] = 1;
        object Token(string amount) => new { accountIndex = Array.IndexOf(addresses, fixture.Route.Destination),
            mint = fixture.Route.Output, owner = terms.Owner.Address, programId = TokenProgram.ProgramIdKey.Key,
            uiTokenAmount = new { amount, decimals = 6 } };
        var meta = JsonSerializer.SerializeToElement(new { preBalances = pre, postBalances = post,
            preTokenBalances = new[] { Token("100") }, postTokenBalances = new[] { Token("2100") } });
        Func<JupiterSettlement> read = () => JupiterSettlementEvidence.Read(terms, fixture.BuildPlan(), [], native, 2039280,
            new(parsed, keys), meta, 5000);
        if (retainTemporary) read.Should().Throw<InvalidDataException>();
        else read().Should().BeEquivalentTo(new JupiterSettlement(1010, 2000, 10, 0, []));
    }

    [Fact]
    public async Task Exposed_absence_requires_all_finalized_parent_linked_blocks_across_persisted_passes()
    {
        using var fixture = new Fixture { LastValidHeight = 100 };
        var quote = await fixture.Provider.QuoteSwapAsync(fixture.Terms);
        var terms = quote.Result!;
        var intent = new SwapIntent(1, "operation", "quote", CanonicalJson.Hash(terms), terms, "request");
        var plan = (await fixture.Provider.PlanSwapStageAsync(intent, "Swap", 0)).Result!;
        var visited = new List<ulong>();
        fixture.ObservationResponse = (method, args) => method switch
        {
            "getSignaturesForAddress" => Array.Empty<object>(),
            "getSlot" => 110UL,
            "getBlock" => Block(args[0].GetUInt64()),
            _ => throw new InvalidOperationException("Unexpected observation request.")
        };
        object Block(ulong slot)
        {
            visited.Add(slot);
            return new { blockhash = Fixture.BlockHash(slot), previousBlockhash = Fixture.BlockHash(slot - 1), parentSlot = slot - 1,
                blockHeight = slot, transactions = Array.Empty<object>() };
        }
        SwapStageObservation? previous = null;
        for (var pass = 0; pass < 3; pass++)
        {
            var observed = await fixture.Provider.ObserveExposedSwapStageAsync(intent, plan, previous: previous);
            observed.IsError.Should().BeFalse(observed.Message);
            previous = observed.Result!.Observation;
            previous.Verdict.Should().Be(pass < 2 ? "Pending" : "ExpiredAbsent");
        }
        visited.Should().Equal(Enumerable.Range(90, 21).Reverse().Select(value => (ulong)value));
        fixture.Provider.ValidateExposedSwapNoEffect(intent, plan, previous!).Should().BeTrue();
        var wrong = plan with { Generation = 1 };
        fixture.Provider.ValidateExposedSwapNoEffect(intent, wrong, previous!).Should().BeFalse();
        fixture.Sent.Should().BeEmpty();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("parent")]
    [InlineData("hash")]
    [InlineData("body")]
    public async Task Incomplete_or_inconsistent_block_history_cannot_become_expired_absence(string failure)
    {
        using var fixture = new Fixture { LastValidHeight = 100 };
        var quote = await fixture.Provider.QuoteSwapAsync(fixture.Terms);
        var terms = quote.Result!;
        var intent = new SwapIntent(1, "operation", "quote", CanonicalJson.Hash(terms), terms, "request");
        var plan = (await fixture.Provider.PlanSwapStageAsync(intent, "Swap", 0)).Result!;
        fixture.ObservationResponse = (method, args) => method switch
        {
            "getSignaturesForAddress" => Array.Empty<object>(), "getSlot" => 110UL,
            "getBlock" => Block(args[0].GetUInt64()),
            _ => throw new InvalidOperationException("Unexpected observation request.")
        };
        object? Block(ulong slot)
        {
            if (failure == "missing" && slot == 109) return null;
            return new { blockhash = failure == "hash" && slot == 109 ? Fixture.BlockHash(999) : Fixture.BlockHash(slot),
                previousBlockhash = Fixture.BlockHash(slot - 1), parentSlot = failure == "parent" ? slot : slot - 1,
                blockHeight = slot, transactions = failure == "body" ? null : Array.Empty<object>() };
        }
        var observed = (await fixture.Provider.ObserveExposedSwapStageAsync(intent, plan)).Result!;
        observed.Observation.Verdict.Should().Be("NeedsOperator");
        observed.Observation.LockedBalance.Should().BeNull();
        fixture.Provider.ValidateExposedSwapNoEffect(intent, plan, observed.Observation).Should().BeFalse();
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("input")]
    [InlineData("output")]
    [InlineData("treasury")]
    [InlineData("native-debit")]
    [InlineData("missing-before")]
    public async Task Finalized_economic_metadata_must_explain_exact_owner_and_treasury_changes(string change)
    {
        using var fixture = new Fixture();
        var quoted = await fixture.Provider.QuoteSwapAsync(fixture.Terms);
        quoted.IsError.Should().BeFalse(quoted.Message);
        var terms = quoted.Result!;
        var parsed = SolanaMessageEnvelope.Read(fixture.Simulated.Single());
        var keys = SolanaMessageEnvelope.Resolve(parsed, [fixture.Table]);
        var accounts = new SolanaObservedTransaction.Accounts(parsed, keys);
        var addresses = keys.Select(key => key.Key).ToArray();
        var pre = Enumerable.Repeat(10000000UL, keys.Count).ToArray();
        var post = pre.ToArray();
        post[Array.IndexOf(addresses, terms.Owner.Address)] -= change == "native-debit" ? 5001UL : 5000UL;
        var treasury = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(new PublicKey(terms.PlatformFee.TreasuryAddress), new PublicKey(fixture.Route.Input)).Key;
        object Balance(string address, string mint, string owner, string amount) => new
        {
            accountIndex = Array.IndexOf(addresses, address), mint, owner, programId = TokenProgram.ProgramIdKey.Key,
            uiTokenAmount = new { amount, decimals = 6 }
        };
        var before = new List<object>
        {
            Balance(fixture.Route.Source, fixture.Route.Input, terms.Owner.Address, "10000"),
            Balance(treasury, fixture.Route.Input, terms.PlatformFee.TreasuryAddress, "0")
        };
        if (change != "missing-before") before.Add(Balance(fixture.Route.Destination, fixture.Route.Output, terms.Owner.Address, "100"));
        var metadata = JsonSerializer.SerializeToElement(new
        {
            preBalances = pre, postBalances = post, preTokenBalances = before,
            postTokenBalances = new[]
            {
                Balance(fixture.Route.Source, fixture.Route.Input, terms.Owner.Address, change == "input" ? "8991" : "8990"),
                Balance(fixture.Route.Destination, fixture.Route.Output, terms.Owner.Address, change == "output" ? "2089" : "2100"),
                Balance(treasury, fixture.Route.Input, terms.PlatformFee.TreasuryAddress, change == "treasury" ? "9" : "10")
            }
        });
        Func<JupiterSettlement> read = () => JupiterSettlementEvidence.Read(terms, fixture.BuildPlan(), [], null, 2039280, accounts, metadata, 5000);
        if (change == "valid") read().Should().BeEquivalentTo(new JupiterSettlement(1010, 2000, 10, 0, []));
        else read.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("Build")]
    [InlineData("Recovery")]
    [InlineData("Snapshot")]
    [InlineData("Guard")]
    [InlineData("Tables")]
    public async Task Explicit_null_saved_evidence_is_a_typed_invalid_plan(string field)
    {
        using var fixture = new Fixture();
        var quote = await fixture.Provider.QuoteSwapAsync(fixture.Terms);
        var terms = quote.Result!;
        var intent = new SwapIntent(1, "operation", "quote", CanonicalJson.Hash(terms), terms, "request");
        var planned = await fixture.Provider.PlanSwapStageAsync(intent, "Swap", 0);
        planned.IsError.Should().BeFalse(planned.Message);
        var metadata = JsonNode.Parse(planned.Result!.RecoveryMetadata)!.AsObject();
        metadata[field] = null;
        var tampered = planned.Result with { RecoveryMetadata = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(metadata)) };
        fixture.Provider.ValidateSignedSwapStage(intent, tampered, "\"\"").IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Each_operation_and_generation_commits_distinct_fixed_width_message_identity()
    {
        using var fixture = new Fixture();
        var quote = await fixture.Provider.QuoteSwapAsync(fixture.Terms);
        var terms = quote.Result!;
        var intent = new SwapIntent(1, "operation", "quote", CanonicalJson.Hash(terms), terms, "request");
        var first = await fixture.Provider.PlanSwapStageAsync(intent, "Swap", 0);
        var next = await fixture.Provider.PlanSwapStageAsync(intent, "Swap", 1);
        var other = await fixture.Provider.PlanSwapStageAsync(intent with { OperationId = "other" }, "Swap", 0);
        first.IsError.Should().BeFalse(first.Message);
        next.IsError.Should().BeFalse(next.Message);
        other.IsError.Should().BeFalse(other.Message);
        first.Result!.UnsignedPayloadJson.Should().NotBe(next.Result!.UnsignedPayloadJson).And.NotBe(other.Result!.UnsignedPayloadJson);
        fixture.Simulated.Select(message => message.Length).Distinct().Should().ContainSingle();
    }

    [Theory]
    [InlineData("wallet")]
    [InlineData("chain")]
    public async Task Wrong_owner_context_is_refused_before_any_custody_or_network_work(string change)
    {
        using var fixture = new Fixture();
        var quote = await fixture.Provider.QuoteSwapAsync(fixture.Terms);
        var terms = quote.Result!;
        var intent = new SwapIntent(1, "operation", "quote", CanonicalJson.Hash(terms), terms, "request");
        var plan = await fixture.Provider.PlanSwapStageAsync(intent, "Swap", 0);
        plan.IsError.Should().BeFalse(plan.Message);
        var before = fixture.Methods.Count;
        var context = SigningContext.ForUser(terms.Owner.UserId,
            change == "wallet" ? Guid.NewGuid() : terms.Owner.WalletId).ForChain(change == "chain" ? "Ethereum" : "Solana")
            with { Scope = AzoaScopes.SwapSign };
        var result = await fixture.Provider.SignSwapStageAsync(intent, plan.Result!, [new(terms.Owner, context)]);
        result.IsError.Should().BeTrue();
        fixture.Methods.Should().HaveCount(before);
        fixture.Sent.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_or_more_expensive_saved_message_refuses_before_custody(bool expired)
    {
        using var fixture = new Fixture();
        var quote = await fixture.Provider.QuoteSwapAsync(fixture.Terms);
        var terms = quote.Result!;
        var intent = new SwapIntent(1, "operation", "quote", CanonicalJson.Hash(terms), terms, "request");
        var plan = await fixture.Provider.PlanSwapStageAsync(intent, "Swap", 0);
        plan.IsError.Should().BeFalse(plan.Message);
        if (expired) fixture.BlockHeight = 501; else fixture.MessageFee = 5001;
        var context = SigningContext.ForUser(terms.Owner.UserId, terms.Owner.WalletId).ForChain("Solana")
            with { Scope = AzoaScopes.SwapSign };
        var result = await fixture.Provider.SignSwapStageAsync(intent, plan.Result!, [new(terms.Owner, context)]);
        result.IsError.Should().BeTrue();
        result.Message.Should().Contain(expired ? "expired" : "reservation");
        fixture.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Signed_submission_replays_identical_real_signature_after_ambiguous_provider_response()
    {
        using var fixture = new Fixture();
        var quote = await fixture.Provider.QuoteSwapAsync(fixture.Terms);
        quote.IsError.Should().BeFalse(quote.Message);
        var terms = quote.Result!;
        var intent = new SwapIntent(1, "operation", "quote", CanonicalJson.Hash(terms), terms, "request");
        var plan = await fixture.Provider.PlanSwapStageAsync(intent, "Swap", 0);
        plan.IsError.Should().BeFalse(plan.Message);
        var message = Convert.FromBase64String(JsonSerializer.Deserialize<string>(plan.Result!.UnsignedPayloadJson)!);
        var wire = SolanaMessageEnvelope.Populate(message, [fixture.Actor.Sign(message)]);
        var prepared = fixture.Provider.ValidateSignedSwapStage(intent, plan.Result, JsonSerializer.Serialize(Convert.ToBase64String(wire)));
        prepared.IsError.Should().BeFalse(prepared.Message);
        fixture.RejectFirstSend = true;
        Func<Task> first = async () => await fixture.Provider.SubmitSwapStageAsync(intent, prepared.Result!);
        await first.Should().ThrowAsync<HttpRequestException>();
        var retry = await fixture.Provider.SubmitSwapStageAsync(intent, prepared.Result!);
        retry.IsError.Should().BeFalse(retry.Message);
        fixture.Sent.Should().HaveCount(2);
        fixture.Sent[0].Should().Be(fixture.Sent[1]);
        fixture.BlockHeight = 501;
        (await fixture.Provider.SubmitSwapStageAsync(intent, prepared.Result!)).IsError.Should().BeFalse();
        fixture.Sent.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preparation_reconstructs_exact_saved_quote_and_native_identity_without_custody(bool nativeInput)
    {
        using var fixture = new Fixture(nativeInput);
        if (nativeInput) fixture.Accounts.Remove(fixture.Route.Source);
        var quote = await fixture.Provider.QuoteSwapAsync(fixture.Terms);
        quote.IsError.Should().BeFalse(quote.Message);
        var terms = quote.Result!;
        var intent = new SwapIntent(1, "operation", "quote", CanonicalJson.Hash(terms), terms, "request");
        var planned = await fixture.Provider.PlanSwapStageAsync(intent, "Swap", 0);
        planned.IsError.Should().BeFalse(planned.Message);
        planned.Result!.IntentHash.Should().Be(CanonicalJson.Hash(intent));
        var message = Convert.FromBase64String(JsonSerializer.Deserialize<string>(planned.Result.UnsignedPayloadJson)!);
        message.Should().Equal(fixture.Simulated.Last());
        fixture.Simulated.Should().HaveCount(2);
        if (nativeInput)
        {
            var parsed = SolanaMessageEnvelope.Read(message);
            var keys = SolanaMessageEnvelope.Resolve(parsed, [fixture.Table]);
            var close = parsed.Instructions.Single(item => keys[item.ProgramIndex].Key == TokenProgram.ProgramIdKey.Key && item.Data[0] == 9);
            keys[close.AccountIndices[0]].Key.Should().Be(terms.Prerequisites.Single(item => item.Kind == "TemporaryWrappedSol").DestinationAddress);
        }
        fixture.Methods.Should().NotContain("sendTransaction");
    }

    [Fact]
    public async Task Preparation_refuses_new_account_creation_absent_from_original_consent()
    {
        using var fixture = new Fixture();
        var quote = await fixture.Provider.QuoteSwapAsync(fixture.Terms);
        quote.IsError.Should().BeFalse(quote.Message);
        var terms = quote.Result!;
        fixture.Accounts.Remove(fixture.Route.Destination);
        var intent = new SwapIntent(1, "operation", "quote", CanonicalJson.Hash(terms), terms, "request");
        (await fixture.Provider.PlanSwapStageAsync(intent, "Swap", 0)).IsError.Should().BeTrue();
        fixture.Methods.Should().NotContain("sendTransaction");
    }

    [Fact]
    public async Task Native_quote_uses_its_disclosed_dedicated_account_with_no_original_WS0L_holding()
    {
        using var fixture = new Fixture(nativeInput: true);
        fixture.Accounts.Remove(fixture.Route.Source);
        var result = await fixture.Provider.QuoteSwapAsync(fixture.Terms);
        result.IsError.Should().BeFalse(result.Message);
        var native = result.Result!.Prerequisites.Single(item => item.Kind == "TemporaryWrappedSol");
        native.RetainedOnSwapFailure.Should().BeFalse();
        native.DestinationAddress.Should().NotBe(fixture.Route.Source);
        var message = SolanaMessageEnvelope.Read(fixture.Simulated.Single());
        var keys = SolanaMessageEnvelope.Resolve(message, [fixture.Table]);
        var close = message.Instructions.Single(instruction => keys[instruction.ProgramIndex].Key == TokenProgram.ProgramIdKey.Key && instruction.Data[0] == 9);
        keys[close.AccountIndices[0]].Key.Should().Be(native.DestinationAddress);
        keys[close.AccountIndices[1]].Key.Should().Be(fixture.Route.Owner);
        keys[close.AccountIndices[2]].Key.Should().Be(fixture.Route.Owner);
        result.Result.ProtocolEvidence.GetProperty("nativeAccountKey").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Existing_temporary_native_address_never_reaches_simulation()
    {
        using var fixture = new Fixture(nativeInput: true) { OccupyTemporaryAccount = true };
        fixture.Accounts.Remove(fixture.Route.Source);
        (await fixture.Provider.QuoteSwapAsync(fixture.Terms)).IsError.Should().BeTrue();
        fixture.Simulated.Should().BeEmpty();
    }

    [Fact]
    public async Task Quote_uses_real_adapter_decoding_rpc_roles_and_exact_simulated_message_without_signing()
    {
        using var fixture = new Fixture();
        var result = await fixture.Provider.QuoteSwapAsync(fixture.Terms);
        result.IsError.Should().BeFalse(result.Message);
        result.Result!.MinimumOutput.Should().Be("1990");
        result.Result.ExpectedOutput.Should().Be("2000");
        result.Result.Prerequisites.Should().BeEmpty();
        fixture.Simulated.Should().ContainSingle();
        var message = SolanaMessageEnvelope.Read(fixture.Simulated[0]);
        message.VersionZero.Should().BeTrue();
        message.Instructions.Should().HaveCount(6);
        message.Instructions[2].Data[0].Should().Be(4);
        fixture.Methods.Should().NotContain("sendTransaction");
    }

    [Fact]
    public async Task Missing_output_ATA_is_disclosed_and_created_in_the_same_guarded_message()
    {
        using var fixture = new Fixture();
        fixture.Accounts.Remove(fixture.Route.Destination);
        var result = await fixture.Provider.QuoteSwapAsync(fixture.Terms);
        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Prerequisites.Should().ContainSingle(item => item.Kind == "CreateAta" && item.RetainedOnSwapFailure);
        SolanaMessageEnvelope.Read(fixture.Simulated.Single()).Instructions.Should().HaveCount(7);
    }

    [Theory]
    [InlineData("program")]
    [InlineData("frozen")]
    [InlineData("mutable-guard")]
    [InlineData("fee-cap")]
    public async Task Changed_evidence_or_cap_never_reaches_simulation(string change)
    {
        using var fixture = new Fixture();
        if (change == "program") fixture.Accounts[JupiterRouteAccounts.Program.Key] = Fixture.Envelope([], SystemProgram.ProgramIdKey.Key);
        if (change == "frozen") fixture.Accounts[fixture.Route.Source] = JupiterRouteAuthorityTests.Account(
            new(fixture.Route.Source, fixture.Route.Input, fixture.Route.Owner), "frozen");
        if (change == "mutable-guard") fixture.MakeGuardMutable();
        var terms = change == "fee-cap" ? fixture.Terms with { Budget = fixture.Terms.Budget with { MaximumNetworkFee = "4999" } } : fixture.Terms;
        (await fixture.Provider.QuoteSwapAsync(terms)).IsError.Should().BeTrue();
        fixture.Simulated.Should().BeEmpty();
    }

    private sealed class Fixture : HttpMessageHandler
    {
        private const string Genesis = "EtWTRABZaYq6iMfeYKouRu166VU2xqa1wcaWoxPkrZBG";
        internal readonly JupiterRouteAccountsTests.Fixture Route;
        internal readonly Dictionary<string, object?> Accounts = new(StringComparer.Ordinal);
        internal readonly List<byte[]> Simulated = [];
        internal readonly List<string> Methods = [];
        internal readonly SolanaProvider Provider;
        internal readonly DexQuoteTerms Terms;
        internal readonly SolanaLookupTableSnapshot Table;
        internal readonly Account Actor = new();
        internal readonly List<string> Sent = [];
        internal bool RejectFirstSend;
        internal long BlockHeight = 100;
        internal ulong MessageFee = 5000;
        internal long LastValidHeight = 500;
        internal Func<string, JsonElement, object?>? ObservationResponse;
        internal bool OccupyTemporaryAccount;
        private readonly PublicKey _guard = Key(40);
        private readonly PublicKey _table = Key(41);
        private readonly byte[] _guardBytes;
        private readonly string[] _tableKeys;

        internal Fixture(bool nativeInput = false)
        {
            Route = new(false, false, true, ownerAtas: true, mintA: nativeInput ? JupiterNativeAccount.Mint.Key : null,
                mintB: nativeInput ? Key(200).Key : null, owner: Actor.PublicKey.Key);
            var roles = Route.Validate();
            foreach (var (address, bytes) in Route.Data) Accounts[address] = Envelope(bytes, WhirlpoolAccountState.Program.Key);
            foreach (var role in roles.TokenAccounts) Accounts[role.Address] = JupiterRouteAuthorityTests.Account(role);
            foreach (var mint in new[] { Route.Input, Route.Output })
            {
                var bytes = new byte[82]; bytes[44] = mint == JupiterNativeAccount.Mint.Key ? (byte)9 : (byte)6; bytes[45] = 1;
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(36), 1000000);
                Accounts[mint] = Envelope(bytes, TokenProgram.ProgramIdKey.Key);
            }
            Accounts[Route.Owner] = Envelope([], SystemProgram.ProgramIdKey.Key);
            var treasury = Key(42);
            var treasuryAta = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(treasury, new PublicKey(Route.Input)).Key;
            Accounts[treasuryAta] = JupiterRouteAuthorityTests.Account(new(treasuryAta, Route.Input, treasury.Key));
            byte[] code = [1, 2, 3, 4];
            var hash = Convert.ToHexStringLower(SHA256.HashData(code));
            _guardBytes = [];
            foreach (var program in new[] { JupiterRouteAccounts.Program, WhirlpoolAccountState.Program, _guard })
            {
                var header = new byte[36]; header[0] = 2;
                SolanaNativeLoader.ProgramData(program).KeyBytes.CopyTo(header, 4);
                Accounts[program.Key] = Envelope(header, SolanaNativeLoader.Program.Key, true);
                var data = new byte[49]; data[0] = 3; BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(4), 50); code.CopyTo(data, 45);
                Accounts[SolanaNativeLoader.ProgramData(program).Key] = Envelope(data, SolanaNativeLoader.Program.Key);
                if (program == _guard) _guardBytes = data;
            }
            var clock = new byte[40]; BinaryPrimitives.WriteUInt64LittleEndian(clock, 90);
            BinaryPrimitives.WriteInt64LittleEndian(clock.AsSpan(32), 1000);
            Accounts[SysVars.ClockKey.Key] = Envelope(clock, "Sysvar1111111111111111111111111111111111111");
            var rent = new byte[17]; BinaryPrimitives.WriteUInt64LittleEndian(rent, 3480);
            BinaryPrimitives.WriteDoubleLittleEndian(rent.AsSpan(8), 2); rent[16] = 50;
            Accounts[SysVars.RentKey.Key] = Envelope(rent, "Sysvar1111111111111111111111111111111111111");
            _tableKeys = Accounts.Keys.Concat(Route.Accounts).Distinct(StringComparer.Ordinal).ToArray();
            var table = new byte[56 + _tableKeys.Length * 32]; table[0] = 1;
            BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(4), ulong.MaxValue);
            for (var index = 0; index < _tableKeys.Length; index++) new PublicKey(_tableKeys[index]).KeyBytes.CopyTo(table, 56 + index * 32);
            Accounts[_table.Key] = Envelope(table, SolanaLookupTableSnapshot.Program);
            Table = new(_table.Key, SolanaLookupTableSnapshot.Program, Convert.ToBase64String(table), 90);
            var prefix = "AZOA:Solana:Dex:Networks:Devnet:";
            var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:DefaultNetwork"] = "Devnet", ["Blockchain:Chains:0:ChainType"] = "Solana",
                ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://jupiter-rpc.test/", ["AZOA:Solana:RpcRetryMaxAttempts"] = "0",
                ["AZOA:Solana:Networks:Devnet:GenesisHash"] = Genesis, ["AZOA:Solana:Dex:JupiterApiKey"] = "fixture-only",
                [prefix + "Enabled"] = "true", [prefix + "MaximumComputeUnits"] = "1400000", [prefix + "MicroLamportsPerUnit"] = "0",
                [prefix + "JupiterProgramDataHash"] = hash, [prefix + "WhirlpoolProgramDataHash"] = hash,
                [prefix + "CostGuardProgram"] = _guard.Key, [prefix + "CostGuardProgramDataHash"] = hash
            }).Build();
            Provider = new(config, NullLogger<SolanaProvider>.Instance, null, null, null, this);
            var actor = Guid.NewGuid(); var owner = new DexParticipant(actor, Guid.NewGuid(), Route.Owner);
            Terms = new(1, actor, null, owner, owner, owner.Address, new("Solana", "Devnet", "solana:" + Genesis),
                new(nativeInput ? "SOL" : Route.Input, "IN", nativeInput ? 9 : 6, nativeInput), new(Route.Output, "OUT", 6, false), "1010", "1000", "0", "0", 50,
                new(NodeFeeSchedule.InputBaseUnitsV1, 1, "10", 0, "10", treasury.Key), new("100000", "10000000", "100000"),
                "Jupiter", "6-whirlpool/1", JsonSerializer.SerializeToElement(new { }), DateTimeOffset.UtcNow, []);
        }

        internal void MakeGuardMutable()
        {
            _guardBytes[12] = 1;
            Accounts[SolanaNativeLoader.ProgramData(_guard).Key] = Envelope(_guardBytes, SolanaNativeLoader.Program.Key);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
            {
                request.RequestUri!.Host.Should().Be("api.jup.ag");
                request.Headers.GetValues("x-api-key").Should().Equal("fixture-only");
                return Response(Build());
            }
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = document.RootElement; var method = root.GetProperty("method").GetString()!; var args = root.GetProperty("params");
            Methods.Add(method);
            if (ObservationResponse is not null && method is "getSignaturesForAddress" or "getSlot" or "getBlock" or "getTransaction")
                return Response(new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt32(), result = ObservationResponse(method, args) });
            if (method == "sendTransaction")
            {
                var wire = args[0].GetString()!;
                Sent.Add(wire);
                if (RejectFirstSend && Sent.Count == 1)
                    return Response(new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt32(), error = new { code = -32000, message = "ambiguous fixture response" } });
                var bytes = Convert.FromBase64String(wire);
                var signature = SolanaMessageEnvelope.VerifyWire(bytes, bytes[65..]);
                return Response(new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt32(), result = signature });
            }
            object result = method switch
            {
                "getGenesisHash" => Genesis,
                "getBlockHeight" => BlockHeight,
                "getLatestBlockhash" => new { context = new { slot = 90 }, value = new { blockhash = Key(43).Key, lastValidBlockHeight = LastValidHeight } },
                "getMultipleAccounts" => new { context = new { slot = 90 }, value = args[0].EnumerateArray().Select(address => ReadAccount(address.GetString()!)).ToArray() },
                "getFeeForMessage" => new { context = new { slot = 90 }, value = MessageFee },
                "simulateTransaction" => Simulate(args),
                _ => throw new InvalidOperationException("Unexpected signing or send during quote: " + method)
            };
            return Response(new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt32(), result });
        }

        private object? ReadAccount(string address)
        {
            if (Accounts.TryGetValue(address, out var account)) return account;
            return OccupyTemporaryAccount && !Route.Accounts.Contains(address)
                ? Envelope([], SystemProgram.ProgramIdKey.Key) : null;
        }

        private object Simulate(JsonElement args)
        {
            args[1].GetProperty("sigVerify").GetBoolean().Should().BeFalse();
            var wire = Convert.FromBase64String(args[0].GetString()!);
            wire.Skip(1).Take(wire[0] * 64).Should().OnlyContain(value => value == 0);
            Simulated.Add(wire[(1 + wire[0] * 64)..]);
            return new { context = new { slot = 90 }, value = new { err = (object?)null, unitsConsumed = 100000 } };
        }

        internal static string BlockHash(ulong slot)
        {
            var bytes = new byte[32]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, slot); return new PublicKey(bytes).Key;
        }

        internal JupiterBuildPlan BuildPlan() => JupiterBuildPlanCodec.Read(JsonSerializer.SerializeToElement(Build()), Route.Input, Route.Output, 1000, 50);

        private object Build()
        {
            var roles = Route.Validate();
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(new byte[] { 229, 23, 203, 151, 122, 227, 173, 42 }); writer.Write(1U);
            writer.Write(new byte[] { 17, 1, 100, 0, 1 }); writer.Write(1000UL); writer.Write(2000UL); writer.Write((ushort)50); writer.Write((byte)0);
            return new
            {
                inputMint = Route.Input, outputMint = Route.Output, inAmount = "1000", outAmount = "2000", otherAmountThreshold = "1990",
                swapMode = "ExactIn", slippageBps = 50, transactionVersion = 0,
                routePlan = new[] { new { percent = 100, bps = 10000, swapInfo = new { label = "Whirlpool", ammKey = Route.Pool.Address,
                    inputMint = Route.Input, outputMint = Route.Output, inAmount = "1000", outAmount = "2000" } } },
                setupInstructions = Array.Empty<object>(), computeBudgetInstructions = Array.Empty<object>(), otherInstructions = Array.Empty<object>(),
                swapInstruction = new { programId = JupiterRouteAccounts.Program.Key, data = Convert.ToBase64String(stream.ToArray()),
                    accounts = Route.Accounts.Select(address => new { pubkey = address, isSigner = address == Route.Owner, isWritable = roles.RequiredWritable.Contains(address) }).ToArray() },
                addressesByLookupTableAddress = new Dictionary<string, string[]> { [_table.Key] = _tableKeys }
            };
        }

        internal static object Envelope(byte[] bytes, string owner, bool executable = false)
            => new { owner, executable, data = new[] { Convert.ToBase64String(bytes), "base64" }, lamports = 1000000000UL };
        private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json") };
        private static PublicKey Key(byte value) => new(Enumerable.Repeat(value, 32).ToArray());
    }
}

