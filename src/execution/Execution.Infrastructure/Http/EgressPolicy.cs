using System.Net;
using System.Net.Sockets;
using Execution.Application.Models;

namespace Execution.Infrastructure.Http;

/// <summary>
/// Whether workflow HTTP may reach a given address. Pure, so the decision can be asserted
/// without opening a socket — the connecting is <see cref="HttpEgressGuard"/>'s job.
/// </summary>
public static class EgressPolicy
{
    public static bool IsAllowed(IPAddress address, HttpEgressOptions policy)
    {
        if (IsAlwaysDenied(address))
            return false;

        if (IsLoopback(address))
            return policy.AllowLoopback;

        if (IsPrivate(address))
            return policy.AllowPrivateNetworks;

        return true;
    }

    /// <summary>
    /// Link-local and the unspecified address, denied regardless of configuration.
    /// 169.254.169.254 — the cloud instance-metadata endpoint — is inside the link-local range,
    /// which is the whole reason this is not an option.
    /// </summary>
    public static bool IsAlwaysDenied(IPAddress address)
    {
        var candidate = Unwrap(address);

        if (candidate.AddressFamily == AddressFamily.InterNetwork)
        {
            var octets = candidate.GetAddressBytes();

            return (octets[0] == 169 && octets[1] == 254)   // 169.254.0.0/16, incl. metadata
                   || octets[0] == 0;                        // 0.0.0.0/8
        }

        return candidate.IsIPv6LinkLocal || candidate.Equals(IPAddress.IPv6Any);
    }

    public static bool IsLoopback(IPAddress address)
    {
        return IPAddress.IsLoopback(Unwrap(address));
    }

    public static bool IsPrivate(IPAddress address)
    {
        var candidate = Unwrap(address);

        if (candidate.AddressFamily != AddressFamily.InterNetwork)
            return candidate.IsIPv6UniqueLocal;

        var octets = candidate.GetAddressBytes();

        return octets[0] == 10
               || (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31)
               || (octets[0] == 192 && octets[1] == 168);
    }

    /// <summary>
    /// An IPv4 address written as ::ffff:a.b.c.d is neither IPv4 nor IPv6 link-local until it is
    /// unwrapped, which would otherwise smuggle ::ffff:169.254.169.254 straight past the check.
    /// </summary>
    private static IPAddress Unwrap(IPAddress address)
    {
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }
}
