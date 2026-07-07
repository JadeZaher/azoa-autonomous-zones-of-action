using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Quest;
using FluentAssertions;
using Xunit;

namespace AZOA.WebAPI.Tests.Quest;

/// <summary>
/// Golden DRIFT test for <see cref="QuestNodeOutputSchema"/>: serializes a
/// representative instance of the REAL output DTO each handler writes to
/// <see cref="QuestNodeExecution.Output"/> (with <see cref="QuestNodeJson.Options"/>)
/// and pins that every declared field exists with a compatible JSON kind, that closed
/// shapes carry no undeclared top-level field, and that every enum value is mapped.
/// Compile-time DTO refs (<see cref="BridgeTransactionResult"/>, <see cref="AZOAResult{T}"/>)
/// mean a rename breaks this test. See Services/Quest/AGENTS.md §output-schema.
/// </summary>
public class QuestNodeOutputSchemaGoldenTests
{
    // ── shape taxonomy ────────────────────────────────────────────────────────
    // How each mapped QuestNodeType's Output is produced, so the test can pick the
    // right representative DTO to serialize. Open/anonymous entries are EXCLUDED
    // from field-level checks (documented per-entry below) — their output is
    // free-form or dynamic-keyed and not statically verifiable.

    /// <summary>Wrapped entries: handler serializes the WHOLE AZOAResult&lt;T&gt;;
    /// the serialized T governs the wrapper's Result JSON kind.</summary>
    private static readonly Dictionary<QuestNodeType, OutputFieldType> WrappedResultKinds = new()
    {
        // Result = domain object
        [QuestNodeType.HolonCreate] = OutputFieldType.Object,
        [QuestNodeType.HolonUpdate] = OutputFieldType.Object,
        [QuestNodeType.HolonGet] = OutputFieldType.Object,
        [QuestNodeType.HolonInteract] = OutputFieldType.Object,
        [QuestNodeType.HolonClone] = OutputFieldType.Object,
        [QuestNodeType.HolonCompose] = OutputFieldType.Object,
        [QuestNodeType.NftMint] = OutputFieldType.Object,
        [QuestNodeType.NftTransfer] = OutputFieldType.Object,
        [QuestNodeType.NftBurn] = OutputFieldType.Object,
        [QuestNodeType.NftGet] = OutputFieldType.Object,
        [QuestNodeType.NftGetMetadata] = OutputFieldType.Object,
        [QuestNodeType.WalletCreate] = OutputFieldType.Object,
        [QuestNodeType.WalletUpdate] = OutputFieldType.Object,
        [QuestNodeType.WalletGet] = OutputFieldType.Object,
        [QuestNodeType.WalletGetPortfolio] = OutputFieldType.Object,
        [QuestNodeType.StarGenerate] = OutputFieldType.Object,
        [QuestNodeType.StarDeploy] = OutputFieldType.Object,
        [QuestNodeType.Search] = OutputFieldType.Object,
        [QuestNodeType.AvatarNFTGetComposite] = OutputFieldType.Object,
        [QuestNodeType.BlockchainExecute] = OutputFieldType.Object,
        [QuestNodeType.Swap] = OutputFieldType.Object,
        [QuestNodeType.Grant] = OutputFieldType.Object,
        [QuestNodeType.Transfer] = OutputFieldType.Object,
        [QuestNodeType.Refund] = OutputFieldType.Object,
        [QuestNodeType.FungibleTokenCreate] = OutputFieldType.Object,
        // Result = list
        [QuestNodeType.HolonQuery] = OutputFieldType.Array,
        [QuestNodeType.HolonGetChildren] = OutputFieldType.Array,
        [QuestNodeType.HolonGetPeers] = OutputFieldType.Array,
        [QuestNodeType.HolonGetAncestors] = OutputFieldType.Array,
        [QuestNodeType.HolonGetDescendants] = OutputFieldType.Array,
        [QuestNodeType.NftQuery] = OutputFieldType.Array,
        [QuestNodeType.WalletQuery] = OutputFieldType.Array,
        // Result = scalar
        [QuestNodeType.HolonDelete] = OutputFieldType.Boolean,
        [QuestNodeType.HolonMoveSubtree] = OutputFieldType.Boolean,
        [QuestNodeType.WalletDelete] = OutputFieldType.Boolean,
        [QuestNodeType.WalletSetDefault] = OutputFieldType.Boolean,
        [QuestNodeType.HolonPropagate] = OutputFieldType.Number,
    };

    /// <summary>Flat entries: handler serializes r.Result DIRECTLY (no wrapper).</summary>
    private static readonly HashSet<QuestNodeType> FlatBridgeShape = new()
    {
        QuestNodeType.Bridge, QuestNodeType.Back,
    };

    /// <summary>Open / anonymous / dynamic-key entries — EXCLUDED from field checks.
    /// Condition passes node config verbatim; ComposeOutputs emits a dynamic
    /// {nodeName: outputJson} map; Emit serializes the tenant payload directly.</summary>
    private static readonly HashSet<QuestNodeType> OpenOrDynamic = new()
    {
        QuestNodeType.Condition, QuestNodeType.ComposeOutputs, QuestNodeType.Emit,
    };

    // ── 1. exhaustiveness: every enum value is mapped (no NotSupportedException) ─

    [Fact]
    public void EveryNodeType_HasAShapeEntry()
    {
        foreach (var type in Enum.GetValues<QuestNodeType>())
        {
            var act = () => QuestNodeOutputSchema.GetShape(type);
            act.Should().NotThrow($"QuestNodeType.{type} must have an output-schema entry");
        }
    }

    /// <summary>Guards the test's own taxonomy against enum growth: every mapped type
    /// is classified into exactly one bucket here, so a new node type forces the
    /// author to declare its output shape in this test too (not just the schema).</summary>
    [Fact]
    public void EveryNodeType_IsClassifiedByThisTest()
    {
        foreach (var type in Enum.GetValues<QuestNodeType>())
        {
            var buckets =
                (WrappedResultKinds.ContainsKey(type) ? 1 : 0) +
                (FlatBridgeShape.Contains(type) ? 1 : 0) +
                (OpenOrDynamic.Contains(type) ? 1 : 0) +
                (type == QuestNodeType.GateCheck ? 1 : 0);
            buckets.Should().Be(1,
                $"QuestNodeType.{type} must be classified in exactly one golden-test bucket");
        }
    }

    // ── 2. no live handler maps to NodeOutputShape.None ────────────────────────
    // Every production node writes a known/wrapped/flat/literal shape or is Open.
    // A None entry (Open==false, empty Fields) would be a pure side-effect node with
    // no live handler — flag it so the None branch never silently masks a real output.

    [Fact]
    public void NoMappedNodeType_IsNoneShape()
    {
        foreach (var type in Enum.GetValues<QuestNodeType>())
        {
            var shape = QuestNodeOutputSchema.GetShape(type);
            var isNone = !shape.Open && shape.Fields.Count == 0;
            isNone.Should().BeFalse(
                $"QuestNodeType.{type} maps to NodeOutputShape.None but no pure-side-effect handler exists");
        }
    }

    // ── 3. wrapped shapes match the serialized AZOAResult<T> wrapper ────────────

    [Theory]
    [MemberData(nameof(WrappedTypes))]
    public void WrappedShape_MatchesSerializedAZOAResult(QuestNodeType type)
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
    public void FlatBridgeShape_MatchesSerializedBridgeTransactionResult(QuestNodeType type)
    {
        var json = Serialize(RepresentativeBridge());
        AssertShape(type, json, closed: true);
    }

    public static IEnumerable<object[]> FlatTypes() =>
        FlatBridgeShape.Select(t => new object[] { t });

    // ── 5. GateCheck matches its fixed literal {"pass":true} ───────────────────

    [Fact]
    public void GateCheckShape_MatchesLiteral()
    {
        // The handler emits this exact literal on success.
        const string json = "{\"pass\":true}";
        AssertShape(QuestNodeType.GateCheck, json, closed: true);
    }

    // ── 6. Open/dynamic entries ARE declared Open (skip field checks) ──────────

    [Theory]
    [MemberData(nameof(OpenTypes))]
    public void OpenOrDynamicShape_IsDeclaredOpen(QuestNodeType type)
    {
        // Excluded from field-serialization: Condition (verbatim config),
        // ComposeOutputs (dynamic {nodeName: json} keys), Emit (tenant payload).
        QuestNodeOutputSchema.GetShape(type).Open.Should().BeTrue(
            $"QuestNodeType.{type} output is free-form/dynamic and must be declared Open");
    }

    public static IEnumerable<object[]> OpenTypes() =>
        OpenOrDynamic.Select(t => new object[] { t });

    // ── assertions ─────────────────────────────────────────────────────────────

    /// <summary>Asserts every declared field of <paramref name="type"/>'s shape is
    /// present in <paramref name="json"/> with a compatible JSON kind, and (when
    /// <paramref name="closed"/>) that no undeclared top-level field leaked.</summary>
    private static void AssertShape(QuestNodeType type, string json, bool closed,
        OutputFieldType? resultKindOverride = null)
    {
        var shape = QuestNodeOutputSchema.GetShape(type);
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
        QuestNodeType type, string field, OutputFieldType declared, JsonElement value)
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
        JsonSerializer.Serialize(value, QuestNodeJson.Options);

    private static AZOAResult<T> Wrap<T>(T result) =>
        new() { IsError = false, Message = "ok", Result = result };

    /// <summary>A fully-populated <see cref="BridgeTransactionResult"/> so every
    /// declared field serializes to a non-null value (nulls are also tolerated).</summary>
    private static BridgeTransactionResult RepresentativeBridge() => new()
    {
        Id = "bridge-1",
        AvatarId = Guid.NewGuid(),
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
}
