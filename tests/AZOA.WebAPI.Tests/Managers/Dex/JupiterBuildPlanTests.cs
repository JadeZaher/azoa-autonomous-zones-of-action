using System.Text.Json;
using System.Text.Json.Nodes;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using FluentAssertions;
using Solnet.Wallet;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed class JupiterBuildPlanTests
{
    private static readonly string Input = Address(1);
    private static readonly string Output = Address(2);

    [Fact]
    public void PinnedBuildQuoteBindsFullWidthDecimalAndDecodedRouteFloor()
    {
        var parsed = Read(Build());
        parsed.InputAmount.Should().Be(ulong.MaxValue);
        parsed.MinimumOutput.Should().Be(9950);
        parsed.Pool.Should().Be(Address(3));
        parsed.Route.SharedAccounts.Should().BeFalse();
        parsed.LookupAddresses.Should().BeEmpty();
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("input")]
    [InlineData("minimum")]
    [InlineData("split")]
    [InlineData("hop")]
    [InlineData("fee")]
    [InlineData("tip")]
    [InlineData("cleanup")]
    [InlineData("extra")]
    [InlineData("version")]
    [InlineData("opaque")]
    [InlineData("wire")]
    public void UnreviewedOrContradictoryBuildSemanticsRefuse(string change)
    {
        var root = Build();
        switch (change)
        {
            case "mode": root["swapMode"] = "ExactOut"; break;
            case "input": root["inAmount"] = "018446744073709551615"; break;
            case "minimum": root["otherAmountThreshold"] = "9949"; break;
            case "split": root["routePlan"]![0]!["bps"] = 9999; break;
            case "hop": root["routePlan"]![0]!["swapInfo"]!["inputMint"] = Output; break;
            case "fee": root["platformFee"] = new JsonObject { ["amount"] = "1" }; break;
            case "tip": root["tipInstruction"] = root["swapInstruction"]!.DeepClone(); break;
            case "cleanup": root["cleanupInstruction"] = root["swapInstruction"]!.DeepClone(); break;
            case "extra": root["otherInstructions"] = new JsonArray(root["swapInstruction"]!.DeepClone()); break;
            case "version": root["transactionVersion"] = 1; break;
            case "opaque": root["tokenLedgerInstruction"] = root["swapInstruction"]!.DeepClone(); break;
            default: root["swapInstruction"]!["data"] = Convert.ToBase64String([1, 2, 3]); break;
        }
        Action parse = () => Read(root);
        parse.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void DuplicateJsonFieldsAndAmbiguousAccountFlagsAreRejected()
    {
        var raw = Build().ToJsonString();
        using var document = JsonDocument.Parse(raw.Insert(1, "\"swapMode\":\"ExactOut\","));
        Action duplicate = () => JupiterBuildPlanCodec.Read(document.RootElement, Input, Output, ulong.MaxValue, 50);
        duplicate.Should().Throw<JsonException>();
        var root = Build();
        root["swapInstruction"]!["accounts"] = new JsonArray(new JsonObject
        {
            ["pubkey"] = Address(4), ["isSigner"] = "true", ["isWritable"] = false,
        });
        Action flags = () => Read(root);
        flags.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData(9000UL, 8955UL)]
    [InlineData(11000UL, 10945UL)]
    [InlineData(201UL, 199UL)]
    [InlineData(200UL, 199UL)]
    [InlineData(ulong.MaxValue, 18354510353341003856UL)]
    public void RefreshedRouteRetainsOriginalFloorWithoutMutatingCurrentBuild(ulong acceptedOutput, ulong acceptedMinimum)
    {
        var current = Read(Build());
        var original = current.Swap.Data.ToArray();
        var bound = JupiterAcceptedRoute.Bind(current, current.Pool, false, false, true,
            ulong.MaxValue, acceptedOutput, acceptedMinimum, 50);
        bound.OutputAmount.Should().Be(acceptedOutput);
        bound.MinimumOutput.Should().Be(acceptedMinimum);
        bound.Swap.Accounts.Should().BeSameAs(current.Swap.Accounts);
        bound.Swap.Data[..^11].Should().Equal(original[..^11]);
        bound.Swap.Data[^3..].Should().Equal(original[^3..]);
        current.Swap.Data.Should().Equal(original);
        JupiterRouteCodec.TryDecode(bound.Swap.Data, out var decoded).Should().BeTrue();
        decoded.Should().Be(bound.Route);
        decoded!.MinimumOutput.Should().Be(acceptedMinimum);
    }

    [Theory]
    [InlineData("pool")]
    [InlineData("direction")]
    [InlineData("kind")]
    [InlineData("input")]
    [InlineData("minimum")]
    [InlineData("slippage")]
    [InlineData("wire")]
    public void AcceptedRouteCannotSilentlyChangeProtocolOrWeakenTheAcceptedFloor(string mutation)
    {
        var current = Read(Build());
        if (mutation == "wire") current = current with { Swap = current.Swap with { Data = [.. current.Swap.Data, 0] } };
        Action bind = () => JupiterAcceptedRoute.Bind(current,
            mutation == "pool" ? Address(9) : current.Pool, false, mutation == "kind", mutation != "direction",
            mutation == "input" ? 1 : ulong.MaxValue, 10000, mutation == "minimum" ? 9949UL : 9950UL,
            mutation == "slippage" ? (ushort)51 : (ushort)50);
        bind.Should().Throw<InvalidDataException>();
    }

    private static JupiterBuildPlan Read(JsonObject value)
        => JupiterBuildPlanCodec.Read(JsonSerializer.SerializeToElement(value), Input, Output, ulong.MaxValue, 50);

    private static JsonObject Build() => JsonSerializer.SerializeToNode(new
    {
        inputMint = Input, outputMint = Output, inAmount = ulong.MaxValue.ToString(), outAmount = "10000",
        otherAmountThreshold = "9950", swapMode = "ExactIn", slippageBps = 50,
        routePlan = new[] { new { percent = 100, bps = 10000, swapInfo = new { ammKey = Address(3), label = "Whirlpool",
            inputMint = Input, outputMint = Output, inAmount = ulong.MaxValue.ToString(), outAmount = "10000" } } },
        computeBudgetInstructions = System.Array.Empty<object>(), setupInstructions = System.Array.Empty<object>(),
        otherInstructions = System.Array.Empty<object>(),
        swapInstruction = new { programId = JupiterRouteAccounts.Program.Key, accounts = System.Array.Empty<object>(),
            data = Convert.ToBase64String(Convert.FromHexString("E517CB977AE3AD2A010000001101640001FFFFFFFFFFFFFFFF1027000000000000320000")) },
        cleanupInstruction = (object?)null, tipInstruction = (object?)null, addressesByLookupTableAddress = (object?)null,
        transactionVersion = 0,
    })!.AsObject();

    private static string Address(byte first) { var bytes = new byte[32]; bytes[0] = first; return new PublicKey(bytes).Key; }
}
