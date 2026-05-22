using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Oasis.SurrealDb.Client.Json;
using Oasis.SurrealDb.Client.Transaction;

namespace Oasis.SurrealDb.Client.Connection;

/// <summary>
/// HTTP transport for SurrealDB's <c>POST /sql</c> endpoint, per
/// <see href="https://surrealdb.com/docs/surrealdb/integration/http">the
/// integration docs</see>. This is the stable, JSON-in / JSON-out path and
/// is the default transport across wave-1 of the package.
/// <para>
/// WebSocket transport is deferred to sub-wave 1.5b — only LIVE queries
/// require it; everything else (CRUD, multi-statement, transactions) works
/// fine over HTTP.
/// </para>
/// </summary>
public sealed class HttpSurrealConnection : ISurrealConnection
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SurrealConnectionOptions _options;
    private string _ns;
    private string _db;
    private int _disposed;

    /// <summary>
    /// Construct using an externally-managed <see cref="HttpClient"/> (e.g.
    /// one supplied by <c>IHttpClientFactory</c>). Caller owns the client's
    /// lifetime; we do NOT dispose it on dispose.
    /// </summary>
    public HttpSurrealConnection(HttpClient http, SurrealConnectionOptions options)
        : this(http, options, ownsHttp: false)
    {
    }

    /// <summary>
    /// Construct with our own <see cref="HttpClient"/>. This is the entry
    /// point intended for unit tests with a mocked <see cref="HttpMessageHandler"/>.
    /// </summary>
    public HttpSurrealConnection(HttpMessageHandler handler, SurrealConnectionOptions options)
        : this(new HttpClient(handler), options, ownsHttp: true)
    {
    }

    private HttpSurrealConnection(HttpClient http, SurrealConnectionOptions options, bool ownsHttp)
    {
        _http     = http    ?? throw new ArgumentNullException(nameof(http));
        _options  = options ?? throw new ArgumentNullException(nameof(options));
        _ownsHttp = ownsHttp;
        _ns       = _options.Namespace;
        _db       = _options.Database;

        if (string.IsNullOrWhiteSpace(_options.Endpoint))
            throw new ArgumentException("SurrealConnectionOptions.Endpoint must be set.", nameof(options));

        if (_http.BaseAddress is null)
        {
            // Tolerate trailing slash either way.
            var baseUri = _options.Endpoint.EndsWith("/", StringComparison.Ordinal)
                ? _options.Endpoint
                : _options.Endpoint + "/";
            _http.BaseAddress = new Uri(baseUri);
        }

        if (_options.RequestTimeout > TimeSpan.Zero)
        {
            // HttpClient.Timeout can only be set when no request is in flight.
            try { _http.Timeout = _options.RequestTimeout; } catch (InvalidOperationException) { /* shared client */ }
        }
    }

    /// <summary>Current namespace scope (last value passed to <see cref="UseAsync"/> or constructor default).</summary>
    public string Namespace => _ns;

    /// <summary>Current database scope.</summary>
    public string Database  => _db;

    /// <inheritdoc/>
    public Task UseAsync(string ns, string db, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ns)) throw new ArgumentException("ns is required.", nameof(ns));
        if (string.IsNullOrWhiteSpace(db)) throw new ArgumentException("db is required.", nameof(db));
        // The HTTP transport sends NS/DB as headers per request, so "switching"
        // is effectively a local state update — no round-trip required.
        _ns = ns;
        _db = db;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<SurrealResponse> ExecuteRawAsync(
        string sql,
        object? parameters = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sql))
        {
            throw new ArgumentException("sql must not be empty.", nameof(sql));
        }
        ThrowIfDisposed();

        // Retry transient transport failures with jittered exponential backoff.
        // We rebuild the HttpRequestMessage every attempt (HttpRequestMessage
        // cannot be reused once sent). The connection-pool layer above us
        // also rate-limits concurrent in-flight requests; retries here are
        // the per-call resilience layer.
        //
        // HIGH#2 exactly-once guarantee: non-idempotent statements
        // (CREATE / UPDATE / DELETE / RELATE / COMMIT TRANSACTION / ...)
        // must NEVER be silently retried — the original send may have
        // succeeded on the server even when the client side observed a
        // transport fault, and retrying would cause double-write in the
        // bridge value path. Only the first attempt fires for those; the
        // exception bubbles to the caller. See
        // <c>bridge-unsafe-pre-launch</c> + <c>data-engine-decision</c>.
        var totalAttempts = Math.Max(1, _options.MaxRetries);
        var allowRetries  = IsIdempotentSql(sql);
        Exception? lastError = null;
        for (int attempt = 0; attempt < totalAttempts; attempt++)
        {
            try
            {
                using var req  = BuildRequest(sql, parameters);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    throw new SurrealProtocolException(
                        $"SurrealDB /sql HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {Truncate(body)}");
                }

                return SurrealResponse.FromJson(body);
            }
            catch (HttpRequestException ex) when (allowRetries && attempt + 1 < totalAttempts)
            {
                lastError = ex;
                await DelayWithJitterAsync(attempt, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (allowRetries && !ct.IsCancellationRequested && attempt + 1 < totalAttempts)
            {
                lastError = ex;
                await DelayWithJitterAsync(attempt, ct).ConfigureAwait(false);
            }
        }
        // Unreachable in practice — the final attempt either returns or throws.
        throw new SurrealProtocolException("SurrealDB /sql request failed after retries.", lastError!);
    }

    /// <summary>
    /// True iff the statement is safe to retry on transport failure — i.e. the
    /// server cannot produce a different observable outcome from a duplicate
    /// send. Conservatively allowlist-only: any statement we don't explicitly
    /// recognise as idempotent is treated as a write and never retried.
    /// </summary>
    /// <remarks>
    /// Allowed prefixes (case-insensitive, after stripping line/block comments
    /// and leading whitespace):
    /// <list type="bullet">
    ///   <item><c>SELECT</c> — pure read.</item>
    ///   <item><c>INFO</c> — schema introspection.</item>
    ///   <item><c>LIVE</c> — subscription set-up; subscription id is server-issued and the second attempt simply re-subscribes.</item>
    ///   <item><c>KILL</c> — idempotent: killing an already-killed subscription is a no-op.</item>
    ///   <item><c>BEGIN TRANSACTION</c> — opens a fresh server-side txn; observable state is per-connection.</item>
    /// </list>
    /// Everything else — including <c>CREATE</c>, <c>UPDATE</c>,
    /// <c>UPSERT</c>, <c>DELETE</c>, <c>INSERT</c>, <c>RELATE</c>,
    /// <c>MERGE</c>, <c>DEFINE</c>, <c>REMOVE</c>, <c>USE</c>,
    /// <c>COMMIT TRANSACTION</c>, <c>CANCEL TRANSACTION</c>, and any
    /// multi-statement body whose first non-comment token isn't on the list —
    /// is treated as non-idempotent.
    /// </remarks>
    public static bool IsIdempotentSql(string sql)
    {
        if (string.IsNullOrEmpty(sql)) return false;

        var trimmed = StripLeadingCommentsAndWhitespace(sql);
        if (trimmed.Length == 0) return false;

        // Match the FIRST token. We need a multi-word check for "BEGIN TRANSACTION"
        // — accept "BEGIN" too because SurrealDB's grammar allows the shorthand.
        return StartsWithToken(trimmed, "SELECT")
            || StartsWithToken(trimmed, "INFO")
            || StartsWithToken(trimmed, "LIVE")
            || StartsWithToken(trimmed, "KILL")
            || StartsWithToken(trimmed, "BEGIN");
    }

    private static string StripLeadingCommentsAndWhitespace(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            // Line comment: -- ... \n  or  // ... \n
            if (c == '-' && i + 1 < s.Length && s[i + 1] == '-')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            // Block comment: /* ... */
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                if (i + 1 < s.Length) i += 2; // consume closing */
                continue;
            }
            break;
        }
        return i >= s.Length ? string.Empty : s.Substring(i);
    }

    private static bool StartsWithToken(string s, string token)
    {
        if (s.Length < token.Length) return false;
        // Case-insensitive ordinal compare on the leading characters.
        if (string.Compare(s, 0, token, 0, token.Length, StringComparison.OrdinalIgnoreCase) != 0)
            return false;
        // The next char must be a non-identifier char (whitespace, semicolon,
        // EOF) — otherwise "SELECTOR" would match "SELECT".
        if (s.Length == token.Length) return true;
        char next = s[token.Length];
        return !char.IsLetterOrDigit(next) && next != '_';
    }

    /// <inheritdoc/>
    public async Task<ISurrealTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        return await SurrealTransaction.StartAsync(this, ct).ConfigureAwait(false);
    }

    private HttpRequestMessage BuildRequest(string sql, object? parameters)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "sql");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.TryAddWithoutValidation("NS", _ns);
        req.Headers.TryAddWithoutValidation("DB", _db);

        if (!string.IsNullOrEmpty(_options.User))
        {
            var raw = $"{_options.User}:{_options.Password}";
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", b64);
        }

        // SurrealDB's HTTP /sql endpoint historically accepts the raw query
        // as text/plain. Parameters are bound via query-string ?<var>=<json>
        // pairs. This avoids the parameterized-POST shape that 0.x rev'd
        // through a few different schemas.
        if (parameters is not null)
        {
            var paramJson = JsonSerializer.Serialize(parameters, SurrealJsonOptions.Default);
            using var doc = JsonDocument.Parse(paramJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var qs = new StringBuilder();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (qs.Length > 0) qs.Append('&');
                    qs.Append(Uri.EscapeDataString(prop.Name));
                    qs.Append('=');
                    qs.Append(Uri.EscapeDataString(prop.Value.GetRawText()));
                }
                req.RequestUri = new Uri("sql?" + qs.ToString(), UriKind.Relative);
            }
        }

        req.Content = new StringContent(sql, Encoding.UTF8, "application/json");
        return req;
    }

    private async Task DelayWithJitterAsync(int attempt, CancellationToken ct)
    {
        // Exponential backoff with ± jitter ratio.
        var baseMs   = _options.BaseRetryDelay.TotalMilliseconds * Math.Pow(2, attempt);
        var jitter   = (_random.NextDouble() * 2 - 1) * _options.JitterRatio; // [-ratio, +ratio]
        var totalMs  = Math.Max(0, baseMs * (1 + jitter));
        await Task.Delay(TimeSpan.FromMilliseconds(totalMs), ct).ConfigureAwait(false);
    }

    // System.Random is not thread-safe pre-net6; for net8 it is, but we keep
    // the field-scoped one and rely on attempt count being small (<= 5).
    private readonly Random _random = new();

    private static string Truncate(string s) =>
        s.Length <= 512 ? s : s.Substring(0, 512) + "...[truncated]";

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(HttpSurrealConnection));
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_ownsHttp) _http.Dispose();
    }
}
