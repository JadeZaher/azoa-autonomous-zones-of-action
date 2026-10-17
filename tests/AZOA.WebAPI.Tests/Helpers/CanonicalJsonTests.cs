using System.Text.Json;
using AZOA.WebAPI.Helpers;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Helpers;

public sealed class CanonicalJsonTests
{
    [Fact]
    public void ObjectOrderingAndStringEscapesHaveOneEncoding()
    {
        using var left = JsonDocument.Parse("{\"z\":{\"b\":true,\"a\":\"\\u0061\"},\"a\":[2,1]}");
        using var right = JsonDocument.Parse("{\"a\":[2,1],\"z\":{\"a\":\"a\",\"b\":true}}");
        CanonicalJson.Hash(left.RootElement).Should().Be(CanonicalJson.Hash(right.RootElement));
        CanonicalJson.Canonicalize(left.RootElement).Should().Be("{\"a\":[2,1],\"z\":{\"a\":\"a\",\"b\":true}}");
    }

    [Theory]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"x\":[{\"a\":1,\"\\u0061\":2}]}")]
    public void DuplicateDecodedKeysAreRejected(string json)
    {
        using var document = JsonDocument.Parse(json);
        Action canonicalize = () => CanonicalJson.Canonicalize(document.RootElement);
        canonicalize.Should().Throw<JsonException>();
    }

    [Fact]
    public void NumericTokensAndArrayOrderRemainPartOfTheIntent()
    {
        using var integer = JsonDocument.Parse("[1,2]");
        using var fractional = JsonDocument.Parse("[1.0,2]");
        using var reordered = JsonDocument.Parse("[2,1]");
        CanonicalJson.Hash(integer.RootElement).Should().NotBe(CanonicalJson.Hash(fractional.RootElement));
        CanonicalJson.Hash(integer.RootElement).Should().NotBe(CanonicalJson.Hash(reordered.RootElement));
        CanonicalJson.Hash(integer.RootElement).Should().Be(CanonicalJson.HashCanonical("[1,2]"));
    }
}
