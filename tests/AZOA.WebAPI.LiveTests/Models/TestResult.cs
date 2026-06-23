using System.Text.Json;

namespace AZOA.WebAPI.LiveTests.Models;

public enum TestStatus
{
    Passed,
    Failed,
    Inconclusive,
    Skipped
}

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

    public TestStatus Status { get; set; } = TestStatus.Failed;

    /// <summary>Backward-compatible alias; derived from Status.</summary>
    public bool Passed
    {
        get => Status == TestStatus.Passed;
        set => Status = value ? TestStatus.Passed : (Status == TestStatus.Inconclusive || Status == TestStatus.Skipped ? Status : TestStatus.Failed);
    }

    public int StatusCode { get; set; }
    public long DurationMs { get; set; }
    public string? Error { get; set; }
    public string? ResponseBodyPreview { get; set; }
    public Dictionary<string, string> ExtractedValues { get; set; } = new();
    public Dictionary<string, string> ContextSnapshot { get; set; } = new();
}
