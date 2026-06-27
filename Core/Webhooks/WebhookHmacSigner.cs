// SPDX-License-Identifier: UNLICENSED

using System.Security.Cryptography;
using System.Text;

namespace AZOA.WebAPI.Core.Webhooks;

/// <summary>
/// Replay-resistant HMAC signer for outbound consent webhooks
/// (tenant-consent-delegation §4, AC7 — H5).
///
/// <para><b>The signed payload includes the timestamp, length-prefixed.</b> The signature
/// is <c>HMAC-SHA256(secret, preimage)</c> where the preimage is an UNAMBIGUOUS
/// length-prefixed concatenation of the timestamp and the body (see <see cref="Sign"/>
/// for the exact byte layout). The delivery timestamp is part of the signed material, NOT
/// a separate unauthenticated header. A captured event therefore cannot be replayed later
/// to desync ArdaNova's view (e.g. resurrect a revoked grant): the attacker can replay the
/// exact <c>(body, timestamp, signature)</c> tuple, but the receiver rejects a stale
/// timestamp, and the attacker cannot forge a fresh timestamp because doing so would
/// invalidate the signature (they lack the per-tenant secret).</para>
///
/// <para><b>Why length-prefixed and not a delimiter.</b> An earlier form signed
/// <c>"{timestampIso}.{body}"</c> — a literal <c>.</c> delimiter. Because <c>.</c> can
/// also occur inside the timestamp (it does — ISO-8601 fractional seconds) and inside the
/// body, that construction is delimiter-ambiguous: different (timestamp, body) pairs could
/// in principle produce the same byte stream. The length-prefix removes the ambiguity by
/// pinning exactly where the timestamp ends, so the (timestamp, body) split the receiver
/// recovers is the SAME split the signer used. <b>This is a breaking change to the wire
/// signature format</b> — it is acceptable because no receiver exists yet and webhooks are
/// default-disabled (<c>Webhooks:Enabled=false</c>).</para>
///
/// <para><b>Delivery headers.</b> Each POST carries:
/// <list type="bullet">
///   <item><c>X-Azoa-Signature</c> — the value returned by <see cref="Sign"/>
///         (lowercase hex of the HMAC).</item>
///   <item><c>X-Azoa-Timestamp</c> — the EXACT <c>timestampIso</c> string that was
///         signed. The receiver MUST feed this same string (and the raw body) back into
///         the SAME length-prefixed preimage construction to verify — the timestamp is
///         part of the signed material, so any tampering breaks the signature.</item>
///   <item><c>X-Azoa-Idempotency-Id</c> — the stable per-event dedup id (separate from
///         the signature; lets the receiver discard a redelivery it already applied).</item>
/// </list></para>
///
/// <para><b>Receiver freshness-window contract.</b> The receiver MUST: (1) recompute the
/// HMAC over the length-prefixed preimage <c>(be32(utf8Len(X-Azoa-Timestamp)) ||
/// utf8(X-Azoa-Timestamp) || utf8(rawBody))</c> with the shared per-tenant secret and
/// constant-time-compare it to <c>X-Azoa-Signature</c>; (2) parse <c>X-Azoa-Timestamp</c>
/// and REJECT the request if it is outside an acceptable freshness window (e.g. ±5
/// minutes of the receiver's own clock) — this is what defeats a delayed replay. AZOA
/// signs with a fresh UTC timestamp on EVERY (re)delivery attempt, so a legitimately
/// retried event is always within the receiver's window; only a captured-and-held replay
/// falls outside it.</para>
/// </summary>
public sealed class WebhookHmacSigner
{
    /// <summary>
    /// Computes the replay-resistant signature for a webhook delivery.
    /// <paramref name="timestampIso"/> is the ISO-8601 UTC delivery timestamp (the value
    /// also sent as <c>X-Azoa-Timestamp</c>); <paramref name="body"/> is the exact raw
    /// JSON body bytes-as-string that will be POSTed; <paramref name="secret"/> is the
    /// tenant's per-tenant HMAC secret. Deterministic for a given triple.
    ///
    /// <para><b>Exact preimage (the receiver contract).</b> The HMAC-SHA256 is computed
    /// over the byte concatenation:
    /// <list type="number">
    ///   <item>the UTF-8 byte length of <paramref name="timestampIso"/> as a 4-byte
    ///         BIG-ENDIAN unsigned integer (a fixed-width length prefix);</item>
    ///   <item>the UTF-8 bytes of <paramref name="timestampIso"/>;</item>
    ///   <item>the UTF-8 bytes of <paramref name="body"/>.</item>
    /// </list>
    /// i.e. <c>be32(utf8Len(timestampIso)) || utf8(timestampIso) || utf8(body)</c>. The
    /// length prefix makes the (timestamp, body) boundary unambiguous regardless of any
    /// <c>.</c> or other bytes inside either field. Returns the lowercase-hex
    /// HMAC-SHA256 of that preimage.</para>
    /// </summary>
    public string Sign(string body, string timestampIso, string secret)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(timestampIso);
        if (string.IsNullOrEmpty(secret))
            throw new ArgumentException("A per-tenant HMAC secret is required.", nameof(secret));

        var keyBytes = System.Text.Encoding.UTF8.GetBytes(secret);
        var timestampBytes = System.Text.Encoding.UTF8.GetBytes(timestampIso);
        var bodyBytes = System.Text.Encoding.UTF8.GetBytes(body);

        // Length-prefixed preimage: a 4-byte BIG-ENDIAN prefix pinning the UTF-8 length of
        // the timestamp, then the timestamp bytes, then the body bytes. The prefix makes
        // the timestamp/body boundary unambiguous — a '.' (or any byte) inside the
        // timestamp or body can no longer shift where one field ends and the next begins,
        // so distinct (timestamp, body) pairs always produce distinct preimages.
        var preimage = new byte[4 + timestampBytes.Length + bodyBytes.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            preimage.AsSpan(0, 4), (uint)timestampBytes.Length);
        timestampBytes.CopyTo(preimage.AsSpan(4));
        bodyBytes.CopyTo(preimage.AsSpan(4 + timestampBytes.Length));

        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(preimage);
        return AZOA.WebAPI.Helpers.Encoding.ToLowerHex(hash);
    }
}
