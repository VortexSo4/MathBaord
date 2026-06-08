using Silk.NET.Input;
using System.Numerics;
using MathBoard.Rendering;
using Silk.NET.Windowing;

namespace MathBoard.Core;

public sealed class InputManager : IDisposable
{
    private readonly IInputContext _input;
    private readonly StrokeRenderer _strokeRenderer;
    private IMouse? _mouse;
    private readonly IWindow _window;

    private bool _isDrawing = false;

    public InputManager(IWindow window, StrokeRenderer strokeRenderer)
    {
        _strokeRenderer = strokeRenderer;
        _input = window.CreateInput();
        _window = window;

        _mouse = _input.Mice.FirstOrDefault();
        if (_mouse != null)
        {
            _mouse.MouseDown += OnMouseDown;
            _mouse.MouseUp += OnMouseUp;
            _mouse.MouseMove += OnMouseMove;
        }
    }

    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        if (button != MouseButton.Left) return;
        var pos = GetMousePosition(mouse, _window);
        _strokeRenderer.BeginStroke(pos);
        _isDrawing = true;
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        if (!_isDrawing) return;
        var pos = GetMousePosition(mouse, _window);
        _strokeRenderer.AddPoint(pos);
    }

    private void OnMouseUp(IMouse mouse, MouseButton button)
    {
        if (button != MouseButton.Left) return;
        Console.WriteLine("[MouseUp]");
        _strokeRenderer.EndStroke();
        _isDrawing = false;
    }

    private Vector2 GetMousePosition(IMouse mouse, IWindow window)
    {
        var pos = mouse.Position;
        return new Vector2(pos.X, window.Size.Y - pos.Y);
    }

    public void Dispose()
    {
        if (_mouse != null)
        {
            _mouse.MouseDown -= OnMouseDown;
            _mouse.MouseUp -= OnMouseUp;
            _mouse.MouseMove -= OnMouseMove;
        }
    }
}