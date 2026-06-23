using System.Text.Json.Serialization;

namespace AZOA.WebAPI.Models.Responses;

/// <summary>
/// Process-wide toggle for verbose error reporting. Set once at startup from
/// configuration (<c>AZOA:DebugErrors</c>), defaulting to <c>true</c> in the
/// Development environment. When enabled, <see cref="AZOAResult{T}"/> emits the
/// full exception chain (type, message, stack trace, inner exceptions) on the
/// wire; when disabled, no internal detail ever leaves the server.
/// </summary>
public static class AZOAResultDebug
{
    public static bool Enabled { get; set; }
}

/// <summary>
/// Serializable snapshot of an exception. Built only when debug mode is on so
/// raw <see cref="Exception"/> instances are never handed to the JSON
/// serializer (which is both unsafe and leaks internals in production).
/// </summary>
public sealed class ErrorDetail
{
    public string Type { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? StackTrace { get; init; }
    public ErrorDetail? Inner { get; init; }

    public static ErrorDetail From(Exception ex) => new()
    {
        Type = ex.GetType().FullName ?? ex.GetType().Name,
        Message = ex.Message,
        StackTrace = ex.StackTrace,
        Inner = ex.InnerException is not null ? From(ex.InnerException) : null,
    };
}

public class AZOAResult<T>
{
    public bool IsError { get; set; }
    public string Message { get; set; } = string.Empty;
    public T? Result { get; set; }

    /// <summary>
    /// The captured exception. Never serialized directly — see <see cref="Detail"/>.
    /// </summary>
    [JsonIgnore]
    public Exception? Exception { get; set; }

    /// <summary>
    /// Verbose error detail. Non-null only when debug mode is enabled and an
    /// exception was captured, so it is automatically included by controllers
    /// that serialize the whole result and stays absent in production.
    /// </summary>
    public ErrorDetail? Detail =>
        AZOAResultDebug.Enabled && Exception is not null
            ? ErrorDetail.From(Exception)
            : null;

    /// <summary>
    /// Record an exception against this result. Marks it as an error and uses
    /// the exception message unless an explicit one is supplied.
    /// </summary>
    public AZOAResult<T> CaptureException(Exception ex, string? message = null)
    {
        IsError = true;
        Exception = ex;
        Message = message ?? ex.Message;
        return this;
    }

    /// <summary>
    /// Wire payload for controllers that return a bare error object instead of
    /// the full result (e.g. <c>BridgeController</c>). Carries both
    /// <c>error</c> and <c>message</c> so either SDK code path can read it, plus
    /// verbose <c>detail</c> when debug mode is on.
    /// </summary>
    public object ToErrorPayload() => new
    {
        isError = true,
        error = Message,
        message = Message,
        detail = Detail,
    };
}

public class AZOAResponse
{
    public bool IsError { get; set; }
    public string Message { get; set; } = string.Empty;

    [JsonIgnore]
    public Exception? Exception { get; set; }

    public ErrorDetail? Detail =>
        AZOAResultDebug.Enabled && Exception is not null
            ? ErrorDetail.From(Exception)
            : null;
}
