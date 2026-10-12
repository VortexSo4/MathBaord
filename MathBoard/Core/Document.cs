using System.Numerics;

namespace MathBoard.Core;

public class Document
{
    private const int MaxHistory = 50;

    public List<Stroke> Strokes { get; } = [];
    public bool IsDirty { get; set; } = false;

    // Snapshots share point lists with the live strokes, so a finished stroke's list must be
    // replaced rather than modified. Only the stroke that is being drawn gets points appended.
    private readonly List<List<Stroke>> _history = new();
    private readonly List<List<Stroke>> _redoStack = new();

    private List<Stroke> Snapshot() =>
        [..Strokes.Select(s => new Stroke { Width = s.Width, Color = s.Color, Points = s.Points })];

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

    private bool Restore(List<List<Stroke>> from, List<List<Stroke>> to)
    {
        if (from.Count == 0) return false;
        to.Add(Snapshot());
        Strokes.Clear();
        Strokes.AddRange(from[^1]);
        from.RemoveAt(from.Count - 1);
        IsDirty = true;
        return true;
    }

    public void Clear()
    {
        Strokes.Clear();
        _history.Clear();
        _redoStack.Clear();
        IsDirty = false;
    }

    public void SaveToFile(string path)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        // Сигнатура "MTHB" и версия
        writer.Write(0x4D544842); // 'MTHB' as int
        writer.Write((byte)1);    // версия

        writer.Write(Strokes.Count);
        foreach (var stroke in Strokes)
        {
            // Цвет (4 float)
            writer.Write(stroke.Color.X);
            writer.Write(stroke.Color.Y);
            writer.Write(stroke.Color.Z);
            writer.Write(stroke.Color.W);
            // Толщина
            writer.Write(stroke.Width);
            // Количество точек
            writer.Write(stroke.Points.Count);
            // Точки
            foreach (var p in stroke.Points)
            {
                writer.Write(p.X);
                writer.Write(p.Y);
            }
        }
    }

    public void LoadFromFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        int sig = reader.ReadInt32();
        if (sig != 0x4D544842) throw new InvalidDataException("Not a MathBoard file");
        byte version = reader.ReadByte();
        if (version != 1) throw new InvalidDataException($"Unsupported version {version}");

        int strokeCount = reader.ReadInt32();
        var newStrokes = new List<Stroke>();
        for (int i = 0; i < strokeCount; i++)
        {
            var color = new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            float width = reader.ReadSingle();
            int pointCount = reader.ReadInt32();
            var points = new List<Vector2>(pointCount);
            for (int j = 0; j < pointCount; j++)
            {
                points.Add(new Vector2(reader.ReadSingle(), reader.ReadSingle()));
            }
            newStrokes.Add(new Stroke { Color = color, Width = width, Points = points });
        }

        // Заменяем содержимое
        Strokes.Clear();
        Strokes.AddRange(newStrokes);

        // Сбрасываем историю
        _history.Clear();
        _redoStack.Clear();
        IsDirty = false;
    }
}
