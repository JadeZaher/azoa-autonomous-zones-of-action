using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OASIS.WebAPI.LiveTests.Models;

namespace OASIS.WebAPI.LiveTests;

/// <summary>
/// Thin HTTP client wrapper that executes test cases against the live API,
/// applies template substitution, extracts values, and records results.
/// </summary>
public class HttpTestClient
{
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly bool _includeResponseBodies;
    private readonly int _truncateAt;

    public HttpTestClient(HttpClient httpClient, bool includeResponseBodies = true, int truncateAt = 2000)
    {
        _httpClient = httpClient;
        _includeResponseBodies = includeResponseBodies;
        _truncateAt = truncateAt;
    }

    public async Task<TestResult> ExecuteAsync(TestCase testCase, string suiteName, Dictionary<string, string> context)
    {
        var sw = Stopwatch.StartNew();
        var result = new TestResult
        {
            Suite = suiteName,
            TestId = testCase.Id,
            Description = testCase.Description,
            Method = testCase.Method.ToUpperInvariant(),
            ContextSnapshot = new Dictionary<string, string>(context)
        };

        try
        {
            // Substitute template variables like {{key}} or {{savedVar.path}}
            var resolvedPath = Substitute(testCase.Path, context);
            result.Path = resolvedPath;

            var request = new HttpRequestMessage(new HttpMethod(testCase.Method), resolvedPath);

            foreach (var (key, value) in testCase.Headers)
            {
                var resolvedValue = Substitute(value, context);
                if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    request.Content = request.Content ?? new StringContent("");
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue(resolvedValue);
                }
                else
                {
                    request.Headers.TryAddWithoutValidation(key, resolvedValue);
                }
            }

            if (testCase.Body.HasValue)
            {
                var bodyJson = JsonSerializer.Serialize(testCase.Body.Value, _jsonOptions);
                bodyJson = Substitute(bodyJson, context);
                request.Content = JsonContent.Create(JsonSerializer.Deserialize<JsonElement>(bodyJson), options: _jsonOptions);
            }

            var response = await _httpClient.SendAsync(request);
            sw.Stop();

            result.StatusCode = (int)response.StatusCode;
            result.DurationMs = sw.ElapsedMilliseconds;

            var bodyText = await response.Content.ReadAsStringAsync();

            if (_includeResponseBodies)
            {
                result.ResponseBodyPreview = bodyText.Length > _truncateAt
                    ? bodyText[.._truncateAt] + "\n... [truncated]"
                    : bodyText;
            }

            // Extract values from response for chaining
            if (!string.IsNullOrWhiteSpace(bodyText) && testCase.Extract.Count > 0)
            {
                using var doc = JsonDocument.Parse(bodyText);
                foreach (var (varName, jsonPath) in testCase.Extract)
                {
                    var value = ExtractJsonPath(doc.RootElement, jsonPath);
                    if (value != null)
                    {
                        var fullKey = string.IsNullOrWhiteSpace(testCase.SaveAs)
                            ? varName
                            : $"{testCase.SaveAs}.{varName}";
                        result.ExtractedValues[fullKey] = value;
                    }
                }
            }

            // Status assertion
            result.Passed = EvaluateStatus(result.StatusCode, testCase.ExpectedStatus, testCase.ExpectedStatusRange);

            if (!result.Passed)
            {
                var expected = testCase.ExpectedStatus?.ToString() ?? testCase.ExpectedStatusRange ?? "any";
                result.Error = $"Expected status {expected}, got {result.StatusCode}.";
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.DurationMs = sw.ElapsedMilliseconds;
            result.Passed = false;
            result.Error = $"Exception: {ex.GetType().Name}: {ex.Message}";
        }

        return result;
    }

    private static string Substitute(string template, Dictionary<string, string> context)
    {
        if (string.IsNullOrEmpty(template)) return template;

        var result = template;
        foreach (var (key, value) in context)
        {
            result = result.Replace($"{{{{{key}}}}}", value, StringComparison.Ordinal);
        }
        return result;
    }

    private static string? ExtractJsonPath(JsonElement element, string path)
    {
        // Supports simple dot-notation: "result.id", "data.0.name", "token"
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var current = element;

        foreach (var segment in segments)
        {
            if (current.ValueKind == JsonValueKind.Null)
                return null;

            if (int.TryParse(segment, out var index))
            {
                if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength())
                    return null;
                current = current[index];
            }
            else
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var child))
                    return null;
                current = child;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => current.GetRawText()
        };
    }

    private static bool EvaluateStatus(int actual, int? expected, string? range)
    {
        if (expected.HasValue)
            return actual == expected.Value;

        if (!string.IsNullOrWhiteSpace(range))
        {
            var category = range.Trim().ToLowerInvariant();
            return category switch
            {
                "2xx" => actual is >= 200 and <= 299,
                "3xx" => actual is >= 300 and <= 399,
                "4xx" => actual is >= 400 and <= 499,
                "5xx" => actual is >= 500 and <= 599,
                _ => true
            };
        }

        return true; // no expectation = pass
    }
}
