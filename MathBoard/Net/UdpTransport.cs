using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace MathBoard.Net;

/// <summary>
/// One UDP socket (IPv6 with IPv4 mapped in, so both kinds of address work) shared by the STUN queries and every peer:
/// a peer's NAT lets packets in only on the port this computer sent from, so everything must use the same one. Packets
/// of MathBoard's protocol go to the handler registered for their connection number; STUN answers to their query.
/// </summary>
public sealed class UdpTransport : IDisposable
{
    private const uint MagicCookie = 0x2112A442;

    /// <summary>Public STUN servers: they only tell each computer its address as the internet sees it.</summary>
    public static readonly (string Host, int Port)[] StunServers =
        [("stun.l.google.com", 19302), ("stun.cloudflare.com", 3478), ("stun1.l.google.com", 19302)];

    private readonly Socket _socket;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IPEndPoint>> _queries = new();
    private readonly ConcurrentDictionary<uint, Action<byte[], int, IPEndPoint>> _handlers = new();
    private readonly CancellationTokenSource _stop = new();

    /// <summary>For tests of a bad network: called with every datagram received in this process; false drops it.</summary>
    public static Func<byte[], int, bool>? Filter { get; set; }

    public int Port => ((IPEndPoint)_socket.LocalEndPoint!).Port;

    public UdpTransport(int port = 0)
    {
        _socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
        // Windows reports an ICMP "port unreachable" for an earlier send as an error on the next receive; ignore it.
        if (OperatingSystem.IsWindows()) _socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
        _socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        _socket.ReceiveBufferSize = 1 << 20;
        _ = Task.Run(ReceiveLoop);
    }

    public void Register(uint connection, Action<byte[], int, IPEndPoint> handler) => _handlers[connection] = handler;

    public void Unregister(uint connection) => _handlers.TryRemove(connection, out _);

    public void Send(ReadOnlySpan<byte> packet, IPEndPoint to)
    {
        try
        {
            _socket.SendTo(packet, Mapped(to));
        }
        catch (SocketException)
        {
            // An address of the other side that cannot be reached from here (another kind of network); others may work.
        }
    }

    private static IPEndPoint Mapped(IPEndPoint e) =>
        e.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(e.Address.MapToIPv6(), e.Port) : e;

    private static IPEndPoint Unmapped(IPEndPoint e) =>
        e.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(e.Address.MapToIPv4(), e.Port) : e;

    private async Task ReceiveLoop()
    {
        var buffer = new byte[2048];
        EndPoint any = new IPEndPoint(IPAddress.IPv6Any, 0);
        while (!_stop.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            int length = result.ReceivedBytes;
            var from = Unmapped((IPEndPoint)result.RemoteEndPoint);
            if (Filter != null && !Filter(buffer, length)) continue;
            if (length >= 20 && BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(4)) == MagicCookie)
            {
                HandleStun(buffer, length);
                continue;
            }

            if (length >= 5 && _handlers.TryGetValue(BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(1)), out var handler))
            {
                try
                {
                    handler(buffer[..length], length, from);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Network packet failed: {ex.Message}");
                }
            }
        }
    }

    // ------------------------------------------------------------- STUN (RFC 5389): what address the internet sees

    /// <summary>Asks a STUN server for this socket's public address; null if it does not answer in time.</summary>
    public async Task<IPEndPoint?> QueryAsync(IPEndPoint server, int attempts = 3, int timeoutMs = 600)
    {
        var request = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(request, 0x0001);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4), MagicCookie);
        RandomNumberGenerator.Fill(request.AsSpan(8, 12));
        string key = Convert.ToHexString(request, 8, 12);
        var answer = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queries[key] = answer;
        try
        {
            for (int i = 0; i < attempts; i++)
            {
                Send(request, server);
                if (await Task.WhenAny(answer.Task, Task.Delay(timeoutMs)) == answer.Task) return answer.Task.Result;
            }

            return null;
        }
        finally
        {
            _queries.TryRemove(key, out _);
        }
    }

    private void HandleStun(byte[] data, int length)
    {
        if (BinaryPrimitives.ReadUInt16BigEndian(data) != 0x0101) return;
        if (!_queries.TryGetValue(Convert.ToHexString(data, 8, 12), out var answer)) return;
        int pos = 20, end = Math.Min(length, 20 + BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2)));
        IPEndPoint? mapped = null;
        while (pos + 4 <= end)
        {
            int type = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
            int size = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 2));
            var value = data.AsSpan(pos + 4, Math.Min(size, end - pos - 4));
            if (type is 0x0020 or 0x0001 && value.Length >= 8)
            {
                bool xor = type == 0x0020;
                int port = BinaryPrimitives.ReadUInt16BigEndian(value[2..]) ^ (xor ? (int)(MagicCookie >> 16) : 0);
                var address = value[1] == 0x01 ? value.Slice(4, 4).ToArray() : value.Length >= 20 ? value.Slice(4, 16).ToArray() : null;
                if (address != null)
                {
                    // XOR with the magic cookie, and for IPv6 also with the transaction id that follows it.
                    if (xor) for (int i = 0; i < address.Length; i++) address[i] ^= data[4 + i];
                    var found = new IPEndPoint(new IPAddress(address), port);
                    if (xor || mapped == null) mapped = found;
                }
            }

            pos += 4 + (size + 3) / 4 * 4;
        }

        if (mapped != null) answer.TrySetResult(mapped);
    }

    /// <summary>The addresses of a STUN server, IPv4 first; empty if the name does not resolve.</summary>
    public static async Task<IPEndPoint[]> ResolveAsync((string Host, int Port) server)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(server.Host);
            return addresses.OrderBy(a => a.AddressFamily != AddressFamily.InterNetwork).Select(a => new IPEndPoint(a, server.Port)).ToArray();
        }
        catch (SocketException)
        {
            return [];
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _socket.Dispose();
    }
}
