using System.Text;
using AZOA.WebAPI.LiveTests.Models;

namespace AZOA.WebAPI.LiveTests.Reporters;

/// <summary>
/// Renders test results into a clean, LLM-friendly Markdown report.
/// </summary>
public class MarkdownReporter
{
    private readonly bool _includeResponseBodies;
    private readonly int _truncateAt;

    public MarkdownReporter(bool includeResponseBodies = true, int truncateResponseBodyAt = 2000)
    {
        _includeResponseBodies = includeResponseBodies;
        _truncateAt = truncateResponseBodyAt;
    }

    public string Render(TestSummary summary)
    {
        var sb = new StringBuilder();

        // Header
        sb.AppendLine("# 🔬 AZOA Live API Test Results");
        sb.AppendLine();
        sb.AppendLine($"- **Base URL:** `{summary.BaseUrl}`");
        sb.AppendLine($"- **Started:** {summary.StartedAt:O}");
        sb.AppendLine($"- **Completed:** {summary.CompletedAt:O}");
        sb.AppendLine($"- **Duration:** {summary.TotalDurationMs}ms");
        sb.AppendLine();

        // Overview
        sb.AppendLine("## 📊 Summary");
        sb.AppendLine();
        sb.AppendLine($"| Metric | Value |");
        sb.AppendLine($"|--------|-------|");
        sb.AppendLine($"| Suites | {summary.TotalSuites} |");
        sb.AppendLine($"| Cases  | {summary.TotalCases} |");
        sb.AppendLine($"| ✅ Passed | {summary.Passed} |");
        sb.AppendLine($"| ❌ Failed | {summary.Failed} |");
        sb.AppendLine($"| ⚠️ Inconclusive | {summary.Inconclusive} |");
        sb.AppendLine($"| ⏭️ Skipped | {summary.Skipped} |");
        sb.AppendLine();

        // Per-suite breakdown
        foreach (var suite in summary.SuiteResults)
        {
            sb.AppendLine($"## 🗂️ {suite.SuiteName}");
            sb.AppendLine();
            sb.AppendLine($"- **Total:** {suite.Total} | **Passed:** {suite.Passed} | **Failed:** {suite.Failed} | **Inconclusive:** {suite.Inconclusive} | **Skipped:** {suite.Skipped}");
            sb.AppendLine($"- **Duration:** {suite.DurationMs}ms");
            sb.AppendLine();

            foreach (var result in suite.Results)
            {
                RenderTestResult(sb, result);
            }
        }

        // Failure rollup for quick LLM scanning
        var failures = summary.SuiteResults
            .SelectMany(s => s.Results)
            .Where(r => r.Status == TestStatus.Failed)
            .ToList();

        if (failures.Count > 0)
        {
            sb.AppendLine("## 🔥 Failure Rollup");
            sb.AppendLine();
            foreach (var f in failures)
            {
                sb.AppendLine($"- `{f.Suite}/{f.TestId}` — {f.Method} {f.Path} => {f.StatusCode} — **{f.Error}**");
            }
            sb.AppendLine();
        }

        // Inconclusive rollup (unresolved context tokens — upstream extract misses)
        var inconclusives = summary.SuiteResults
            .SelectMany(s => s.Results)
            .Where(r => r.Status == TestStatus.Inconclusive)
            .ToList();

        if (inconclusives.Count > 0)
        {
            sb.AppendLine("## ⚠️ Inconclusive Rollup");
            sb.AppendLine();
            foreach (var i in inconclusives)
            {
                sb.AppendLine($"- `{i.Suite}/{i.TestId}` — {i.Method} {i.Path} — **{i.Error}**");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private void RenderTestResult(StringBuilder sb, TestResult result)
    {
        var statusEmoji = result.Status switch
        {
            TestStatus.Skipped => "⏭️",
            TestStatus.Passed => "✅",
            TestStatus.Inconclusive => "⚠️",
            _ => "❌"
        };

        sb.AppendLine($"### {statusEmoji} {result.TestId}");
        sb.AppendLine();
        sb.AppendLine($"- **Description:** {result.Description}");
        sb.AppendLine($"- **Method:** `{result.Method}`");
        sb.AppendLine($"- **Path:** `{result.Path}`");
        sb.AppendLine($"- **Status:** {result.StatusCode}");
        sb.AppendLine($"- **Duration:** {result.DurationMs}ms");

        if (result.Error != null && result.Error != "SKIPPED")
        {
            sb.AppendLine($"- **Error:** {result.Error}");
        }

        if (result.ExtractedValues.Count > 0)
        {
            sb.AppendLine($"- **Extracted:**");
            foreach (var (k, v) in result.ExtractedValues)
            {
                sb.AppendLine($"  - `{k}` = `{v}`");
            }
        }

        if (_includeResponseBodies && !string.IsNullOrWhiteSpace(result.ResponseBodyPreview))
        {
            sb.AppendLine();
            sb.AppendLine("<details>");
            sb.AppendLine("<summary>Response body</summary>");
            sb.AppendLine();
            sb.AppendLine("```json");
            sb.AppendLine(result.ResponseBodyPreview.Length > _truncateAt
                ? result.ResponseBodyPreview[.._truncateAt] + "\n... [truncated]"
                : result.ResponseBodyPreview);
            sb.AppendLine("```");
            sb.AppendLine("</details>");
        }

        sb.AppendLine();
    }
}
