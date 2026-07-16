using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Interfaces.Stores;
using Microsoft.AspNetCore.DataProtection;

namespace AZOA.WebAPI.Services.Governance;

public sealed class NodeTransparencyCursorCodec
{
    private const int CursorVersion = 1;
    private readonly IDataProtector _protector;

    public NodeTransparencyCursorCodec(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _protector = provider.CreateProtector("AZOA.NodeTransparency.Cursor.v1");
    }

    public string Encode(NodeTransparencyAuditKind kind, NodeTransparencyStoreCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        var payload = JsonSerializer.Serialize(new CursorPayload(
            CursorVersion,
            kind,
            cursor.OccurredAt,
            cursor.RecordId));
        return _protector.Protect(payload);
    }

    public bool TryDecode(
        string token,
        NodeTransparencyAuditKind expectedKind,
        out NodeTransparencyStoreCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        try
        {
            var payload = JsonSerializer.Deserialize<CursorPayload>(_protector.Unprotect(token));
            if (payload is null
                || payload.Version != CursorVersion
                || payload.Kind != expectedKind
                || payload.OccurredAt == default
                || string.IsNullOrWhiteSpace(payload.RecordId))
            {
                return false;
            }

            cursor = new NodeTransparencyStoreCursor(payload.OccurredAt, payload.RecordId);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException
            or FormatException
            or JsonException
            or ArgumentException)
        {
            return false;
        }
    }

    private sealed record CursorPayload(
        int Version,
        NodeTransparencyAuditKind Kind,
        DateTimeOffset OccurredAt,
        string RecordId);
}
