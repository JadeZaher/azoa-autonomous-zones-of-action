using System.Text.Json;

namespace OASIS.WebAPI.LiveTests.Models;

/// <summary>
/// Result of executing a single test case against the live API.
/// </summary>
public class TestResult
{
    public string Suite { get; set; } = string.Empty;
    public string TestId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public int StatusCode { get; set; }
    public long DurationMs { get; set; }
    public string? Error { get; set; }
    public string? ResponseBodyPreview { get; set; }
    public Dictionary<string, string> ExtractedValues { get; set; } = new();
    public Dictionary<string, string> ContextSnapshot { get; set; } = new();
}
