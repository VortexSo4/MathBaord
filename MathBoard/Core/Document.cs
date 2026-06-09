using System.Numerics;

namespace MathBoard.Core;

public class Document
{
    public List<Stroke> Strokes { get; } = [];
    
    // Для Undo/Redo
    private readonly Stack<List<Stroke>> _history = new();
    private readonly Stack<List<Stroke>> _redoStack = new();
    private bool _isUndoing = false;

    public void SaveState()
    {
        if (_isUndoing) return;
        
        _redoStack.Clear();
        _history.Push([
            ..Strokes.Select(s => new Stroke
            {
                Width = s.Width,
                Color = s.Color,
                Points = [..s.Points]
            })
        ]);
        
        // Ограничение истории
        if (_history.Count > 50)
            _history.Pop(); // удаляем самое старое
    }

    public void Undo()
    {
        if (_history.Count == 0) return;
        
        _isUndoing = true;
        _redoStack.Push([
            ..Strokes.Select(s => new Stroke
            {
                Width = s.Width,
                Color = s.Color,
                Points = [..s.Points]
            })
        ]);
        
        Strokes.Clear();
        var previous = _history.Pop();
        foreach (var s in previous)
            Strokes.Add(s);
        
        _isUndoing = false;
    }

    public void Redo()
    {
        if (_redoStack.Count == 0) return;
        
        _history.Push([
            ..Strokes.Select(s => new Stroke
            {
                Width = s.Width,
                Color = s.Color,
                Points = [..s.Points]
            })
        ]);
        
        Strokes.Clear();
        var next = _redoStack.Pop();
        foreach (var s in next)
            Strokes.Add(s);
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
    }
}