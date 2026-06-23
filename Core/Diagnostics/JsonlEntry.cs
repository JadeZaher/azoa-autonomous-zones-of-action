using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AZOA.WebAPI.Core.Diagnostics;

/// <summary>One row written to the JSONL exception log (spec §2.A schema).</summary>
public sealed record JsonlEntry
{
    [JsonPropertyName("ts")]
    public required string Ts { get; init; }

    [JsonPropertyName("level")]
    public required string Level { get; init; }

    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("eventId")]
    public int EventId { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("exceptionType")]
    public string? ExceptionType { get; init; }

    [JsonPropertyName("exceptionMessage")]
    public string? ExceptionMessage { get; init; }

    [JsonPropertyName("stack")]
    public string? Stack { get; init; }

    [JsonPropertyName("innerChain")]
    public IReadOnlyList<string>? InnerChain { get; init; }

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("requestMethod")]
    public string? RequestMethod { get; init; }

    [JsonPropertyName("requestPath")]
    public string? RequestPath { get; init; }

    [JsonPropertyName("statusCode")]
    public int? StatusCode { get; init; }

    [JsonPropertyName("surrealStatement")]
    public string? SurrealStatement { get; init; }

    [JsonPropertyName("surrealParams")]
    public Dictionary<string, object?>? SurrealParams { get; init; }
}
