using System.IO.Compression;
using System.Numerics;
using MathBoard.Core;

namespace MathBoard.Collab;

/// <summary>
/// Keeps a lesson the same on several computers by sending what changed. It remembers what the other side has been sent
/// (strokes and objects by id, and their order) and compares the document with it: new or edited items go whole, a
/// stroke being drawn goes as the points added since, removed items by id, and the order only when it changed in another
/// way than items added at the end. Received changes are applied to the document and to its undo history, so undoing
/// takes back one's own edits and not the other people's. Pictures go separately, once, when the other side asks.
/// </summary>
public sealed class BoardSync(Document document)
{
    private enum Op : byte
    {
        StrokeUpsert = 1, StrokeTail, ObjectUpsert, Remove, Order, SnapshotStart, SnapshotEnd, ImageRequest, ImageData, StateHash, ResyncRequest,
        OrderTail, Batch, Applied
    }

    private readonly record struct SentStroke(List<Vector2> Points, int Count, Vector2 Last, Vector4 Color, float Width);

    private readonly Dictionary<long, SentStroke> _strokes = new();
    private readonly Dictionary<long, BoardObject> _objects = new();
    private List<long> _strokeOrder = [], _objectOrder = [];
    // Pictures this side lacks, and when they were last asked for.
    private readonly Dictionary<string, DateTime> _missingImages = new();

    // A participant numbers its messages and remembers, for each item it changed, the message that did it; the host
    // says which messages it has applied. A change from the host to an item whose own change the host has not applied
    // yet is older than that change and is skipped: the host takes this side's change and everybody ends with it.
    private int _batch, _hostApplied;
    private readonly Dictionary<long, int> _pending = new();
    // Items whose change from the host was skipped: they go whole in the next message, so the host takes this side's
    // version as it is here and not the host's own with a stroke's newest points added to it.
    private readonly HashSet<long> _resend = new();

    /// <summary>Whether the host has not yet applied some changes made here.</summary>
    public bool HasUnconfirmed => _pending.Values.Any(b => b > _hostApplied);

    /// <summary>The result of applying a message from another computer.</summary>
    public sealed class Received
    {
        public bool DocumentChanged, Snapshot, ResyncRequested;
        public ulong? Hash;
        public int? Batch;
        // Items this message added (they went to the end of their list here).
        public readonly List<long> AddedStrokes = new(), AddedObjects = new();
        // The message set an order (an undo puts an item back in its place): the host then sends its whole order.
        public bool StrokesReordered, ObjectsReordered;
        public readonly List<byte[]> Replies = new();
    }

    // ------------------------------------------------------------- sending

    /// <summary>
    /// The changes made here since the last call, as one message, or null if there are none. The stroke being drawn is
    /// left out until it is finished unless <paramref name="live"/>.
    /// </summary>
    /// <param name="numbered">For a participant: the message gets a number, for the host to confirm.</param>
    public byte[]? CollectChanges(Stroke? active, bool live, bool numbered = false)
    {
        var w = new Writer();
        var touched = new List<long>();
        var strokeOrder = new List<long>(document.Strokes.Count);
        var seen = new HashSet<long>();
        foreach (var s in document.Strokes)
        {
            if (ReferenceEquals(s, active) && !live) continue;
            strokeOrder.Add(s.Id);
            seen.Add(s.Id);
            var last = s.Points.Count > 0 ? s.Points[^1] : default;
            if (!_strokes.TryGetValue(s.Id, out var sent) || !ReferenceEquals(sent.Points, s.Points) && !ReferenceEquals(s, active) ||
                sent.Color != s.Color || sent.Width != s.Width || _resend.Remove(s.Id))
            {
                w.Op(Op.StrokeUpsert);
                WriteStroke(w.Data, s);
                touched.Add(s.Id);
            }
            else if (sent.Count != s.Points.Count || sent.Last != last || !ReferenceEquals(sent.Points, s.Points))
            {
                // The stroke being drawn: its last point is replaced by the smoothed curve, so the tail goes from just before it.
                int from = Math.Max(0, Math.Min(sent.Count, s.Points.Count) - 2);
                w.Op(Op.StrokeTail);
                w.Data.Write(s.Id);
                w.Data.Write(from);
                WritePoints(w.Data, s.Points.Skip(from).ToList());
                touched.Add(s.Id);
            }

            _strokes[s.Id] = new SentStroke(s.Points, s.Points.Count, last, s.Color, s.Width);
        }

        var objectOrder = new List<long>(document.Objects.Count);
        foreach (var o in document.Objects)
        {
            objectOrder.Add(o.Id);
            seen.Add(o.Id);
            if (_objects.TryGetValue(o.Id, out var sent) && ReferenceEquals(sent, o) && !_resend.Remove(o.Id)) continue;
            w.Op(Op.ObjectUpsert);
            WriteObject(w.Data, o);
            _objects[o.Id] = o;
            touched.Add(o.Id);
        }

        // A skipped change to an item removed here: the removal goes again.
        foreach (long id in _resend.Where(id => !seen.Contains(id) && !_strokes.ContainsKey(id) && !_objects.ContainsKey(id)).ToList())
        {
            w.Op(Op.Remove);
            w.Data.Write(id);
            touched.Add(id);
        }

        _resend.Clear();
        foreach (long id in _strokes.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            w.Op(Op.Remove);
            w.Data.Write(id);
            _strokes.Remove(id);
            touched.Add(id);
        }

        foreach (long id in _objects.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            w.Op(Op.Remove);
            w.Data.Write(id);
            _objects.Remove(id);
            touched.Add(id);
        }

        WriteOrderIfChanged(w, 0, _strokeOrder, strokeOrder);
        WriteOrderIfChanged(w, 1, _objectOrder, objectOrder);
        _strokeOrder = strokeOrder;
        _objectOrder = objectOrder;

        foreach (var (name, asked) in _missingImages.ToList())
        {
            if (ImageStore.HasFile(name))
            {
                _missingImages.Remove(name);
                continue;
            }

            if ((DateTime.Now - asked).TotalSeconds < 3) continue;
            _missingImages[name] = DateTime.Now;
            w.Op(Op.ImageRequest);
            w.Data.Write(name);
        }

        if (!numbered || !w.Any) return w.Finish();
        _batch++;
        foreach (long id in touched) _pending[id] = _batch;
        w.Op(Op.Batch);
        w.Data.Write(_batch);
        return w.Finish();
    }

    /// <summary>For the host: tells a participant that its messages up to this number are applied.</summary>
    public static byte[] AppliedMessage(int batch)
    {
        var w = new Writer();
        w.Op(Op.Applied);
        w.Data.Write(batch);
        return w.Finish()!;
    }

    /// <summary>Whether a change from the host to this item is older than a change made here that the host has not applied.</summary>
    private bool Stale(long id)
    {
        if (!_pending.TryGetValue(id, out int batch) || batch <= _hostApplied) return false;
        _resend.Add(id);
        return true;
    }

    /// <summary>
    /// The order is sent only when it is not what the other side gets anyway: the old order without the removed items,
    /// with the new ones appended.
    /// </summary>
    private static void WriteOrderIfChanged(Writer w, byte list, List<long> sent, List<long> now)
    {
        var present = now.ToHashSet();
        var known = sent.ToHashSet();
        var expected = sent.Where(present.Contains).Concat(now.Where(id => !known.Contains(id)));
        if (expected.SequenceEqual(now)) return;
        w.Op(Op.Order);
        w.Data.Write(list);
        w.Data.Write(now.Count);
        foreach (long id in now) w.Data.Write(id);
    }

    /// <summary>The whole lesson, for someone who just joined or whose copy differs; this side then counts it as sent.</summary>
    public byte[] Snapshot()
    {
        MarkSynced();
        var w = new Writer();
        w.Op(Op.SnapshotStart);
        foreach (var o in document.Objects)
        {
            w.Op(Op.ObjectUpsert);
            WriteObject(w.Data, o);
        }

        foreach (var s in document.Strokes)
        {
            w.Op(Op.StrokeUpsert);
            WriteStroke(w.Data, s);
        }

        w.Op(Op.SnapshotEnd);
        return w.Finish()!;
    }

    /// <summary>Counts the document as it is now as what the other side has (after applying what it sent).</summary>
    public void MarkSynced()
    {
        _strokes.Clear();
        foreach (var s in document.Strokes)
            _strokes[s.Id] = new SentStroke(s.Points, s.Points.Count, s.Points.Count > 0 ? s.Points[^1] : default, s.Color, s.Width);
        _objects.Clear();
        foreach (var o in document.Objects) _objects[o.Id] = o;
        _strokeOrder = document.Strokes.Select(s => s.Id).ToList();
        _objectOrder = document.Objects.Select(o => o.Id).ToList();
    }

    public static byte[] HashMessage(ulong hash)
    {
        var w = new Writer();
        w.Op(Op.StateHash);
        w.Data.Write(hash);
        return w.Finish()!;
    }

    public static byte[] ResyncRequest()
    {
        var w = new Writer();
        w.Op(Op.ResyncRequest);
        return w.Finish()!;
    }

    /// <summary>
    /// A fingerprint of the lesson (ids, order, colors, widths and points), the same on every computer that has the
    /// same lesson: the floats are sent exactly, so their bits can be hashed.
    /// </summary>
    public ulong Hash()
    {
        ulong h = 14695981039346656037;
        void Mix(ulong v)
        {
            h ^= v;
            h *= 1099511628211;
        }

        void MixFloat(float f) => Mix(BitConverter.SingleToUInt32Bits(f));
        foreach (var s in document.Strokes)
        {
            Mix((ulong)s.Id);
            MixFloat(s.Width);
            MixFloat(s.Color.X + s.Color.Y * 3 + s.Color.Z * 7 + s.Color.W * 11);
            Mix((ulong)s.Points.Count);
            foreach (var p in s.Points)
            {
                MixFloat(p.X);
                MixFloat(p.Y);
            }
        }

        foreach (var o in document.Objects)
        {
            Mix((ulong)o.Id);
            Mix((ulong)o.Kind);
            foreach (var p in o.Points)
            {
                MixFloat(p.X);
                MixFloat(p.Y);
            }
        }

        return h;
    }

    // ------------------------------------------------------------- receiving

    /// <summary>Applies a message from another computer to the document and its undo history.</summary>
    public Received Apply(byte[] message)
    {
        var result = new Received();
        using var r = new BinaryReader(Reader.Open(message));
        while (r.BaseStream.Position < r.BaseStream.Length)
        {
            var op = (Op)r.ReadByte();
            switch (op)
            {
                case Op.StrokeUpsert:
                {
                    var stroke = ReadStroke(r);
                    if (Stale(stroke.Id)) break;
                    if (!document.Strokes.Exists(s => s.Id == stroke.Id)) result.AddedStrokes.Add(stroke.Id);
                    ApplyEverywhere((strokes, _) => Upsert(strokes, stroke.Id, Copy(stroke)));
                    result.DocumentChanged = true;
                    break;
                }
                case Op.StrokeTail:
                {
                    long id = r.ReadInt64();
                    int from = r.ReadInt32();
                    var tail = ReadPoints(r);
                    if (Stale(id)) break;
                    ApplyEverywhere((strokes, _) =>
                    {
                        int i = strokes.FindIndex(s => s.Id == id);
                        if (i < 0) return;
                        var old = strokes[i];
                        strokes[i] = new Stroke { Id = id, Color = old.Color, Width = old.Width, Points = [..old.Points.Take(Math.Min(from, old.Points.Count)), ..tail] };
                    });
                    result.DocumentChanged = true;
                    break;
                }
                case Op.ObjectUpsert:
                {
                    var o = ReadObject(r);
                    if (Stale(o.Id)) break;
                    if (!document.Objects.Exists(x => x.Id == o.Id)) result.AddedObjects.Add(o.Id);
                    ApplyEverywhere((_, objects) => Upsert(objects, o.Id, o));
                    if (o.Kind == ShapeKind.Image && !ImageStore.HasFile(o.Text) && !_missingImages.ContainsKey(o.Text))
                    {
                        _missingImages[o.Text] = DateTime.Now;
                        result.Replies.Add(ImageRequestMessage(o.Text));
                    }

                    result.DocumentChanged = true;
                    break;
                }
                case Op.Remove:
                {
                    long id = r.ReadInt64();
                    if (Stale(id)) break;
                    ApplyEverywhere((strokes, objects) =>
                    {
                        strokes.RemoveAll(s => s.Id == id);
                        objects.RemoveAll(o => o.Id == id);
                    });
                    result.DocumentChanged = true;
                    break;
                }
                case Op.Order:
                {
                    byte list = r.ReadByte();
                    var ids = new long[r.ReadInt32()];
                    for (int i = 0; i < ids.Length; i++) ids[i] = r.ReadInt64();
                    ApplyEverywhere((strokes, objects) =>
                    {
                        if (list == 0) Reorder(strokes, s => s.Id, ids);
                        else Reorder(objects, o => o.Id, ids);
                    });
                    if (list == 0) result.StrokesReordered = true;
                    else result.ObjectsReordered = true;
                    result.DocumentChanged = true;
                    break;
                }
                case Op.OrderTail:
                {
                    var ids = new long[r.ReadInt32()];
                    for (int i = 0; i < ids.Length; i++) ids[i] = r.ReadInt64();
                    ApplyEverywhere((strokes, _) => MoveToEnd(strokes, ids));
                    result.DocumentChanged = true;
                    break;
                }
                case Op.SnapshotStart:
                    // The host's whole lesson replaces everything here, unconfirmed changes included.
                    _pending.Clear();
                    document.Strokes.Clear();
                    document.Objects.Clear();
                    document.ClearHistory();
                    result.Snapshot = result.DocumentChanged = true;
                    break;
                case Op.SnapshotEnd:
                    break;
                case Op.ImageRequest:
                {
                    string name = r.ReadString();
                    if (ImageStore.ReadFile(name) is { } bytes)
                    {
                        var w = new Writer();
                        w.Op(Op.ImageData);
                        w.Data.Write(name);
                        w.Data.Write(bytes.Length);
                        w.Data.Write(bytes);
                        result.Replies.Add(w.Finish()!);
                    }

                    break;
                }
                case Op.ImageData:
                {
                    string name = r.ReadString();
                    var bytes = r.ReadBytes(r.ReadInt32());
                    ImageStore.StoreFile(name, bytes);
                    _missingImages.Remove(name);
                    result.DocumentChanged = true;
                    break;
                }
                case Op.StateHash:
                    result.Hash = r.ReadUInt64();
                    break;
                case Op.ResyncRequest:
                    result.ResyncRequested = true;
                    break;
                case Op.Batch:
                    result.Batch = r.ReadInt32();
                    break;
                case Op.Applied:
                    _hostApplied = Math.Max(_hostApplied, r.ReadInt32());
                    foreach (long id in _pending.Where(p => p.Value <= _hostApplied).Select(p => p.Key).ToList()) _pending.Remove(id);
                    break;
                default:
                    throw new InvalidDataException($"Unknown change {op}");
            }
        }

        if (result.DocumentChanged) document.IsDirty = true;
        return result;
    }

    private static byte[] ImageRequestMessage(string name)
    {
        var w = new Writer();
        w.Op(Op.ImageRequest);
        w.Data.Write(name);
        return w.Finish()!;
    }

    private void ApplyEverywhere(Action<List<Stroke>, List<BoardObject>> change)
    {
        change(document.Strokes, document.Objects);
        document.ForEachHistoryState(change);
    }

    private static Stroke Copy(Stroke s) => new() { Id = s.Id, Color = s.Color, Width = s.Width, Points = s.Points };

    private static void Upsert(List<Stroke> list, long id, Stroke item)
    {
        int i = list.FindIndex(s => s.Id == id);
        if (i >= 0) list[i] = item;
        else list.Add(item);
    }

    private static void Upsert(List<BoardObject> list, long id, BoardObject item)
    {
        int i = list.FindIndex(o => o.Id == id);
        if (i >= 0) list[i] = item;
        else list.Add(item);
    }

    /// <summary>
    /// For the host after applying someone's message: the order of what it added or reordered, as the host has it.
    /// Items added at the same time on two computers end up in a different order on each; the host's order is the one
    /// everybody takes. For strokes added at the end only the end of the list from the first added one is sent;
    /// otherwise (an order in the message, or objects, where fills go to the front) the whole list.
    /// </summary>
    public byte[]? OrderFix(Received received)
    {
        var w = new Writer();
        if (received.StrokesReordered)
        {
            w.Op(Op.Order);
            w.Data.Write((byte)0);
            w.Data.Write(document.Strokes.Count);
            foreach (var s in document.Strokes) w.Data.Write(s.Id);
        }
        else if (received.AddedStrokes.Count > 0)
        {
            var added = received.AddedStrokes.ToHashSet();
            int first = document.Strokes.FindIndex(s => added.Contains(s.Id));
            if (first >= 0)
            {
                w.Op(Op.OrderTail);
                w.Data.Write(document.Strokes.Count - first);
                for (int i = first; i < document.Strokes.Count; i++) w.Data.Write(document.Strokes[i].Id);
            }
        }

        if (received.AddedObjects.Count > 0 || received.ObjectsReordered)
        {
            w.Op(Op.Order);
            w.Data.Write((byte)1);
            w.Data.Write(document.Objects.Count);
            foreach (var o in document.Objects) w.Data.Write(o.Id);
        }

        return w.Finish();
    }

    private static void MoveToEnd(List<Stroke> list, long[] ids)
    {
        var byId = list.Where(s => ids.Contains(s.Id)).ToDictionary(s => s.Id);
        list.RemoveAll(s => byId.ContainsKey(s.Id));
        list.AddRange(ids.Where(byId.ContainsKey).Select(id => byId[id]));
    }

    /// <summary>Puts the listed items in the given order; ones not listed (added here meanwhile) keep theirs after them.</summary>
    private static void Reorder<T>(List<T> list, Func<T, long> id, long[] order)
    {
        var byId = new Dictionary<long, T>();
        foreach (var item in list) byId.TryAdd(id(item), item);
        var listed = order.ToHashSet();
        var sorted = order.Where(byId.ContainsKey).Select(i => byId[i]).Concat(list.Where(item => !listed.Contains(id(item)))).ToList();
        list.Clear();
        list.AddRange(sorted);
    }

    // ------------------------------------------------------------- the format

    private static void WriteStroke(BinaryWriter w, Stroke s)
    {
        w.Write(s.Id);
        WriteVector(w, s.Color);
        w.Write(s.Width);
        WritePoints(w, s.Points);
    }

    private static Stroke ReadStroke(BinaryReader r) => new()
    {
        Id = r.ReadInt64(), Color = ReadVector(r), Width = r.ReadSingle(), Points = ReadPoints(r)
    };

    private static void WriteObject(BinaryWriter w, BoardObject o)
    {
        w.Write(o.Id);
        w.Write((byte)o.Kind);
        WriteVector(w, o.Color);
        w.Write(o.Width);
        WritePoints(w, o.Points);
        w.Write(o.Text);
        w.Write(o.XMin);
        w.Write(o.XMax);
    }

    private static BoardObject ReadObject(BinaryReader r)
    {
        long id = r.ReadInt64();
        var kind = (ShapeKind)r.ReadByte();
        var color = ReadVector(r);
        float width = r.ReadSingle();
        var points = ReadPoints(r).ToArray();
        return new BoardObject(kind, points, color, width) { Id = id, Text = r.ReadString(), XMin = r.ReadSingle(), XMax = r.ReadSingle() };
    }

    private static void WritePoints(BinaryWriter w, IReadOnlyList<Vector2> points)
    {
        w.Write(points.Count);
        foreach (var p in points)
        {
            w.Write(p.X);
            w.Write(p.Y);
        }
    }

    private static List<Vector2> ReadPoints(BinaryReader r)
    {
        int count = r.ReadInt32();
        var points = new List<Vector2>(count);
        for (int i = 0; i < count; i++) points.Add(new Vector2(r.ReadSingle(), r.ReadSingle()));
        return points;
    }

    private static void WriteVector(BinaryWriter w, Vector4 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
        w.Write(v.W);
    }

    private static Vector4 ReadVector(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    /// <summary>Changes written one after another; the message is compressed when that makes it smaller.</summary>
    private sealed class Writer
    {
        private readonly MemoryStream _stream = new();
        public readonly BinaryWriter Data;
        private bool _any;

        public Writer() => Data = new BinaryWriter(_stream);

        public bool Any => _any;

        public void Op(Op op)
        {
            Data.Write((byte)op);
            _any = true;
        }

        public byte[]? Finish()
        {
            if (!_any) return null;
            Data.Flush();
            var raw = _stream.ToArray();
            if (raw.Length >= 256)
            {
                using var packed = new MemoryStream();
                packed.WriteByte(1);
                using (var deflate = new DeflateStream(packed, CompressionLevel.Fastest, leaveOpen: true)) deflate.Write(raw);
                if (packed.Length < raw.Length) return packed.ToArray();
            }

            return [0, ..raw];
        }
    }

    private static class Reader
    {
        public static Stream Open(byte[] message)
        {
            var body = new MemoryStream(message, 1, message.Length - 1);
            if (message[0] == 0) return body;
            var raw = new MemoryStream();
            using (var deflate = new DeflateStream(body, CompressionMode.Decompress)) deflate.CopyTo(raw);
            raw.Position = 0;
            return raw;
        }
    }
}
