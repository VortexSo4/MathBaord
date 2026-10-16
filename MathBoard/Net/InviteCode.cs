using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace MathBoard.Net;

/// <summary>
/// The two codes people pass through a chat to connect, packed bit by bit. A code holds a random secret and where its
/// computer can be reached: the public IPv4 address (as a STUN server saw it), the address in its local network and,
/// if there is one, a public IPv6 address. Everything else is implied: 3 bits say the format version and which of the
/// two codes it is, 4 more which addresses follow and how the local one is written (192.168.x.y takes 2 bytes, 10.x.y.z
/// 3); the bits left in the last character check for typos. Written in Crockford's base 32 (no I, L, O or U, case does
/// not matter) in groups of five: an invite with two IPv4 addresses has 29 characters, its answer 26.
/// </summary>
public static class InviteCode
{
    public const int InviteSecretLength = 6, AnswerSecretLength = 4;
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int Version = 1, CheckBits = 8;

    public enum Kind : byte { Invite = 0, Answer = 1 }

    public sealed record Code(Kind Kind, byte[] Secret, List<IPEndPoint> Addresses);

    // How the local address is written: none, 192.168.x.y, 10.x.y.z, or all four bytes.
    private enum Local { None, Home, Ten, Full }

    public static string Encode(Code code)
    {
        var v4 = code.Addresses.Where(e => e.AddressFamily == AddressFamily.InterNetwork).ToList();
        var publicV4 = v4.FirstOrDefault(e => !IsPrivate(e.Address));
        var local = v4.FirstOrDefault(e => IsPrivate(e.Address)) ?? v4.FirstOrDefault(e => e != publicV4);
        var v6 = code.Addresses.FirstOrDefault(e => e.AddressFamily == AddressFamily.InterNetworkV6);
        var localBytes = local?.Address.GetAddressBytes();
        var localKind = localBytes == null ? Local.None
            : localBytes[0] == 192 && localBytes[1] == 168 ? Local.Home
            : localBytes[0] == 10 ? Local.Ten : Local.Full;

        var w = new Bits();
        w.Write(Version, 2);
        w.Write((int)code.Kind, 1);
        w.Write(publicV4 != null ? 1 : 0, 1);
        w.Write((int)localKind, 2);
        w.Write(v6 != null ? 1 : 0, 1);
        foreach (byte b in code.Secret) w.Write(b, 8);
        if (publicV4 != null) WriteEndPoint(w, publicV4.Address.GetAddressBytes(), publicV4.Port);
        if (local != null) WriteEndPoint(w, localKind switch { Local.Home => localBytes![2..], Local.Ten => localBytes![1..], _ => localBytes! }, local.Port);
        if (v6 != null) WriteEndPoint(w, v6.Address.GetAddressBytes(), v6.Port);

        // The check fills the last character, so its spare bits catch typos too.
        int length = (w.Count + CheckBits + 4) / 5;
        int check = length * 5 - w.Count;
        w.Write((int)(Check(w) & ((1UL << check) - 1)), check);
        return Group(w.ToBase32());
    }

    /// <summary>The code read back; null if it is mistyped or not a MathBoard code.</summary>
    public static Code? Decode(string text)
    {
        if (Bits.FromBase32(text) is not { } r || r.Count < 7 + CheckBits) return null;
        try
        {
            if (r.Read(2) != Version) return null;
            var kind = (Kind)r.Read(1);
            bool hasPublic = r.Read(1) == 1;
            var localKind = (Local)r.Read(2);
            bool hasV6 = r.Read(1) == 1;
            var secret = new byte[kind == Kind.Invite ? InviteSecretLength : AnswerSecretLength];
            for (int i = 0; i < secret.Length; i++) secret[i] = (byte)r.Read(8);
            var addresses = new List<IPEndPoint>();
            if (hasPublic) addresses.Add(ReadEndPoint(r, [], 4));
            if (localKind == Local.Home) addresses.Add(ReadEndPoint(r, [192, 168], 2));
            else if (localKind == Local.Ten) addresses.Add(ReadEndPoint(r, [10], 3));
            else if (localKind == Local.Full) addresses.Add(ReadEndPoint(r, [], 4));
            if (hasV6) addresses.Add(ReadEndPoint(r, [], 16));

            int data = r.Position, check = r.Count - data;
            if (check < CheckBits || r.Read(check) != (int)(Check(r.Prefix(data)) & ((1UL << check) - 1))) return null;
            return new Code(kind, secret, addresses);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool IsPrivate(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and < 32;
    }

    private static void WriteEndPoint(Bits w, byte[] address, int port)
    {
        foreach (byte b in address) w.Write(b, 8);
        w.Write(port, 16);
    }

    private static IPEndPoint ReadEndPoint(Bits r, byte[] implied, int count)
    {
        var bytes = new byte[implied.Length + count];
        implied.CopyTo(bytes, 0);
        for (int i = implied.Length; i < bytes.Length; i++) bytes[i] = (byte)r.Read(8);
        return new IPEndPoint(new IPAddress(bytes), r.Read(16));
    }

    private static ulong Check(Bits bits) => BitConverter.ToUInt64(SHA256.HashData(bits.ToBytes()));

    private static string Group(string s) => string.Join("-", Enumerable.Range(0, (s.Length + 4) / 5).Select(i => s.Substring(i * 5, Math.Min(5, s.Length - i * 5))));

    /// <summary>A string of bits, written and read most significant bit first.</summary>
    private sealed class Bits
    {
        private readonly List<bool> _bits = new();
        public int Position;
        public int Count => _bits.Count;

        public void Write(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--) _bits.Add((value >> i & 1) != 0);
        }

        public int Read(int count)
        {
            if (Position + count > _bits.Count) throw new ArgumentOutOfRangeException(nameof(count));
            int value = 0;
            for (int i = 0; i < count; i++) value = value << 1 | (_bits[Position++] ? 1 : 0);
            return value;
        }

        public Bits Prefix(int count)
        {
            var b = new Bits();
            b._bits.AddRange(_bits.Take(count));
            return b;
        }

        public byte[] ToBytes()
        {
            var bytes = new byte[(_bits.Count + 7) / 8 + 1];
            for (int i = 0; i < _bits.Count; i++)
                if (_bits[i]) bytes[i / 8] |= (byte)(0x80 >> (i % 8));
            bytes[^1] = (byte)_bits.Count;
            return bytes;
        }

        public string ToBase32()
        {
            var chars = new char[(_bits.Count + 4) / 5];
            for (int i = 0; i < chars.Length; i++)
            {
                int v = 0;
                for (int j = 0; j < 5; j++) v = v << 1 | (i * 5 + j < _bits.Count && _bits[i * 5 + j] ? 1 : 0);
                chars[i] = Alphabet[v];
            }

            return new string(chars);
        }

        public static Bits? FromBase32(string text)
        {
            var bits = new Bits();
            foreach (char raw in text.ToUpperInvariant())
            {
                if (raw is '-' or ' ' or '\t' or '\r' or '\n') continue;
                // Letters read as the digits they look like.
                int v = Alphabet.IndexOf(raw switch { 'O' => '0', 'I' or 'L' => '1', _ => raw });
                if (v < 0) return null;
                bits.Write(v, 5);
            }

            return bits;
        }
    }
}
