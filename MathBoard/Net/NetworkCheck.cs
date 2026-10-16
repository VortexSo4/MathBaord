using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MathBoard.Net;

/// <summary>
/// How this computer's network treats a direct connection. <see cref="Open"/>: no NAT at all. <see cref="Friendly"/>: the
/// router keeps one public port for everyone this socket talks to, so the other side's packets get in once this side has
/// sent to it (nearly every home router). <see cref="Strict"/>: a new port for each destination, so only a peer on an
/// open or friendly network can connect. <see cref="Blocked"/>: no UDP gets out (or no internet).
/// </summary>
public enum NetworkKind { Open, Friendly, Strict, Blocked }

public sealed record NetworkReport(NetworkKind Kind, IPEndPoint? Public, bool HasIPv6);

public static class NetworkCheck
{
    /// <summary>Asks two different STUN servers from one socket and compares the public addresses they see.</summary>
    public static async Task<NetworkReport> RunAsync()
    {
        using var transport = new UdpTransport();
        var servers = new List<IPEndPoint>();
        foreach (var server in UdpTransport.StunServers)
            if ((await UdpTransport.ResolveAsync(server)).FirstOrDefault(e => e.AddressFamily == AddressFamily.InterNetwork) is { } e &&
                servers.All(s => !s.Address.Equals(e.Address)))
                servers.Add(e);

        var seen = new List<IPEndPoint>();
        foreach (var server in servers.Take(3))
            if (await transport.QueryAsync(server) is { } mapped) seen.Add(mapped);

        bool ipv6 = await PublicIPv6Async(transport) != null;
        if (seen.Count == 0) return new NetworkReport(NetworkKind.Blocked, null, ipv6);
        var first = seen[0];
        if (LocalAddresses().Any(a => a.Equals(first.Address)) && first.Port == transport.Port)
            return new NetworkReport(NetworkKind.Open, first, ipv6);
        // With one answer only, the mapping cannot be compared; it is most likely the common friendly kind.
        bool same = seen.All(s => s.Equals(first));
        return new NetworkReport(same ? NetworkKind.Friendly : NetworkKind.Strict, first, ipv6);
    }

    /// <summary>
    /// Where the other side can try to reach this socket: its public IPv4 address, a public IPv6 address and the local
    /// network address (for two boards in one network, or two copies on one computer).
    /// </summary>
    public static async Task<List<IPEndPoint>> GatherAsync(UdpTransport transport)
    {
        var found = new List<IPEndPoint>();
        foreach (var server in UdpTransport.StunServers)
        {
            var v4 = (await UdpTransport.ResolveAsync(server)).FirstOrDefault(e => e.AddressFamily == AddressFamily.InterNetwork);
            if (v4 != null && await transport.QueryAsync(v4) is { } mapped)
            {
                found.Add(mapped);
                break;
            }
        }

        if (await PublicIPv6Async(transport) is { } v6) found.Add(v6);
        // A home or school network address first (VPN adapters, such as Radmin's 26.x.x.x, come after).
        if (LocalAddresses().Where(a => a.AddressFamily == AddressFamily.InterNetwork).OrderBy(a => !IsPrivate(a)).FirstOrDefault() is { } local)
            found.Add(new IPEndPoint(local, transport.Port));
        return found.Distinct().ToList();
    }

    private static async Task<IPEndPoint?> PublicIPv6Async(UdpTransport transport)
    {
        if (!LocalAddresses().Any(a => a.AddressFamily == AddressFamily.InterNetworkV6)) return null;
        foreach (var server in UdpTransport.StunServers)
        {
            var v6 = (await UdpTransport.ResolveAsync(server)).FirstOrDefault(e => e.AddressFamily == AddressFamily.InterNetworkV6);
            if (v6 != null && await transport.QueryAsync(v6, attempts: 2) is { } mapped) return mapped;
        }

        return null;
    }

    private static bool IsPrivate(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and < 32;
    }

    /// <summary>Addresses of the network adapters that are up: IPv4 ones of networks with a gateway first, global IPv6.</summary>
    public static List<IPAddress> LocalAddresses()
    {
        var list = new List<(IPAddress Address, bool Gateway)>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = adapter.GetIPProperties();
                bool gateway = props.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any));
                foreach (var unicast in props.UnicastAddresses)
                {
                    var a = unicast.Address;
                    if (a.AddressFamily == AddressFamily.InterNetwork ||
                        a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal && !a.IsIPv6UniqueLocal && !a.IsIPv6Teredo)
                        list.Add((a, gateway));
                }
            }
        }
        catch (NetworkInformationException)
        {
        }

        return list.OrderBy(t => !t.Gateway).Select(t => t.Address).ToList();
    }
}
