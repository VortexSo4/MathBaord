using System.Numerics;
using System.Linq;
using System.Collections.Generic;

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
}