using System.Numerics;

namespace MathBoard.Core;

public class Document
{
    private const int MaxHistory = 50;
    private const int Signature = 0x4D544842; // 'MTHB'
    private const byte Version = 2;

    public List<Stroke> Strokes { get; } = [];

    /// <summary>Shapes, graphs and geometry. They are drawn below the ink.</summary>
    public List<BoardObject> Objects { get; } = [];

    public bool IsDirty { get; set; } = false;

    // Snapshots share point lists with the live strokes, so a finished stroke's list must be
    // replaced rather than modified. Only the stroke that is being drawn gets points appended.
    // Objects are immutable, so snapshots simply keep references to them.
    private readonly record struct State(List<Stroke> Strokes, List<BoardObject> Objects);

    private readonly List<State> _history = new();
    private readonly List<State> _redoStack = new();

    private State Snapshot() => new(
        [..Strokes.Select(s => new Stroke { Width = s.Width, Color = s.Color, Points = s.Points })],
        [..Objects]);

    public void SaveState()
    {
        IsDirty = true;
        _redoStack.Clear();
        _history.Add(Snapshot());
        if (_history.Count > MaxHistory)
            _history.RemoveAt(0);
    }

    /// <summary>Drops the snapshot taken for an edit that was cancelled.</summary>
    public void DiscardLastState()
    {
        if (_history.Count > 0) _history.RemoveAt(_history.Count - 1);
    }

    public bool Undo() => Restore(_history, _redoStack);

    public bool Redo() => Restore(_redoStack, _history);

    private bool Restore(List<State> from, List<State> to)
    {
        if (from.Count == 0) return false;
        to.Add(Snapshot());
        var state = from[^1];
        from.RemoveAt(from.Count - 1);
        Strokes.Clear();
        Strokes.AddRange(state.Strokes);
        Objects.Clear();
        Objects.AddRange(state.Objects);
        IsDirty = true;
        return true;
    }

    public void Clear()
    {
        Strokes.Clear();
        Objects.Clear();
        _history.Clear();
        _redoStack.Clear();
        IsDirty = false;
    }

    public void SaveToFile(string path)
    {
        // Written to a temporary file first so a crash never leaves a half-written lesson.
        string temp = path + ".tmp";
        using (var stream = File.Create(temp))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Signature);
            writer.Write(Version);

            writer.Write(Strokes.Count);
            foreach (var stroke in Strokes)
            {
                WriteColor(writer, stroke.Color);
                writer.Write(stroke.Width);
                writer.Write(stroke.Points.Count);
                foreach (var p in stroke.Points)
                {
                    writer.Write(p.X);
                    writer.Write(p.Y);
                }
            }

            writer.Write(Objects.Count);
            foreach (var o in Objects)
            {
                writer.Write((byte)o.Kind);
                WriteColor(writer, o.Color);
                writer.Write(o.Width);
                writer.Write(o.Points.Length);
                foreach (var p in o.Points)
                {
                    writer.Write(p.X);
                    writer.Write(p.Y);
                }

                writer.Write(o.Functions);
                writer.Write(o.XMin);
                writer.Write(o.XMax);
            }
        }

        File.Move(temp, path, overwrite: true);
    }

    private static void WriteColor(BinaryWriter writer, Vector4 c)
    {
        writer.Write(c.X);
        writer.Write(c.Y);
        writer.Write(c.Z);
        writer.Write(c.W);
    }

    private static Vector4 ReadColor(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    public void LoadFromFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        if (reader.ReadInt32() != Signature) throw new InvalidDataException("Not a MathBoard file");
        byte version = reader.ReadByte();
        if (version is < 1 or > Version) throw new InvalidDataException($"Unsupported version {version}");

        var strokes = new List<Stroke>();
        int strokeCount = reader.ReadInt32();
        for (int i = 0; i < strokeCount; i++)
        {
            var color = ReadColor(reader);
            float width = reader.ReadSingle();
            int pointCount = reader.ReadInt32();
            var points = new List<Vector2>(pointCount);
            for (int j = 0; j < pointCount; j++)
                points.Add(new Vector2(reader.ReadSingle(), reader.ReadSingle()));
            strokes.Add(new Stroke { Color = color, Width = width, Points = points });
        }

        var objects = new List<BoardObject>();
        if (version >= 2)
        {
            int objectCount = reader.ReadInt32();
            for (int i = 0; i < objectCount; i++)
            {
                var kind = (ShapeKind)reader.ReadByte();
                var color = ReadColor(reader);
                float width = reader.ReadSingle();
                var points = new Vector2[reader.ReadInt32()];
                for (int j = 0; j < points.Length; j++)
                    points[j] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                objects.Add(new BoardObject(kind, points, color, width)
                {
                    Functions = reader.ReadString(),
                    XMin = reader.ReadSingle(),
                    XMax = reader.ReadSingle()
                });
            }
        }

        Strokes.Clear();
        Strokes.AddRange(strokes);
        Objects.Clear();
        Objects.AddRange(objects);

        _history.Clear();
        _redoStack.Clear();
        IsDirty = false;
    }
}
