using System.Net;
using System.Net.Sockets;
using Execution.Application.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Execution.Infrastructure.Http;

/// <summary>
/// Connects workflow HTTP only to addresses <see cref="EgressPolicy"/> has approved.
///
/// This runs as <see cref="SocketsHttpHandler.ConnectCallback"/> rather than as a
/// <see cref="DelegatingHandler"/>, for two reasons a message handler cannot cover:
///
/// 1. <b>Redirects.</b> Auto-redirect is on by default and a message handler sees only the
///    original request, so a 302 into an internal address is followed without passing through it
///    again. A connect callback runs per TCP connection, so it sees every hop.
/// 2. <b>DNS rebinding.</b> Validating a hostname and then handing the hostname to the socket
///    lets the second resolution return a different address. Here resolving and connecting are
///    one act: the address approved is the address dialled, so there is no window between the
///    check and the use.
/// </summary>
public sealed class HttpEgressGuard(
    IOptions<HttpEgressOptions> options,
    ILogger<HttpEgressGuard> logger)
{
    public async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        var policy = options.Value;

        // Resolves an IP literal to itself, so hostnames and literals take the same path.
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        var allowed = addresses.Where(address => EgressPolicy.IsAllowed(address, policy)).ToArray();

        if (allowed.Length == 0)
        {
            var reason = Describe(addresses, policy);

            logger.LogWarning(
                "Blocked workflow HTTP egress to {EgressHost}:{EgressPort} — {EgressReason}",
                host, port, reason);

            throw new HttpEgressBlockedException(
                $"Outbound request to '{host}:{port}' was blocked: {reason}");
        }

        // Dial an approved address itself, never the hostname — that is what closes the
        // check-then-use window.
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(allowed, port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static string Describe(IPAddress[] addresses, HttpEgressOptions policy)
    {
        if (addresses.Length == 0)
            return "the host did not resolve to any address";

        if (addresses.Any(EgressPolicy.IsAlwaysDenied))
            return "it resolves to a link-local address, which is never reachable from a workflow";

        if (addresses.Any(EgressPolicy.IsLoopback) && !policy.AllowLoopback)
            return "it resolves to a loopback address and loopback egress is disabled";

        if (addresses.Any(EgressPolicy.IsPrivate) && !policy.AllowPrivateNetworks)
            return "it resolves to a private address and private-network egress is disabled";

        return "no resolved address is permitted by the configured egress policy";
    }
}

/// <summary>
/// Raised when egress policy refuses a destination. Distinct from a transport failure so the
/// message reaching a workflow's <c>error</c> context says the request was refused rather than
/// that the host was unreachable.
/// </summary>
public sealed class HttpEgressBlockedException(string message) : Exception(message);
