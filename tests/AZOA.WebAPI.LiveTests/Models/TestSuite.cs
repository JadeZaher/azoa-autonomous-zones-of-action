namespace AZOA.WebAPI.LiveTests.Models;

/// <summary>
/// A suite of test cases derived from a single JSONL file.
/// </summary>
public class TestSuite
{
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public List<TestCase> Cases { get; set; } = new();

    /// <summary>
    /// Key-value pairs parsed from the optional <c>_suiteVars</c> header line.
    /// Seeded into the substitution context before the first case runs.
    /// </summary>
    public Dictionary<string, string> SuiteVars { get; set; } = new();
}
