using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Observability;

// Decorator that wraps ISurrealExecutor with OTEL Activity spans and SurrealMetrics.
// Lives in AZOA.WebAPI so the homebake package stays observability-agnostic.
sealed class InstrumentedSurrealExecutor : ISurrealExecutor
{
    static readonly ActivitySource _activitySource = new("SurrealForge");
    static readonly Meter          _meter          = new("SurrealForge", "1.0.0");

    static readonly Counter<long>      _queryCounter = _meter.CreateCounter<long>(
        "surrealdb.queries",
        description: "Total SurrealDB query dispatches");

    static readonly Counter<long>      _errorCounter = _meter.CreateCounter<long>(
        "surrealdb.errors",
        description: "Total SurrealDB query failures");

    static readonly Histogram<double>  _durationHistogram = _meter.CreateHistogram<double>(
        "surrealdb.duration_ms",
        unit: "ms",
        description: "SurrealDB query latency in milliseconds");

    readonly ISurrealExecutor _inner;

    public InstrumentedSurrealExecutor(ISurrealExecutor inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public async Task<IReadOnlyList<T>> QueryAsync<T>(SurrealQuery query, CancellationToken ct = default)
    {
        var sql   = query?.Build() ?? string.Empty;
        var kind  = ExtractStatementKind(sql);
        var table = ExtractTable(sql);

        using var activity = _activitySource.StartActivity("QueryAsync", ActivityKind.Client);
        SetSpanTags(activity, "QueryAsync", kind, table);

        var start = Stopwatch.GetTimestamp();
        try
        {
            var result = await _inner.QueryAsync<T>(query!, ct).ConfigureAwait(false);
            RecordSuccess(kind, table, start);
            return result;
        }
        catch (Exception ex)
        {
            RecordError(activity, ex, kind, table, start);
            throw;
        }
    }

    public async Task<T?> QuerySingleAsync<T>(SurrealQuery query, CancellationToken ct = default)
        where T : class
    {
        var sql   = query?.Build() ?? string.Empty;
        var kind  = ExtractStatementKind(sql);
        var table = ExtractTable(sql);

        using var activity = _activitySource.StartActivity("QuerySingleAsync", ActivityKind.Client);
        SetSpanTags(activity, "QuerySingleAsync", kind, table);

        var start = Stopwatch.GetTimestamp();
        try
        {
            var result = await _inner.QuerySingleAsync<T>(query!, ct).ConfigureAwait(false);
            RecordSuccess(kind, table, start);
            return result;
        }
        catch (Exception ex)
        {
            RecordError(activity, ex, kind, table, start);
            throw;
        }
    }

    public async Task<SurrealResponse> ExecuteAsync(SurrealQuery query, CancellationToken ct = default)
    {
        var sql   = query?.Build() ?? string.Empty;
        var kind  = query?.IsMultiStatement == true ? "MULTI" : ExtractStatementKind(sql);
        var table = query?.IsMultiStatement == true ? null    : ExtractTable(sql);

        using var activity = _activitySource.StartActivity("ExecuteAsync", ActivityKind.Client);
        SetSpanTags(activity, "ExecuteAsync", kind, table);

        var start = Stopwatch.GetTimestamp();
        try
        {
            var result = await _inner.ExecuteAsync(query!, ct).ConfigureAwait(false);
            RecordSuccess(kind, table, start);
            return result;
        }
        catch (Exception ex)
        {
            RecordError(activity, ex, kind, table, start);
            throw;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    static void SetSpanTags(Activity? activity, string operation, string kind, string? table)
    {
        if (activity is null) return;
        activity.SetTag("db.system", "surrealdb");
        activity.SetTag("db.operation", operation);
        activity.SetTag("surrealdb.statement_kind", kind);
        if (table is not null)
            activity.SetTag("surrealdb.table", table);
    }

    void RecordSuccess(string kind, string? table, long start)
    {
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var tags    = BuildTagList(kind, table);
        _queryCounter.Add(1, tags);
        _durationHistogram.Record(elapsed, tags);
    }

    void RecordError(Activity? activity, Exception ex, string kind, string? table, long start)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity?.SetTag("exception.message", ex.Message);
        activity?.SetTag("exception.type", ex.GetType().FullName);

        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var tags    = BuildTagList(kind, table);
        _queryCounter.Add(1, tags);
        _errorCounter.Add(1, tags);
        _durationHistogram.Record(elapsed, tags);
    }

    static TagList BuildTagList(string kind, string? table)
    {
        var tags = new TagList { { "statement_kind", kind } };
        if (table is not null)
            tags.Add("table", table);
        return tags;
    }

    // Returns the first SQL keyword (uppercase), skipping leading whitespace and -- comments.
    // Returns "OTHER" when unrecognised.
    private static string ExtractStatementKind(string sql)
    {
        var span = SkipWhitespaceAndComments(sql.AsSpan());
        foreach (var keyword in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "CREATE", "RELATE", "INFO" })
        {
            if (span.Length >= keyword.Length &&
                span[..keyword.Length].Equals(keyword.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                // Ensure it's a full token (followed by whitespace, end of string, or punctuation)
                if (span.Length == keyword.Length || !char.IsLetterOrDigit(span[keyword.Length]))
                    return keyword;
            }
        }
        return "OTHER";
    }

    // Extracts the table name after the first matching keyword in {FROM, INTO, UPDATE}.
    // "DELETE FROM <table>" is handled implicitly by the FROM branch. Returns null when unknown.
    private static string? ExtractTable(string sql)
    {
        ReadOnlySpan<char> span = sql.AsSpan();
        foreach (var anchor in new[] { "FROM", "INTO", "UPDATE" })
        {
            var idx = IndexOfKeyword(span, anchor);
            if (idx >= 0)
            {
                var after = SkipWhitespaceAndComments(span[(idx + anchor.Length)..]);
                var token = ReadToken(after);
                if (token.Length > 0)
                    return token.ToString();
            }
        }
        return null;
    }

    static ReadOnlySpan<char> SkipWhitespaceAndComments(ReadOnlySpan<char> span)
    {
        while (!span.IsEmpty)
        {
            // Skip whitespace
            if (char.IsWhiteSpace(span[0]))
            {
                span = span[1..];
                continue;
            }
            // Skip -- line comments
            if (span.Length >= 2 && span[0] == '-' && span[1] == '-')
            {
                var nl = span.IndexOf('\n');
                span = nl >= 0 ? span[(nl + 1)..] : ReadOnlySpan<char>.Empty;
                continue;
            }
            break;
        }
        return span;
    }

    // Returns the index of a keyword token in span (case-insensitive, whole-word).
    static int IndexOfKeyword(ReadOnlySpan<char> span, string keyword)
    {
        for (int i = 0; i <= span.Length - keyword.Length; i++)
        {
            if (span[i..].StartsWith(keyword.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                bool preceded = i == 0 || !char.IsLetterOrDigit(span[i - 1]);
                bool followed = (i + keyword.Length) >= span.Length || !char.IsLetterOrDigit(span[i + keyword.Length]);
                if (preceded && followed)
                    return i;
            }
        }
        return -1;
    }

    // Reads the next alphanumeric/underscore/colon token (SurrealDB table:id form) from span.
    static ReadOnlySpan<char> ReadToken(ReadOnlySpan<char> span)
    {
        span = SkipWhitespaceAndComments(span);
        int len = 0;
        while (len < span.Length && (char.IsLetterOrDigit(span[len]) || span[len] == '_' || span[len] == ':'))
            len++;
        return span[..len];
    }
}
