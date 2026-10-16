using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MathBoard.Core;
using MathBoard.Net;

namespace MathBoard.Collab;

/// <summary>
/// Working on one lesson from several computers without a server. The host (usually the board) invites: its code holds
/// where it can be reached and a secret; the one joining answers with a code of its own, and once the host enters it
/// both connect directly (see <see cref="PeerLink"/>). The host's lesson is the shared one: someone who joins gets a new
/// lesson filled with it, changes go to the host, and the host passes them on to everyone else. Every couple of seconds
/// the host sends a fingerprint of the lesson; a participant whose copy differs asks for the whole lesson again.
/// </summary>
public sealed class CollabSession : IDisposable
{
    public enum Role { None, Host, Participant }

    private const long HashIntervalMs = 2000;

    private sealed class Peer(PeerLink link)
    {
        public readonly PeerLink Link = link;
        public bool Welcomed;
        public int Mismatches;
    }

    private readonly Document _document;
    private readonly BoardSync _sync;
    private readonly List<Peer> _peers = new();
    private readonly object _lock = new();
    private UdpTransport? _transport;
    private List<IPEndPoint> _addresses = [];
    private byte[]? _inviteSecret;
    private CancellationTokenSource? _ticker;
    private long _lastSent, _lastHash;
    private bool _prepared;
    private int _lastConnectedCount;

    public Role CurrentRole { get; private set; }

    /// <summary>Whether a participant whose copy differs asks for the whole lesson again (off only to find why it differs).</summary>
    public bool Resync { get; set; } = true;

    /// <summary>A participant's board must be a new lesson before the host's arrives: called once when connected.</summary>
    public Action? PrepareBoard { get; set; }

    /// <summary>Raised from any thread when something arrived or the connection changed; the app should wake up.</summary>
    public event Action? Changed;

    public CollabSession(Document document)
    {
        _document = document;
        _sync = new BoardSync(document);
    }

    public int ConnectedCount
    {
        get
        {
            lock (_lock) return _peers.Count(p => p.Link.State == PeerLink.LinkState.Connected);
        }
    }

    public bool IsConnecting
    {
        get
        {
            lock (_lock) return _peers.Any(p => p.Link.State == PeerLink.LinkState.Connecting);
        }
    }

    /// <summary>Whether the host has made an invite whose answer has not been entered yet.</summary>
    public bool HasOpenInvite => _inviteSecret != null;

    // ------------------------------------------------------------- codes

    /// <summary>Starts hosting (if not yet) and makes an invite code; null if no address could be found.</summary>
    public async Task<string?> CreateInviteAsync()
    {
        if (CurrentRole == Role.Participant) return null;
        if (!await EnsureTransportAsync()) return null;
        CurrentRole = Role.Host;
        _inviteSecret = RandomNumberGenerator.GetBytes(InviteCode.InviteSecretLength);
        return InviteCode.Encode(new InviteCode.Code(InviteCode.Kind.Invite, _inviteSecret, _addresses));
    }

    /// <summary>The host enters the answer to its last invite and starts connecting; returns an error to show, or null.</summary>
    public string? AcceptAnswer(string text)
    {
        if (CurrentRole != Role.Host || _inviteSecret == null) return "no_invite";
        if (InviteCode.Decode(text) is not { Kind: InviteCode.Kind.Answer } answer) return "bad_code";
        var (key, connection) = Derive(_inviteSecret, answer.Secret);
        AddPeer(new PeerLink(_transport!, connection, key, host: true, answer.Addresses));
        _inviteSecret = null;
        return null;
    }

    /// <summary>Joins with the host's invite code; returns the answer code to send back, or null if the code is wrong.</summary>
    public async Task<string?> JoinAsync(string text)
    {
        if (CurrentRole == Role.Host || InviteCode.Decode(text) is not { Kind: InviteCode.Kind.Invite } invite) return null;
        if (!await EnsureTransportAsync()) return null;
        CurrentRole = Role.Participant;
        _prepared = false;
        var secret = RandomNumberGenerator.GetBytes(InviteCode.AnswerSecretLength);
        var (key, connection) = Derive(invite.Secret, secret);
        lock (_lock)
        {
            foreach (var p in _peers) p.Link.Dispose();
            _peers.Clear();
        }

        // The participant starts knocking at once: its packets open its own NAT for the host, whose packets start
        // once the host has entered the answer.
        AddPeer(new PeerLink(_transport!, connection, key, host: false, invite.Addresses));
        return InviteCode.Encode(new InviteCode.Code(InviteCode.Kind.Answer, secret, _addresses));
    }

    private static (byte[] Key, uint Connection) Derive(byte[] invite, byte[] answer)
    {
        var hash = SHA256.HashData([..Encoding.ASCII.GetBytes("MathBoard collab 1"), ..invite, ..answer]);
        return (hash[..16], BitConverter.ToUInt32(hash, 16) | 1);
    }

    private async Task<bool> EnsureTransportAsync()
    {
        if (_transport != null) return _addresses.Count > 0;
        _transport = new UdpTransport();
        _addresses = await NetworkCheck.GatherAsync(_transport);
        _ticker = new CancellationTokenSource();
        var token = _ticker.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                Peer[] peers;
                lock (_lock) peers = _peers.ToArray();
                foreach (var p in peers) p.Link.Tick();
                await Task.Delay(5, CancellationToken.None);
            }
        });
        Console.WriteLine($"Collaboration: addresses {string.Join(", ", _addresses)}");
        return _addresses.Count > 0;
    }

    private void AddPeer(PeerLink link)
    {
        link.MaxPayload = Math.Clamp((int)Settings.NetPacketBytes.Value, 200, 1100);
        link.Changed += () => Changed?.Invoke();
        lock (_lock) _peers.Add(new Peer(link));
        Changed?.Invoke();
    }

    /// <summary>Ends the session: the lesson stays on every computer as it is.</summary>
    public void Leave()
    {
        lock (_lock)
        {
            foreach (var p in _peers) p.Link.Dispose();
            _peers.Clear();
        }

        _ticker?.Cancel();
        _transport?.Dispose();
        _transport = null;
        _addresses = [];
        _inviteSecret = null;
        CurrentRole = Role.None;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------- every frame, on the app's thread

    private static long Now => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    /// <summary>
    /// Sends what changed here and applies what arrived. Changes from others wait while something is being drawn or
    /// dragged here (<paramref name="idle"/> is false), as they could change what that edit works on. Returns true when
    /// the document changed.
    /// </summary>
    public bool Pump(bool idle, Stroke? activeStroke)
    {
        if (CurrentRole == Role.None) return false;
        Peer[] peers;
        lock (_lock) peers = _peers.Where(p => p.Link.State != PeerLink.LinkState.Closed).ToArray();
        var connected = peers.Where(p => p.Link.State == PeerLink.LinkState.Connected).ToArray();
        if (connected.Length != _lastConnectedCount)
        {
            _lastConnectedCount = connected.Length;
            Console.WriteLine($"Collaboration: {connected.Length} connected");
        }

        bool changed = false;
        if (CurrentRole == Role.Participant && connected.Length > 0 && !_prepared)
        {
            if (!idle) return false;
            PrepareBoard?.Invoke();
            _prepared = true;
            changed = true;
        }

        // A new participant gets the whole lesson first.
        foreach (var p in connected.Where(p => !p.Welcomed && CurrentRole == Role.Host))
        {
            if (!idle) break;
            SendLocalChanges(connected.Where(o => o.Welcomed), activeStroke, force: true);
            p.Link.Send(_sync.Snapshot());
            p.Welcomed = true;
        }

        if (CurrentRole == Role.Participant)
            foreach (var p in connected) p.Welcomed = _prepared;

        var ready = connected.Where(p => p.Welcomed).ToArray();
        if (Now - _lastSent >= Settings.NetSendIntervalMs.Value) SendLocalChanges(ready, activeStroke, force: false);
        if (idle) changed |= ApplyIncoming(ready, activeStroke);

        if (CurrentRole == Role.Host && Now - _lastHash >= HashIntervalMs && ready.Length > 0 && idle)
        {
            _lastHash = Now;
            // The host's own latest changes go first, so the fingerprint is of what the others have by then.
            SendLocalChanges(ready, activeStroke, force: false);
            var hash = BoardSync.HashMessage(_sync.Hash());
            foreach (var p in ready) p.Link.Send(hash);
        }

        return changed;
    }

    private void SendLocalChanges(IEnumerable<Peer> to, Stroke? activeStroke, bool force)
    {
        _lastSent = Now;
        if (_sync.CollectChanges(activeStroke, Settings.NetLiveStrokes.Value || force, numbered: CurrentRole == Role.Participant) is not { } message) return;
        foreach (var p in to) p.Link.Send(message);
    }

    private bool ApplyIncoming(Peer[] peers, Stroke? activeStroke)
    {
        bool changed = false;
        foreach (var from in peers)
        {
            while (from.Link.TryReceive(out var message))
            {
                // What changed here is sent before the other side's changes are taken in.
                SendLocalChanges(peers, activeStroke, force: false);
                BoardSync.Received received;
                try
                {
                    received = _sync.Apply(message);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Collaboration: bad message ({ex.Message}), asking for the whole lesson");
                    if (CurrentRole == Role.Participant) from.Link.Send(BoardSync.ResyncRequest());
                    continue;
                }

                foreach (var reply in received.Replies) from.Link.Send(reply);
                if (received.Batch is { } batch && CurrentRole == Role.Host) from.Link.Send(BoardSync.AppliedMessage(batch));
                if (received.DocumentChanged)
                {
                    _sync.MarkSynced();
                    changed = true;
                    if (CurrentRole == Role.Host)
                    {
                        // Passed on to everyone else, then the host's order of what was added goes to everyone.
                        foreach (var other in peers.Where(p => p != from)) other.Link.Send(message);
                        if (_sync.OrderFix(received) is { } fix)
                            foreach (var p in peers) p.Link.Send(fix);
                    }
                }

                if (received.ResyncRequested && CurrentRole == Role.Host) from.Link.Send(_sync.Snapshot());
                if (received.Hash is { } hash && CurrentRole == Role.Participant) CompareHash(from, hash);
            }
        }

        return changed;
    }

    /// <summary>Two fingerprints in a row that differ, with nothing of its own on the way, mean this copy went astray.</summary>
    private void CompareHash(Peer host, ulong hash)
    {
        if (host.Link.Backlog > 0 || _sync.HasUnconfirmed) return;
        host.Mismatches = hash == _sync.Hash() ? 0 : host.Mismatches + 1;
        if (host.Mismatches < 2 || !Resync) return;
        host.Mismatches = 0;
        Console.WriteLine("Collaboration: lesson differs from the host's, asking for it again");
        host.Link.Send(BoardSync.ResyncRequest());
    }

    public void Dispose() => Leave();
}
