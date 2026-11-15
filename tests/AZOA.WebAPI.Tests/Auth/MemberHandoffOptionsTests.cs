using AZOA.WebAPI.Services.Auth;
using AZOA.WebAPI.Models;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;

namespace AZOA.WebAPI.Tests.Auth;

public sealed class MemberHandoffOptionsTests
{
    [Theory]
    [InlineData("https://app.example/portfolio/withdraw", true)]
    [InlineData("https://app.example:443/portfolio/withdraw", true)]
    [InlineData("https://app.example.evil/portfolio/withdraw", false)]
    [InlineData("https://app.example@evil.example/portfolio/withdraw", false)]
    [InlineData("https://user:password@app.example/portfolio/withdraw", false)]
    [InlineData("http://app.example/portfolio/withdraw", false)]
    [InlineData("//app.example/portfolio/withdraw", false)]
    [InlineData("https://app.example/portfolio/withdraw#token=secret", false)]
    [InlineData("https://app.example/portfolio/withdraw?next=https://evil.example", false)]
    [InlineData("https://app.example/portfolio/withdraw/extra", false)]
    public void Return_is_the_exact_configured_origin_and_fixed_path(string url, bool allowed)
        => new MemberHandoffOptions { AllowedReturnOrigins = ["https://app.example"] }.AllowsReturn(url).Should().Be(allowed);

    [Fact]
    public void The_delivery_stub_requires_explicit_acknowledgement_outside_local_hosts()
    {
        var options = new MemberHandoffOptions { Enabled = true, HostedBaseUrl = "https://node.example", AllowedReturnOrigins = ["https://app.example"] };
        var validate = () => options.Validate(false);
        validate.Should().Throw<InvalidOperationException>().WithMessage("*StubDeliveryAcknowledged*");
        options.Validate(true);
    }

    [Theory]
    [InlineData("https://node.example/path")]
    [InlineData("http://node.example")]
    [InlineData("https://secret@node.example")]
    public void Enabled_handoffs_reject_unsafe_hosted_origins(string hosted)
    {
        var validate = () => new MemberHandoffOptions { Enabled = true, StubDeliveryAcknowledged = true,
            HostedBaseUrl = hosted, AllowedReturnOrigins = ["https://app.example"] }.Validate(false);
        validate.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Stub_messages_are_protected_before_persistence_and_need_the_same_purpose_to_read()
    {
        var sender = new ProtectedStubEmailSender(new EphemeralDataProtectionProvider());
        var message = new MemberHandoffDelivery("member@example.test", "Account", "https://node.example/claim#token=raw-secret");
        var encrypted = sender.Prepare(message);
        encrypted.Should().NotContain("raw-secret").And.NotContain("member@example");
        sender.Read(encrypted).Should().Be(message);
        var other = new ProtectedStubEmailSender(new EphemeralDataProtectionProvider());
        var read = () => other.Read(encrypted);
        read.Should().Throw<System.Security.Cryptography.CryptographicException>();
    }

    [Theory]
    [InlineData("member@example.test", true)]
    [InlineData("Member <member@example.test>", false)]
    [InlineData("member@example.test\r\nBcc: other@example.test", false)]
    [InlineData("not-an-email", false)]
    public void Delivery_address_cannot_carry_headers_or_display_name_routing(string value, bool valid)
        => MemberHandoffService.ValidEmail(value).Should().Be(valid);
}
