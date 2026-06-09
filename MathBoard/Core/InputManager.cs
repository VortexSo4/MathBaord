using Silk.NET.Input;
using System.Numerics;
using MathBoard.Rendering;
using Silk.NET.Windowing;

namespace MathBoard.Core;

public sealed class InputManager : IDisposable
{
    private readonly IInputContext _input;
    private readonly StrokeRenderer _strokeRenderer;
    private readonly Camera _camera;
    private readonly Document _document;
    private IMouse? _mouse;
    private IKeyboard? _keyboard;
    private readonly IWindow _window;

    private bool _isDrawing = false;
    private Vector2 _lastPanPosition;

    public InputManager(IWindow window, StrokeRenderer strokeRenderer, Camera camera, Document document)
    {
        _strokeRenderer = strokeRenderer;
        _camera = camera;
        _document = document;
        _input = window.CreateInput();
        _window = window;

        _mouse = _input.Mice.FirstOrDefault();
        _keyboard = _input.Keyboards.FirstOrDefault();

        if (_mouse != null)
        {
            _mouse.MouseDown += OnMouseDown;
            _mouse.MouseUp += OnMouseUp;
            _mouse.MouseMove += OnMouseMove;
            _mouse.Scroll += OnMouseWheel;
        }
        
        if (_keyboard != null)
        {
            _keyboard.KeyDown += OnKeyDown;
        }
    }

    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        var pos = GetMousePosition(mouse, _window);

        if (button == MouseButton.Left)
        {
            _strokeRenderer.BeginStroke(pos);
            _isDrawing = true;
        }
        else if (button == MouseButton.Right || button == MouseButton.Middle)
        {
            _lastPanPosition = pos;
        }
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        var pos = GetMousePosition(mouse, _window);

        if (_isDrawing)
        {
            _strokeRenderer.AddPoint(pos);
        }
        else if (_mouse?.IsButtonPressed(MouseButton.Right) == true ||
                 _mouse?.IsButtonPressed(MouseButton.Middle) == true)
        {
            var delta = pos - _lastPanPosition;
            _camera.Position += delta;
            _lastPanPosition = pos;
            _strokeRenderer.SetDirty();
        }
    }

    private void OnMouseUp(IMouse mouse, MouseButton button)
    {
        if (button == MouseButton.Left)
        {
            _strokeRenderer.EndStroke();
            _isDrawing = false;
        }
    }

    private void OnMouseWheel(IMouse mouse, ScrollWheel wheel)
    {
        var screenPos = GetMousePosition(mouse, _window);

        bool ctrlPressed = _keyboard?.IsKeyPressed(Key.ControlLeft) == true ||
                           _keyboard?.IsKeyPressed(Key.ControlRight) == true;
        bool shiftPressed = _keyboard?.IsKeyPressed(Key.ShiftLeft) == true ||
                            _keyboard?.IsKeyPressed(Key.ShiftRight) == true;

        if (ctrlPressed)
        {
            var worldBefore = _strokeRenderer.ScreenToWorld(screenPos);
            float zoomFactor = 1.0f + wheel.Y * 0.15f;
            _camera.Zoom = Math.Clamp(_camera.Zoom * zoomFactor, 0.1f, 30f);
            _camera.Position = screenPos - worldBefore * _camera.Zoom;
        }
        else if (shiftPressed)
        {
            _camera.Position += new Vector2(wheel.Y * 35f, 0);
        }
        else
        {
            _camera.Position += new Vector2(0, wheel.Y * 35f);
        }

        _strokeRenderer.SetDirty();
    }

    private Vector2 GetMousePosition(IMouse mouse, IWindow window)
    {
        var pos = mouse.Position;
        return new Vector2(pos.X, pos.Y);
    }

    public void Dispose()
    {
        if (_mouse != null)
        {
            _mouse.MouseDown -= OnMouseDown;
            _mouse.MouseUp -= OnMouseUp;
            _mouse.MouseMove -= OnMouseMove;
            _mouse.Scroll -= OnMouseWheel;
        }
        if (_keyboard != null)
            _keyboard.KeyDown -= OnKeyDown;
    }
    
    private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        bool ctrl = keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight);
        bool shift = keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight);

        if (ctrl && key == Key.Z)
        {
            if (shift)
                _strokeRenderer.Redo();
            else
                _strokeRenderer.Undo();
        
            _strokeRenderer.SetDirty();
        }

        if (key == Key.E)
        {
            _strokeRenderer.ToggleEraser();
        }
    }
}