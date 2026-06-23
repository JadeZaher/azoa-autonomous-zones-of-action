using System;
using FluentAssertions;
using AZOA.WebAPI.Core.Webhooks;

namespace AZOA.WebAPI.Tests.Webhooks;

/// <summary>
/// tenant-consent-delegation AC7/H5: the length-prefixed HMAC preimage hardening for
/// outbound consent webhooks. The signature is
/// HMAC-SHA256(secret, be32(utf8Len(ts)) || utf8(ts) || utf8(body)). These cases pin
/// determinism, timestamp binding, per-tenant isolation, fail-closed on an empty secret,
/// and — the adversarial one — that the length prefix closes the delimiter-shift ambiguity
/// a naive "ts + '.' + body" concatenation had.
/// </summary>
public class WebhookHmacPreimageTests
{
    private const string Secret = "tenant-secret";

    [Fact]
    public void Sign_IsDeterministic_ForSameInputs()
    {
        var signer = new WebhookHmacSigner();
        var body = "{\"eventType\":\"consent.revoked\"}";
        var ts = "2026-06-22T10:00:00.0000000Z";
        signer.Sign(body, ts, Secret).Should().Be(signer.Sign(body, ts, Secret));
    }

    [Fact]
    public void Sign_DifferentTimestamp_DiffersForSameBody()
    {
        // The timestamp is part of the signed material — a captured event cannot be
        // replayed at a different time under the same signature.
        var signer = new WebhookHmacSigner();
        var body = "{\"x\":1}";
        signer.Sign(body, "2026-06-22T10:00:00.0000000Z", Secret)
            .Should().NotBe(signer.Sign(body, "2026-06-22T11:00:00.0000000Z", Secret));
    }

    [Fact]
    public void Sign_DifferentSecret_DiffersPerTenant()
    {
        // One tenant's secret cannot forge another tenant's signature.
        var signer = new WebhookHmacSigner();
        var body = "{\"x\":1}";
        var ts = "2026-06-22T10:00:00.0000000Z";
        signer.Sign(body, ts, "tenant-A-secret")
            .Should().NotBe(signer.Sign(body, ts, "tenant-B-secret"));
    }

    [Fact]
    public void Sign_DelimiterShift_ProducesDistinctSignatures()
    {
        // THE ATTACK: two distinct (timestamp, body) pairs whose NAIVE "ts + '.' + body"
        // concatenations are byte-identical:
        //   ("2026-01-01T00:00:00Z",   "X.Y")  ->  "2026-01-01T00:00:00Z.X.Y"
        //   ("2026-01-01T00:00:00Z.X", "Y")    ->  "2026-01-01T00:00:00Z.X.Y"
        // Under a delimiter scheme these would collide. The length-prefix pins where the
        // timestamp ends, so the two pairs MUST produce distinct signatures — the boundary
        // ambiguity is closed.
        var signer = new WebhookHmacSigner();

        var sigA = signer.Sign(body: "X.Y", timestampIso: "2026-01-01T00:00:00Z", secret: Secret);
        var sigB = signer.Sign(body: "Y", timestampIso: "2026-01-01T00:00:00Z.X", secret: Secret);

        sigA.Should().NotBe(sigB);
    }

    [Fact]
    public void Sign_EmptySecret_ThrowsArgumentException_FailClosed()
    {
        // Fail-closed: a missing per-tenant secret must never silently produce a signature.
        var signer = new WebhookHmacSigner();
        Action act = () => signer.Sign("{\"x\":1}", "2026-06-22T10:00:00.0000000Z", secret: "");
        act.Should().Throw<ArgumentException>();
    }
}
