using System.Text.Json;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttAlgorandAssetTests
{
    [Fact]
    public void Full_width_asset_and_binary_metadata_preserve_exact_protocol_bytes()
    {
        var value = Parameters();
        value["unit-name-b64"] = Convert.ToBase64String(new byte[] { 0xff, 0, 0x80 });
        var asset = WttAlgorandAsset.Read(Response(value), ulong.MaxValue);
        Assert.Equal(ulong.MaxValue, asset.Id);
        Assert.Equal(ulong.MaxValue, asset.Total);
        Assert.Equal(19, asset.Decimals);
        Assert.Equal("ff0080" + new string('0', 58), asset.SymbolHex);
        Assert.False(asset.DefaultFrozen);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("decimals")]
    [InlineData("negative")]
    [InlineData("symbol-size")]
    [InlineData("raw-alias")]
    [InlineData("creator")]
    [InlineData("authority")]
    [InlineData("frozen")]
    [InlineData("duplicate")]
    public void Mutated_asset_identity_or_metadata_refuses(string mutation)
    {
        var value = Parameters();
        if (mutation == "decimals") value["decimals"] = 20;
        if (mutation == "negative") value["total"] = -1;
        if (mutation == "symbol-size") value["unit-name"] = "123456789";
        if (mutation == "raw-alias") { value["unit-name"] = "A"; value["unit-name-b64"] = "Qg=="; }
        if (mutation == "creator") value.Remove("creator");
        if (mutation == "authority") value["clawback"] = "invalid";
        if (mutation == "frozen") value["default-frozen"] = "false";
        var response = Response(value);
        if (mutation == "duplicate") response = JsonDocument.Parse("{\"index\":1,\"index\":2,\"params\":{}}").RootElement.Clone();
        if (mutation == "duplicate") Assert.Throws<JsonException>(() => WttAlgorandAsset.Read(response, ulong.MaxValue));
        else Assert.Throws<InvalidDataException>(() => WttAlgorandAsset.Read(response, mutation == "id" ? 1 : ulong.MaxValue));
    }

    private static Dictionary<string, object> Parameters() => new()
    {
        ["creator"] = new global::Algorand.Address(Enumerable.Repeat((byte)1, 32).ToArray()).ToString(),
        ["decimals"] = 19, ["total"] = ulong.MaxValue, ["name"] = "A token"
    };

    private static JsonElement Response(Dictionary<string, object> value)
        => JsonSerializer.SerializeToElement(new { index = ulong.MaxValue, @params = value });
}
