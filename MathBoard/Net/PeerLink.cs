using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;

namespace MathBoard.Net;

/// <summary>
/// A connection to one other computer through the shared socket. Both sides send probes to every address in the other's
/// code until one gets through: each side's own probes open its NAT for the other's (hole punching). After that it is
/// a reliable, ordered stream of messages: packets are numbered, acknowledged and sent again when lost, long messages are
/// cut into packets that fit the network, and how many may be on the way grows while nothing is lost and halves when
/// something is (as TCP does). Every packet is encrypted and authenticated with AES-GCM under the key from both codes,
/// so only the two people who exchanged them can talk, and a packet from a new address is believed only if it decrypts.
/// </summary>
public sealed class PeerLink : IDisposable
{
    public enum LinkState { Connecting, Connected, Closed }

    private const byte Probe = 0xA1, ProbeAck = 0xA2, Data = 0xA3, Ack = 0xA4, Keepalive = 0xA5, Bye = 0xA6;
    private const int HeaderSize = 13, TagSize = 16;
    private const long ProbeIntervalMs = 150, ConnectTimeoutMs = 60_000, KeepaliveMs = 2_000, DeadAfterMs = 20_000;

    private readonly UdpTransport _transport;
    private readonly uint _connection;
    private readonly AesGcm _aes;
    private readonly byte _direction;
    private readonly List<IPEndPoint> _candidates;
    private readonly object _lock = new();
    private IPEndPoint? _remote;
    private ulong _packetNumber;
    private long _started, _lastReceived, _lastSent, _lastProbe;

    // Sending: frames waiting for a number, and those sent but not acknowledged yet.
    private readonly Queue<(byte[] Data, byte Flags)> _outgoing = new();
    private readonly SortedDictionary<uint, Pending> _inFlight = new();
    private uint _nextSeq;
    private double _window = 16, _threshold = 1024;
    private double _srtt = -1, _rttVar;
    private long _rto = 300, _lastCut;
    // When the newest acknowledged frame was sent: a frame sent well before it and still not acknowledged was lost.
    private long _newestAckedSent;

    // Receiving: the next frame number expected, frames that came early, and the message being put together.
    private uint _expected;
    private readonly Dictionary<uint, (byte[] Data, byte Flags)> _early = new();
    private MemoryStream? _message;
    private readonly ConcurrentQueue<byte[]> _inbox = new();

    private sealed class Pending(byte[] frame, long sentAt)
    {
        public readonly byte[] Frame = frame;
        public long SentAt = sentAt;
        public int Resends;
    }

    public LinkState State { get; private set; } = LinkState.Connecting;
    public IPEndPoint? Remote => _remote;

    /// <summary>Largest message piece per packet; smaller packets pass some networks better, larger ones carry more.</summary>
    public int MaxPayload { get; set; } = 1100;

    /// <summary>Raised from a network thread when a message arrived or the state changed.</summary>
    public event Action? Changed;

    /// <param name="key">16 bytes from both codes.</param>
    /// <param name="host">Which side this is: the two directions use different nonces.</param>
    public PeerLink(UdpTransport transport, uint connection, byte[] key, bool host, IEnumerable<IPEndPoint> candidates)
    {
        _transport = transport;
        _connection = connection;
        _aes = new AesGcm(key, TagSize);
        _direction = host ? (byte)1 : (byte)2;
        _candidates = candidates.ToList();
        _started = _lastReceived = Now;
        transport.Register(connection, Handle);
    }

    private static long Now => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    /// <summary>Queues a message; it arrives whole and in order, however it is cut into packets.</summary>
    public void Send(byte[] message)
    {
        lock (_lock)
        {
            if (State == LinkState.Closed) return;
            int count = Math.Max(1, (message.Length + MaxPayload - 1) / MaxPayload);
            for (int i = 0; i < count; i++)
            {
                int from = i * MaxPayload;
                byte flags = (byte)((i == 0 ? 1 : 0) | (i == count - 1 ? 2 : 0));
                _outgoing.Enqueue((message[from..Math.Min(message.Length, from + MaxPayload)], flags));
            }
        }
    }

    public bool TryReceive(out byte[] message) => _inbox.TryDequeue(out message!);

    /// <summary>Messages still waiting to be sent or acknowledged.</summary>
    public int Backlog
    {
        get
        {
            lock (_lock) return _outgoing.Count + _inFlight.Count;
        }
    }

    /// <summary>Probes, sends, resends and keeps the connection alive; called every few milliseconds.</summary>
    public void Tick()
    {
        bool changed = false;
        lock (_lock)
        {
            long now = Now;
            if (State == LinkState.Connecting)
            {
                if (now - _started > ConnectTimeoutMs) changed = SetState(LinkState.Closed);
                else if (now - _lastProbe >= ProbeIntervalMs)
                {
                    _lastProbe = now;
                    foreach (var c in _candidates) SendPacket(Probe, [], c);
                }
            }
            else if (State == LinkState.Connected)
            {
                if (now - _lastReceived > DeadAfterMs) changed = SetState(LinkState.Closed);
                else
                {
                    Resend(now);
                    while (_outgoing.Count > 0 && _inFlight.Count < (int)_window)
                    {
                        var (data, flags) = _outgoing.Dequeue();
                        var frame = new byte[5 + data.Length];
                        BinaryPrimitives.WriteUInt32BigEndian(frame, _nextSeq);
                        frame[4] = flags;
                        data.CopyTo(frame, 5);
                        _inFlight[_nextSeq++] = new Pending(frame, now);
                        SendPacket(Data, frame, _remote!);
                    }

                    if (now - _lastSent > KeepaliveMs) SendPacket(Keepalive, [], _remote!);
                }
            }
        }

        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// Sends again what is lost: a frame sent well before one that was acknowledged since (packets overtake each other
    /// only a little), or one not acknowledged within the timeout, which grows when a frame is lost again.
    /// </summary>
    private void Resend(long now)
    {
        bool lost = false;
        long patience = (long)Math.Max(_srtt * 1.25, 10);
        foreach (var p in _inFlight.Values)
        {
            bool overtaken = p.SentAt + 5 < _newestAckedSent && now - p.SentAt > patience;
            if (!overtaken && now - p.SentAt < Math.Min(_rto << Math.Min(p.Resends, 3), 3000)) continue;
            ResendFrame(p, now);
            lost = true;
        }

        if (lost) Cut(now);
    }

    private void ResendFrame(Pending p, long now)
    {
        p.SentAt = now;
        p.Resends++;
        SendPacket(Data, p.Frame, _remote!);
    }

    /// <summary>Loss means the network is full: send half as much at once, but at most once per round trip.</summary>
    private void Cut(long now)
    {
        if (now - _lastCut < Math.Max(_srtt, 50)) return;
        _lastCut = now;
        _threshold = Math.Max(_window / 2, 4);
        _window = _threshold;
    }

    public void Close()
    {
        lock (_lock)
        {
            if (State == LinkState.Connected)
                for (int i = 0; i < 3; i++) SendPacket(Bye, [], _remote!);
            SetState(LinkState.Closed);
        }

        _transport.Unregister(_connection);
    }

    private bool SetState(LinkState state)
    {
        if (State == state) return false;
        State = state;
        return true;
    }

    private void SendPacket(byte type, ReadOnlySpan<byte> plain, IPEndPoint to)
    {
        var packet = new byte[HeaderSize + plain.Length + TagSize];
        packet[0] = type;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1), _connection);
        ulong number = ++_packetNumber;
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(5), number);
        _aes.Encrypt(Nonce(_direction, number), plain, packet.AsSpan(HeaderSize, plain.Length), packet.AsSpan(HeaderSize + plain.Length), packet.AsSpan(0, HeaderSize));
        _transport.Send(packet, to);
        _lastSent = Now;
    }

    private static byte[] Nonce(byte direction, ulong number)
    {
        var nonce = new byte[12];
        nonce[0] = direction;
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(1), number);
        return nonce;
    }

    private void Handle(byte[] packet, int length, IPEndPoint from)
    {
        if (length < HeaderSize + TagSize) return;
        var plain = new byte[length - HeaderSize - TagSize];
        try
        {
            _aes.Decrypt(Nonce((byte)(3 - _direction), BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(5))),
                packet.AsSpan(HeaderSize, plain.Length), packet.AsSpan(length - TagSize, TagSize), plain, packet.AsSpan(0, HeaderSize));
        }
        catch (CryptographicException)
        {
            return;
        }

        bool changed = false;
        lock (_lock)
        {
            if (State == LinkState.Closed) return;
            _lastReceived = Now;
            switch (packet[0])
            {
                case Probe:
                    SendPacket(ProbeAck, [], from);
                    changed = Connect(from);
                    break;
                case ProbeAck:
                    changed = Connect(from);
                    break;
                case Bye:
                    changed = SetState(LinkState.Closed);
                    break;
                default:
                    changed = Connect(from);
                    // The other side's address changed (another network, or its NAT moved the port): follow it.
                    _remote = from;
                    if (packet[0] == Data) changed |= OnData(plain);
                    else if (packet[0] == Ack) OnAck(plain);
                    break;
            }
        }

        if (changed) Changed?.Invoke();
    }

    private bool Connect(IPEndPoint from)
    {
        if (State != LinkState.Connecting) return false;
        _remote = from;
        return SetState(LinkState.Connected);
    }

    private bool OnData(byte[] frame)
    {
        if (frame.Length < 5) return false;
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(frame);
        bool delivered = false;
        if (seq == _expected)
        {
            delivered = Deliver(frame[5..], frame[4]);
            while (_early.Remove(_expected, out var next)) delivered |= Deliver(next.Data, next.Flags);
        }
        else if (seq - _expected < 1u << 30 && _early.Count < 8192)
        {
            _early[seq] = (frame[5..], frame[4]);
        }

        // Acknowledges what has arrived: everything before the next expected frame, and up to 64 frames after it.
        var ack = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(ack, _expected);
        ulong mask = 0;
        for (int i = 0; i < 64; i++)
            if (_early.ContainsKey(_expected + 1 + (uint)i)) mask |= 1UL << i;
        BinaryPrimitives.WriteUInt64BigEndian(ack.AsSpan(4), mask);
        SendPacket(Ack, ack, _remote!);
        return delivered;
    }

    private bool Deliver(byte[] data, byte flags)
    {
        _expected++;
        if ((flags & 1) != 0) _message = new MemoryStream();
        _message?.Write(data);
        if ((flags & 2) == 0 || _message == null) return false;
        _inbox.Enqueue(_message.ToArray());
        _message = null;
        return true;
    }

    private void OnAck(byte[] ack)
    {
        if (ack.Length < 12) return;
        uint expected = BinaryPrimitives.ReadUInt32BigEndian(ack);
        ulong mask = BinaryPrimitives.ReadUInt64BigEndian(ack.AsSpan(4));
        long now = Now;
        foreach (uint seq in _inFlight.Keys.ToList())
        {
            uint offset = seq - expected - 1;
            bool acked = seq - expected >= 1u << 31 || offset < 64 && (mask >> (int)offset & 1) != 0;
            if (!acked || !_inFlight.Remove(seq, out var p)) continue;
            if (p.Resends == 0) Sample(now - p.SentAt);
            _newestAckedSent = Math.Max(_newestAckedSent, p.SentAt);
            _window = Math.Min(_window < _threshold ? _window + 1 : _window + 1 / _window, 4096);
        }
    }

    /// <summary>Round-trip time estimate and resend timeout, as TCP computes them (RFC 6298).</summary>
    private void Sample(long rtt)
    {
        if (_srtt < 0)
        {
            _srtt = rtt;
            _rttVar = rtt / 2.0;
        }
        else
        {
            _rttVar = 0.75 * _rttVar + 0.25 * Math.Abs(_srtt - rtt);
            _srtt = 0.875 * _srtt + 0.125 * rtt;
        }

        _rto = Math.Clamp((long)(_srtt + 4 * _rttVar), 120, 3000);
    }

    public void Dispose()
    {
        Close();
        _aes.Dispose();
    }
}
