using System.Text.Json;
using FluentAssertions;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.Models;

public class AZOAResultTests
{
    [Fact]
    public void AZOAResult_Default_ShouldHaveIsErrorFalse()
    {
        var result = new AZOAResult<string>();
        result.IsError.Should().BeFalse();
        result.Message.Should().BeEmpty();
        result.Result.Should().BeNull();
        result.Exception.Should().BeNull();
    }

    [Fact]
    public void AZOAResult_WithValues_ShouldPreserveThem()
    {
        var result = new AZOAResult<int>
        {
            IsError = false,
            Message = "Success",
            Result = 42
        };

        result.Result.Should().Be(42);
        result.Message.Should().Be("Success");
    }

    [Fact]
    public void AZOAResult_Error_ShouldIncludeException()
    {
        var ex = new InvalidOperationException("boom");
        var result = new AZOAResult<string>
        {
            IsError = true,
            Message = "boom",
            Exception = ex
        };

        result.Exception.Should().Be(ex);
    }

    [Fact]
    public void AZOAResponse_Default_ShouldHaveIsErrorFalse()
    {
        var response = new AZOAResponse();
        response.IsError.Should().BeFalse();
        response.Message.Should().BeEmpty();
        response.Exception.Should().BeNull();
    }

    [Fact]
    public void Serialization_AZOAResult_ShouldRoundTrip()
    {
        var original = new AZOAResult<string> { Result = "test", Message = "ok" };
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<AZOAResult<string>>(json);

        deserialized.Should().NotBeNull();
        deserialized!.Result.Should().Be("test");
        deserialized.Message.Should().Be("ok");
        deserialized.IsError.Should().BeFalse();
    }

    [Fact]
    public void Serialization_AZOAResult_RawException_IsNeverSerialized()
    {
        // The raw Exception is [JsonIgnore] — serializing it is unsafe and
        // leaks internals. Verbose detail is exposed via Detail, gated by
        // debug mode (see the debug-mode tests below).
        var original = new AZOAResult<string>
        {
            IsError = true,
            Message = "error",
            Exception = new Exception("hidden")
        };

        var json = JsonSerializer.Serialize(original);

        json.Should().NotContain("hidden");
        json.Should().NotContain("StackTrace");
    }

    [Fact]
    public void Detail_IsNull_WhenDebugDisabled()
    {
        var prev = AZOAResultDebug.Enabled;
        try
        {
            AZOAResultDebug.Enabled = false;
            var result = new AZOAResult<string>()
                .CaptureException(new InvalidOperationException("boom"));

            result.IsError.Should().BeTrue();
            result.Message.Should().Be("boom");
            result.Detail.Should().BeNull();
        }
        finally { AZOAResultDebug.Enabled = prev; }
    }

    [Fact]
    public void Detail_IncludesExceptionChain_WhenDebugEnabled()
    {
        var prev = AZOAResultDebug.Enabled;
        try
        {
            AZOAResultDebug.Enabled = true;
            var ex = new InvalidOperationException("outer", new IOException("inner cause"));
            var result = new AZOAResult<string>().CaptureException(ex);

            result.Detail.Should().NotBeNull();
            result.Detail!.Type.Should().Contain("InvalidOperationException");
            result.Detail.Message.Should().Be("outer");
            result.Detail.Inner.Should().NotBeNull();
            result.Detail.Inner!.Message.Should().Be("inner cause");

            var payload = JsonSerializer.Serialize(result.ToErrorPayload(),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            payload.Should().Contain("\"error\":\"outer\"");
            payload.Should().Contain("\"detail\"");
            payload.Should().Contain("inner cause");
        }
        finally { AZOAResultDebug.Enabled = prev; }
    }

    [Fact]
    public void ToErrorPayload_OmitsDetail_WhenDebugDisabled()
    {
        var prev = AZOAResultDebug.Enabled;
        try
        {
            AZOAResultDebug.Enabled = false;
            // Internal exception ("secret internals") + public-facing summary.
            var result = new AZOAResult<string>()
                .CaptureException(
                    new InvalidOperationException("secret internals"),
                    "Something went wrong.");

            var payload = JsonSerializer.Serialize(result.ToErrorPayload(),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            // The chosen summary message is surfaced...
            payload.Should().Contain("Something went wrong.");
            // ...but no verbose internals (type, stack trace, inner chain) leak.
            payload.Should().Contain("\"detail\":null");
            payload.Should().NotContain("secret internals");
            payload.Should().NotContain("InvalidOperationException");
            result.Detail.Should().BeNull();
        }
        finally { AZOAResultDebug.Enabled = prev; }
    }

    [Fact]
    public void Serialization_AZOAResult_WithNullResult_ShouldRoundTrip()
    {
        var original = new AZOAResult<object> { IsError = true, Message = "Not found", Result = null };
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<AZOAResult<object>>(json);

        deserialized!.IsError.Should().BeTrue();
        deserialized.Result.Should().BeNull();
    }

    [Fact]
    public void Serialization_AZOAResult_WithComplexType_ShouldRoundTrip()
    {
        var original = new AZOAResult<Dictionary<string, int>>
        {
            Result = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }
        };
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<AZOAResult<Dictionary<string, int>>>(json);

        deserialized!.Result.Should().ContainKey("a").WhoseValue.Should().Be(1);
    }

    [Fact]
    public void Serialization_AZOAResponse_ShouldRoundTrip()
    {
        var original = new AZOAResponse { Message = "Deleted." };
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<AZOAResponse>(json);

        deserialized!.Message.Should().Be("Deleted.");
        deserialized.IsError.Should().BeFalse();
    }
}
