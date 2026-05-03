namespace OASIS.WebAPI.LiveTests.Models;

/// <summary>
/// Aggregated summary across all executed test suites.
/// </summary>
public class TestSummary
{
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public int TotalSuites { get; set; }
    public int TotalCases { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public long TotalDurationMs { get; set; }
    public List<TestSuiteResult> SuiteResults { get; set; } = new();
}

public class TestSuiteResult
{
    public string SuiteName { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public long DurationMs { get; set; }
    public List<TestResult> Results { get; set; } = new();
}
