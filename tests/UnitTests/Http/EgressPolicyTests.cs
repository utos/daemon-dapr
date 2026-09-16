using System.Net;
using Execution.Application.Models;
using Execution.Infrastructure.Http;

namespace UnitTests.Http;

/// <summary>
/// Egress policy decides what workflow HTTP may reach. The link-local cases are the ones that
/// matter most: 169.254.169.254 is the cloud instance-metadata endpoint, so a workflow reaching
/// it holds the daemon's cloud credentials.
/// </summary>
public class EgressPolicyTests
{
    private static readonly HttpEgressOptions Permissive = new()
    {
        AllowLoopback = true,
        AllowPrivateNetworks = true
    };

    private static readonly HttpEgressOptions Locked = new()
    {
        AllowLoopback = false,
        AllowPrivateNetworks = false
    };

    [Theory]
    [InlineData("169.254.169.254")]  // cloud instance metadata
    [InlineData("169.254.0.1")]
    [InlineData("169.254.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("fe80::1")]          // IPv6 link-local
    [InlineData("::")]               // IPv6 unspecified
    public void LinkLocal_is_denied_even_when_everything_else_is_allowed(string address)
    {
        Assert.False(EgressPolicy.IsAllowed(IPAddress.Parse(address), Permissive));
    }

    [Fact]
    public void Metadata_address_cannot_be_smuggled_through_an_ipv4_mapped_ipv6_literal()
    {
        // ::ffff:169.254.169.254 is neither IPv4 nor IPv6 link-local until it is unwrapped, so a
        // classifier that skips the unwrap lets the metadata endpoint straight through.
        var mapped = IPAddress.Parse("169.254.169.254").MapToIPv6();

        Assert.True(mapped.IsIPv4MappedToIPv6);
        Assert.False(EgressPolicy.IsAllowed(mapped, Permissive));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("::1")]
    public void Loopback_follows_configuration(string address)
    {
        var parsed = IPAddress.Parse(address);

        Assert.True(EgressPolicy.IsAllowed(parsed, Permissive));
        Assert.False(EgressPolicy.IsAllowed(parsed, Locked));
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.1")]
    [InlineData("fd00::1")]          // IPv6 unique local
    public void Private_ranges_follow_configuration(string address)
    {
        var parsed = IPAddress.Parse(address);

        Assert.True(EgressPolicy.IsAllowed(parsed, Permissive));
        Assert.False(EgressPolicy.IsAllowed(parsed, Locked));
    }

    [Theory]
    [InlineData("172.15.255.255")]   // just below the private block
    [InlineData("172.32.0.1")]       // just above it
    [InlineData("11.0.0.1")]
    [InlineData("192.167.1.1")]
    public void Addresses_adjacent_to_private_blocks_are_public(string address)
    {
        Assert.True(EgressPolicy.IsAllowed(IPAddress.Parse(address), Locked));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("142.250.180.14")]
    [InlineData("2606:4700:4700::1111")]
    public void Public_addresses_are_allowed_under_the_strictest_configuration(string address)
    {
        Assert.True(EgressPolicy.IsAllowed(IPAddress.Parse(address), Locked));
    }

    [Fact]
    public void Defaults_permit_loopback_and_private_so_a_self_hosted_daemon_still_works()
    {
        var defaults = new HttpEgressOptions();

        Assert.True(EgressPolicy.IsAllowed(IPAddress.Parse("10.0.0.1"), defaults));
        Assert.True(EgressPolicy.IsAllowed(IPAddress.Parse("127.0.0.1"), defaults));
        Assert.False(EgressPolicy.IsAllowed(IPAddress.Parse("169.254.169.254"), defaults));
    }

    [Fact]
    public void Default_request_timeout_is_not_the_HttpClient_default()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), new HttpEgressOptions().RequestTimeout);
    }
}
