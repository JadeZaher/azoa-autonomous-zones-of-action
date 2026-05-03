namespace OASIS.WebAPI.LiveTests.Models;

/// <summary>
/// A suite of test cases derived from a single JSONL file.
/// </summary>
public class TestSuite
{
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public List<TestCase> Cases { get; set; } = new();
}
