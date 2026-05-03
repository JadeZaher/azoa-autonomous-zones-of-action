using System.Text.Json;
using FluentAssertions;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Tests.Models;

public class OASISResultTests
{
    [Fact]
    public void OASISResult_Default_ShouldHaveIsErrorFalse()
    {
        var result = new OASISResult<string>();
        result.IsError.Should().BeFalse();
        result.Message.Should().BeEmpty();
        result.Result.Should().BeNull();
        result.Exception.Should().BeNull();
    }

    [Fact]
    public void OASISResult_WithValues_ShouldPreserveThem()
    {
        var result = new OASISResult<int>
        {
            IsError = false,
            Message = "Success",
            Result = 42
        };

        result.Result.Should().Be(42);
        result.Message.Should().Be("Success");
    }

    [Fact]
    public void OASISResult_Error_ShouldIncludeException()
    {
        var ex = new InvalidOperationException("boom");
        var result = new OASISResult<string>
        {
            IsError = true,
            Message = "boom",
            Exception = ex
        };

        result.Exception.Should().Be(ex);
    }

    [Fact]
    public void OASISResponse_Default_ShouldHaveIsErrorFalse()
    {
        var response = new OASISResponse();
        response.IsError.Should().BeFalse();
        response.Message.Should().BeEmpty();
        response.Exception.Should().BeNull();
    }

    [Fact]
    public void Serialization_OASISResult_ShouldRoundTrip()
    {
        var original = new OASISResult<string> { Result = "test", Message = "ok" };
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<OASISResult<string>>(json);

        deserialized.Should().NotBeNull();
        deserialized!.Result.Should().Be("test");
        deserialized.Message.Should().Be("ok");
        deserialized.IsError.Should().BeFalse();
    }

    [Fact]
    public void Serialization_OASISResult_WithException_ShouldRoundTrip()
    {
        var original = new OASISResult<string>
        {
            IsError = true,
            Message = "error",
            Exception = new Exception("hidden")
        };
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<OASISResult<string>>(json);

        deserialized!.IsError.Should().BeTrue();
        deserialized.Message.Should().Be("error");
        // System.Text.Json serializes exception properties by default
        deserialized.Exception.Should().NotBeNull();
    }

    [Fact]
    public void Serialization_OASISResult_WithNullResult_ShouldRoundTrip()
    {
        var original = new OASISResult<object> { IsError = true, Message = "Not found", Result = null };
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<OASISResult<object>>(json);

        deserialized!.IsError.Should().BeTrue();
        deserialized.Result.Should().BeNull();
    }

    [Fact]
    public void Serialization_OASISResult_WithComplexType_ShouldRoundTrip()
    {
        var original = new OASISResult<Dictionary<string, int>>
        {
            Result = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }
        };
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<OASISResult<Dictionary<string, int>>>(json);

        deserialized!.Result.Should().ContainKey("a").WhoseValue.Should().Be(1);
    }

    [Fact]
    public void Serialization_OASISResponse_ShouldRoundTrip()
    {
        var original = new OASISResponse { Message = "Deleted." };
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<OASISResponse>(json);

        deserialized!.Message.Should().Be("Deleted.");
        deserialized.IsError.Should().BeFalse();
    }
}
