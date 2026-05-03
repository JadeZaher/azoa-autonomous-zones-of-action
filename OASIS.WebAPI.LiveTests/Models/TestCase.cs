using System.Text.Json;
using System.Text.Json.Serialization;

namespace OASIS.WebAPI.LiveTests.Models;

/// <summary>
/// A single test case loaded from one line in a JSONL file.
/// </summary>
public class TestCase
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("method")]
    public string Method { get; set; } = "GET";

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; set; } = new();

    [JsonPropertyName("body")]
    public JsonElement? Body { get; set; }

    [JsonPropertyName("expectedStatus")]
    public int? ExpectedStatus { get; set; }

    [JsonPropertyName("expectedStatusRange")]
    public string? ExpectedStatusRange { get; set; } // "2xx", "4xx", etc.

    [JsonPropertyName("extract")]
    public Dictionary<string, string> Extract { get; set; } = new();

    [JsonPropertyName("saveAs")]
    public string? SaveAs { get; set; }

    [JsonPropertyName("dependsOn")]
    public string? DependsOn { get; set; }

    [JsonPropertyName("skip")]
    public bool Skip { get; set; }

    [JsonPropertyName("assertions")]
    public List<JsonElement> Assertions { get; set; } = new();
}
