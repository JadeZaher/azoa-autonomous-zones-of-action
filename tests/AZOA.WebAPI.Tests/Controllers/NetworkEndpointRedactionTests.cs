using AZOA.WebAPI.Controllers;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Controllers;

/// <summary>Anonymous GET /api/network never echoes an RPC credential (audit-2 LOW, NetworkController).</summary>
public sealed class NetworkEndpointRedactionTests
{
    [Theory]
    [InlineData("https://testnet-api.algonode.cloud", "https://testnet-api.algonode.cloud")]
    [InlineData("http://localhost:8545/", "http://localhost:8545")]
    [InlineData("https://api.devnet.solana.com?api-key=abc123", "https://api.devnet.solana.com")]
    [InlineData("https://user:secret@node.example", "https://node.example")]
    [InlineData("https://node.example/rpc", null)]
    [InlineData("https://rpc.example/abcdefghijklmnopqrstuvwx", null)]
    [InlineData("https://rpc.example/%61%62%63%64%65%66%67%68", null)]
    [InlineData("https://k7f3a9b2c4d6e8.rpc.example", null)]
    [InlineData("https://abcdefghijklmnopqrstuv.rpc.example", null)]
    [InlineData("https://mainnet.infura.io/v3/9aa3d95b3bc440fa88ea12eaa4456161", null)]
    [InlineData("https://eth-sepolia.g.alchemy.com/v2/Xk3p_9Qm2LwZr8Tn5Vb1", null)]
    [InlineData("file:///etc/passwd", null)]
    [InlineData("not a url", null)]
    public void Public_endpoint_is_scheme_host_and_port_or_withheld(string configured, string? expected)
        => NetworkController.PublicEndpoint(configured).Should().Be(expected);
}
