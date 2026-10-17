using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Lending;
using FluentAssertions;
using Xunit;

namespace AZOA.WebAPI.Tests.Actions;

/// <summary>
/// Golden DRIFT test for <see cref="ActionNodeOutputSchema"/>: serializes a
/// representative instance of the REAL output DTO each handler writes to
/// <see cref="ActionNodeExecution.Output"/> (with <see cref="ActionNodeJson.Options"/>)
/// and pins that every declared field exists with a compatible JSON kind, that closed
/// shapes carry no undeclared top-level field, and that every enum value is mapped.
/// Compile-time DTO refs (<see cref="BridgeTransactionResult"/>, <see cref="AZOAResult{T}"/>)
/// mean a rename breaks this test. See Services/Actions/AGENTS.md §output-schema.
/// </summary>
public class ActionNodeOutputSchemaGoldenTests
{
    // ── shape taxonomy ────────────────────────────────────────────────────────
    // How each mapped ActionNodeType's Output is produced, so the test can pick the
    // right representative DTO to serialize. Open/anonymous entries are EXCLUDED
    // from field-level checks (documented per-entry below) — their output is
    // free-form or dynamic-keyed and not statically verifiable.

    /// <summary>Wrapped entries: handler serializes the WHOLE AZOAResult&lt;T&gt;;
    /// the serialized T governs the wrapper's Result JSON kind.</summary>
    private static readonly Dictionary<ActionNodeType, OutputFieldType> WrappedResultKinds = new()
    {
        // Result = domain object
        [ActionNodeType.HolonCreate] = OutputFieldType.Object,
        [ActionNodeType.HolonUpdate] = OutputFieldType.Object,
        [ActionNodeType.HolonGet] = OutputFieldType.Object,
        [ActionNodeType.HolonInteract] = OutputFieldType.Object,
        [ActionNodeType.HolonClone] = OutputFieldType.Object,
        [ActionNodeType.HolonCompose] = OutputFieldType.Object,
        [ActionNodeType.NftMint] = OutputFieldType.Object,
        [ActionNodeType.NftTransfer] = OutputFieldType.Object,
        [ActionNodeType.NftBurn] = OutputFieldType.Object,
        [ActionNodeType.NftGet] = OutputFieldType.Object,
        [ActionNodeType.NftGetMetadata] = OutputFieldType.Object,
        [ActionNodeType.WalletCreate] = OutputFieldType.Object,
        [ActionNodeType.WalletUpdate] = OutputFieldType.Object,
        [ActionNodeType.WalletGet] = OutputFieldType.Object,
        [ActionNodeType.WalletGetPortfolio] = OutputFieldType.Object,
        [ActionNodeType.ActionGraphBundleGenerate] = OutputFieldType.Object,
        [ActionNodeType.ActionGraphBundleDeploy] = OutputFieldType.Object,
        [ActionNodeType.Search] = OutputFieldType.Object,
        [ActionNodeType.UserNFTGetComposite] = OutputFieldType.Object,
        [ActionNodeType.BlockchainExecute] = OutputFieldType.Object,
        [ActionNodeType.Grant] = OutputFieldType.Object,
        [ActionNodeType.Transfer] = OutputFieldType.Object,
        [ActionNodeType.Refund] = OutputFieldType.Object,
        [ActionNodeType.FungibleTokenCreate] = OutputFieldType.Object,
        // Result = list
        [ActionNodeType.HolonQuery] = OutputFieldType.Array,
        [ActionNodeType.HolonGetChildren] = OutputFieldType.Array,
        [ActionNodeType.HolonGetPeers] = OutputFieldType.Array,
        [ActionNodeType.HolonGetAncestors] = OutputFieldType.Array,
        [ActionNodeType.HolonGetDescendants] = OutputFieldType.Array,
        [ActionNodeType.NftQuery] = OutputFieldType.Array,
        [ActionNodeType.WalletQuery] = OutputFieldType.Array,
        // Result = scalar
        [ActionNodeType.HolonDelete] = OutputFieldType.Boolean,
        [ActionNodeType.HolonMoveSubtree] = OutputFieldType.Boolean,
        [ActionNodeType.WalletDelete] = OutputFieldType.Boolean,
        [ActionNodeType.WalletSetDefault] = OutputFieldType.Boolean,
        [ActionNodeType.HolonPropagate] = OutputFieldType.Number,
    };

    /// <summary>Flat entries: handler serializes r.Result DIRECTLY (no wrapper).</summary>
    private static readonly HashSet<ActionNodeType> FlatBridgeShape = new()
    {
        ActionNodeType.Bridge, ActionNodeType.Back,
    };

    /// <summary>Flat known entries (Phase 5): the handler serializes a domain DTO or a
    /// fixed-key dictionary DIRECTLY -- no AZOAResult wrapper, no dynamic keys. Each
    /// value here reproduces exactly what the handler writes, so the closed-shape check
    /// catches a schema entry that drifted from the handler in either direction.</summary>
    private static readonly Dictionary<ActionNodeType, Func<string>> FlatKnown = new()
    {
        // CredentialRevokeNodeHandler serializes r.Result, a CredentialRevokeResult
        // (compile-time ref, so a property rename breaks this test).
        [ActionNodeType.CredentialRevoke] = () => Serialize(RepresentativeCredentialRevoke()),
        // The three governance handlers each build a literal Dictionary<string, object?>.
        [ActionNodeType.GovernanceProposalCreate] = () => Serialize(RepresentativeProposalCreated()),
        [ActionNodeType.GovernanceVote] = () => Serialize(RepresentativeVoteCast()),
        [ActionNodeType.GovernanceTally] = () => Serialize(RepresentativeTally()),
        // All three loan handlers serialize the same LoanBalanceProjectionResult.
        [ActionNodeType.LoanOpen] = () => Serialize(RepresentativeLoanProjection()),
        [ActionNodeType.LoanRepaymentRecord] = () => Serialize(RepresentativeLoanProjection()),
        [ActionNodeType.LoanBalance] = () => Serialize(RepresentativeLoanProjection()),
        // All four hold handlers serialize the same flat HoldNodeResult.
        [ActionNodeType.Hold] = () => Serialize(RepresentativeHold()),
        [ActionNodeType.Release] = () => Serialize(RepresentativeHold()),
        [ActionNodeType.HoldRefund] = () => Serialize(RepresentativeHold()),
        [ActionNodeType.ReleaseIntoHold] = () => Serialize(RepresentativeHold()),
        // SwapActionResolver serializes the flat SwapActionOutput from the confirmed receipt only.
        [ActionNodeType.Swap] = () => Serialize(new AZOA.WebAPI.Models.Dex.SwapActionOutput("op", "Ethereum", "Devnet", "UniswapV3",
            "0x0000000000000000000000000000000000000006", "ETH", "1010", "1990", "10", ["0xabc"], "0xabc", [])),
    };

    /// <summary>Open / anonymous / dynamic-key entries — EXCLUDED from field checks.
    /// Condition passes node config verbatim; ComposeOutputs emits a dynamic
    /// {nodeName: outputJson} map; Emit serializes the tenant payload directly.</summary>
    private static readonly HashSet<ActionNodeType> OpenOrDynamic = new()
    {
        ActionNodeType.Condition, ActionNodeType.ComposeOutputs, ActionNodeType.Emit,
    };

    // ── 1. exhaustiveness: every enum value is mapped (no NotSupportedException) ─

    [Fact]
    public void EveryNodeType_HasAShapeEntry()
    {
        foreach (var type in Enum.GetValues<ActionNodeType>())
        {
            var act = () => ActionNodeOutputSchema.GetShape(type);
            act.Should().NotThrow($"ActionNodeType.{type} must have an output-schema entry");
        }
    }

    /// <summary>Guards the test's own taxonomy against enum growth: every mapped type
    /// is classified into exactly one bucket here, so a new node type forces the
    /// author to declare its output shape in this test too (not just the schema).</summary>
    [Fact]
    public void EveryNodeType_IsClassifiedByThisTest()
    {
        foreach (var type in Enum.GetValues<ActionNodeType>())
        {
            var buckets =
                (WrappedResultKinds.ContainsKey(type) ? 1 : 0) +
                (FlatBridgeShape.Contains(type) ? 1 : 0) +
                (FlatKnown.ContainsKey(type) ? 1 : 0) +
                (OpenOrDynamic.Contains(type) ? 1 : 0) +
                (type == ActionNodeType.GateCheck ? 1 : 0);
            buckets.Should().Be(1,
                $"ActionNodeType.{type} must be classified in exactly one golden-test bucket");
        }
    }

    // ── 2. no live handler maps to NodeOutputShape.None ────────────────────────
    // Every production node writes a known/wrapped/flat/literal shape or is Open.
    // A None entry (Open==false, empty Fields) would be a pure side-effect node with
    // no live handler — flag it so the None branch never silently masks a real output.

    [Fact]
    public void NoMappedNodeType_IsNoneShape()
    {
        foreach (var type in Enum.GetValues<ActionNodeType>())
        {
            var shape = ActionNodeOutputSchema.GetShape(type);
            var isNone = !shape.Open && shape.Fields.Count == 0;
            isNone.Should().BeFalse(
                $"ActionNodeType.{type} maps to NodeOutputShape.None but no pure-side-effect handler exists");
        }
    }

    // ── 3. wrapped shapes match the serialized AZOAResult<T> wrapper ────────────

    [Theory]
    [MemberData(nameof(WrappedTypes))]
    public void WrappedShape_MatchesSerializedAZOAResult(ActionNodeType type)
    {
        var resultKind = WrappedResultKinds[type];
        var json = resultKind switch
        {
            // Representative T governs the Result JSON kind; other wrapper fields are T-invariant.
            OutputFieldType.Array => Serialize(Wrap(new[] { new object() })),
            OutputFieldType.Boolean => Serialize(Wrap(true)),
            OutputFieldType.Number => Serialize(Wrap(1)),
            _ => Serialize(Wrap<object>(new { placeholder = 1 })),
        };

        AssertShape(type, json, closed: true, resultKindOverride: resultKind);
    }

    public static IEnumerable<object[]> WrappedTypes() =>
        WrappedResultKinds.Keys.Select(t => new object[] { t });

    // ── 4. Bridge/Back match the flat BridgeTransactionResult DTO ──────────────

    [Theory]
    [MemberData(nameof(FlatTypes))]
    public void FlatBridgeShape_MatchesSerializedBridgeTransactionResult(ActionNodeType type)
    {
        var json = Serialize(RepresentativeBridge());
        AssertShape(type, json, closed: true);
    }

    public static IEnumerable<object[]> FlatTypes() =>
        FlatBridgeShape.Select(t => new object[] { t });

    // -- 4b. Phase 5 flat shapes match the DTO / dictionary the handler writes --

    [Theory]
    [MemberData(nameof(FlatKnownTypes))]
    public void FlatKnownShape_MatchesSerializedHandlerOutput(ActionNodeType type)
    {
        AssertShape(type, FlatKnown[type](), closed: true);
    }

    public static IEnumerable<object[]> FlatKnownTypes() =>
        FlatKnown.Keys.Select(t => new object[] { t });

    [Fact]
    public void FlatBridgeOutput_DoesNotSerializeServerIdempotencyKey()
    {
        var json = Serialize(new BridgeTransactionResult
        {
            IdempotencyKey = "bridge:private-user:payment-intent",
        });

        using var document = JsonDocument.Parse(json);
        document.RootElement.TryGetProperty(nameof(BridgeTransactionResult.IdempotencyKey), out _)
            .Should().BeFalse();
    }

    // ── 5. GateCheck matches its fixed literal {"pass":true} ───────────────────

    [Fact]
    public void GateCheckShape_MatchesLiteral()
    {
        // The handler emits this exact literal on success.
        const string json = "{\"pass\":true}";
        AssertShape(ActionNodeType.GateCheck, json, closed: true);
    }

    // ── 6. Open/dynamic entries ARE declared Open (skip field checks) ──────────

    [Theory]
    [MemberData(nameof(OpenTypes))]
    public void OpenOrDynamicShape_IsDeclaredOpen(ActionNodeType type)
    {
        // Excluded from field-serialization: Condition (verbatim config),
        // ComposeOutputs (dynamic {nodeName: json} keys), Emit (tenant payload).
        ActionNodeOutputSchema.GetShape(type).Open.Should().BeTrue(
            $"ActionNodeType.{type} output is free-form/dynamic and must be declared Open");
    }

    public static IEnumerable<object[]> OpenTypes() =>
        OpenOrDynamic.Select(t => new object[] { t });

    // ── assertions ─────────────────────────────────────────────────────────────

    /// <summary>Asserts every declared field of <paramref name="type"/>'s shape is
    /// present in <paramref name="json"/> with a compatible JSON kind, and (when
    /// <paramref name="closed"/>) that no undeclared top-level field leaked.</summary>
    private static void AssertShape(ActionNodeType type, string json, bool closed,
        OutputFieldType? resultKindOverride = null)
    {
        var shape = ActionNodeOutputSchema.GetShape(type);
        shape.Open.Should().BeFalse($"{type} is asserted here as a closed/known shape");
        shape.Fields.Should().NotBeEmpty($"{type} must declare at least one output field");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.ValueKind.Should().Be(JsonValueKind.Object, $"{type} output must be a JSON object");

        var serializedProps = root.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var (field, declaredType) in shape.Fields)
        {
            root.TryGetProperty(field, out var value).Should().BeTrue(
                $"{type}: declared field '{field}' must exist in the serialized output");

            // The wrapper's Result kind is Object/Array/Boolean/Number depending on T;
            // for wrapped shapes the caller supplies the T it serialized.
            var expected = (field == "Result" && resultKindOverride is { } k) ? k : declaredType;
            AssertKindCompatible(type, field, expected, value);
        }

        if (closed)
        {
            var undeclared = serializedProps
                .Where(p => !shape.Fields.Keys.Contains(p, StringComparer.OrdinalIgnoreCase))
                .ToList();
            undeclared.Should().BeEmpty(
                $"{type}: serialized output has undeclared top-level field(s): {string.Join(", ", undeclared)}");
        }
    }

    /// <summary>A JSON <c>null</c> is compatible with ANY declared type — a nullable
    /// DTO field (e.g. AZOAResult.Detail, BridgeTransactionResult.TargetTokenId)
    /// legitimately serializes null since no DefaultIgnoreCondition is set.</summary>
    private static void AssertKindCompatible(
        ActionNodeType type, string field, OutputFieldType declared, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return;

        var ok = declared switch
        {
            OutputFieldType.String => value.ValueKind == JsonValueKind.String,
            OutputFieldType.Number => value.ValueKind == JsonValueKind.Number,
            OutputFieldType.Boolean =>
                value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            OutputFieldType.Object => value.ValueKind == JsonValueKind.Object,
            OutputFieldType.Array => value.ValueKind == JsonValueKind.Array,
            OutputFieldType.Unknown => true,
            _ => false,
        };
        ok.Should().BeTrue(
            $"{type}: field '{field}' declared {declared} but serialized as {value.ValueKind}");
    }

    // ── representative DTOs ─────────────────────────────────────────────────────

    private static string Serialize(object value) =>
        JsonSerializer.Serialize(value, ActionNodeJson.Options);

    private static AZOAResult<T> Wrap<T>(T result) =>
        new() { IsError = false, Message = "ok", Result = result };

    /// <summary>A fully-populated <see cref="BridgeTransactionResult"/> so every
    /// declared field serializes to a non-null value (nulls are also tolerated).</summary>
    private static BridgeTransactionResult RepresentativeBridge() => new()
    {
        Id = "bridge-1",
        UserId = Guid.NewGuid(),
        SourceChain = "Algorand",
        TargetChain = "Solana",
        SourceTokenId = "src-token",
        TargetTokenId = "tgt-token",
        SourceAddress = "src-addr",
        TargetAddress = "tgt-addr",
        Amount = 100,
        Status = BridgeStatus.Completed,
        Mode = BridgeMode.Trusted,
        LockTxHash = "lock-tx",
        MintTxHash = "mint-tx",
        ProofData = "proof",
        ErrorMessage = null,
        CreatedAt = DateTime.UtcNow,
        CompletedAt = DateTime.UtcNow,
        WormholeEmitterChainId = 8,
        WormholeEmitterAddress = "emitter",
        WormholeSequence = 42,
        VaaBytes = "vaa",
        VaaSignatureCount = 13,
        RedemptionTxHash = "redeem-tx",
        IdempotencyKey = "idem",
        Network = ChainNetwork.Devnet,
    };

    /// <summary>A fully-populated <see cref="CredentialRevokeResult"/> -- the exact DTO
    /// CredentialRevokeNodeHandler serializes on its success path. <c>IsConfirmed</c> is a
    /// computed get-only property and IS serialized, so the schema must declare it.</summary>
    private static CredentialRevokeResult RepresentativeCredentialRevoke() => new()
    {
        AssetId = "12345",
        CredentialId = Guid.NewGuid(),
        IssuerUserId = Guid.NewGuid(),
        HolderUserId = Guid.NewGuid(),
        HolderAddress = "HOLDER-ADDR",
        RecipientAddress = "ISSUER-ADDR",
        ChainType = "Algorand",
        Status = AZOA.WebAPI.Models.OperationStatus.Completed,
        TxHash = "clawback-tx",
        OperationId = Guid.NewGuid(),
        Replayed = false,
    };

    /// <summary>The literal dictionary GovernanceProposalCreateNodeHandler writes.</summary>
    private static Dictionary<string, object?> RepresentativeProposalCreated() => new()
    {
        ["proposalId"] = Guid.NewGuid().ToString(),
        ["status"] = "Open",
        ["options"] = new List<string> { "yes", "no" },
        ["opensAt"] = "2026-01-01T00:00:00.0000000Z",
        ["closesAt"] = "2026-01-08T00:00:00.0000000Z",
        ["uniquenessOperationKey"] = "governance:proposal:vote",
        ["uniquenessScope"] = Guid.NewGuid().ToString(),
        ["createdAt"] = "2026-01-01T00:00:00.0000000Z",
    };

    /// <summary>The literal dictionary GovernanceVoteNodeHandler writes.</summary>
    private static Dictionary<string, object?> RepresentativeVoteCast() => new()
    {
        ["proposalId"] = Guid.NewGuid().ToString(),
        ["voteId"] = Guid.NewGuid().ToString(),
        ["option"] = "yes",
        ["castAt"] = "2026-01-02T00:00:00.0000000Z",
    };

    /// <summary>The literal dictionary GovernanceTallyNodeHandler writes. <c>counts</c>
    /// carries tenant-declared option keys, hence a declared Object rather than fields.</summary>
    private static Dictionary<string, object?> RepresentativeTally() => new()
    {
        ["proposalId"] = Guid.NewGuid().ToString(),
        ["totalVotes"] = 3,
        ["counts"] = new Dictionary<string, int> { ["yes"] = 2, ["no"] = 1 },
        ["tallyHash"] = "0123456789abcdef",
        ["tallyAt"] = "2026-01-09T00:00:00.0000000Z",
        ["closesAt"] = "2026-01-08T00:00:00.0000000Z",
    };

    /// <summary>The <see cref="LoanBalanceProjectionResult"/> all three loan handlers
    /// serialize. Its [JsonPropertyName] attributes pin the camelCase wire names, so a
    /// rename on the DTO breaks this test rather than silently breaking $from bindings.</summary>
    private static LoanBalanceProjectionResult RepresentativeLoanProjection() => new()
    {
        LoanId = Guid.NewGuid().ToString(),
        Principal = "1000",
        TotalRepaid = "250",
        TotalWrittenOff = "0",
        Outstanding = "750",
        Status = "Open",
        EntryCount = 2,
    };

    /// <summary>The <see cref="AZOA.WebAPI.Services.Actions.Handlers.HoldNodeResult"/> all
    /// four hold handlers serialize (D14 / W3.4). Every source field is populated so the
    /// closed-shape check sees the full record rather than a row whose optional fields
    /// happen to collapse to the empty string.</summary>
    private static AZOA.WebAPI.Services.Actions.Handlers.HoldNodeResult RepresentativeHold() =>
        AZOA.WebAPI.Services.Actions.Handlers.HoldNodeResult.Representative(
            new AZOA.WebAPI.Persistence.SurrealDb.Models.HoldLedger
            {
                Id = "0123456789abcdef",
                HoldId = "hold-1",
                Chain = "Algorand",
                Network = "Devnet",
                HoldingAddress = "RESERVEADDRESS",
                AssetId = "12345",
                Amount = "1000",
                ReservePartition = "contributor",
                FundingSource = "Reserve",
                FunderUserId = Guid.NewGuid().ToString(),
                BeneficiaryUserId = Guid.NewGuid().ToString(),
                TenantId = Guid.NewGuid().ToString(),
                Purpose = "milestone",
                State = "Held",
                ReleaseNotBefore = new DateTimeOffset(2026, 1, 8, 0, 0, 0, TimeSpan.Zero),
                AutoRelease = true,
                InheritsReserveCommitment = false,
                ParentHoldId = "hold-0",
                ChildHoldId = "hold-2",
                SettlementTransactionId = "TXID",
                SettledAt = new DateTimeOffset(2026, 1, 9, 0, 0, 0, TimeSpan.Zero),
                SettlementReason = "released",
                Version = 3,
            });
}
