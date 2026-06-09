using Silk.NET.Input;
using System.Numerics;
using MathBoard.Rendering;
using Silk.NET.Windowing;
using System;

namespace MathBoard.Core;

public sealed class InputManager : IDisposable
{
    private readonly IInputContext _input;
    private readonly StrokeRenderer _strokeRenderer;
    private readonly Camera _camera;
    private readonly Document _document;
    private readonly RadialMenu _radialMenu;        // ← новое

    private IMouse? _mouse;
    private IKeyboard? _keyboard;
    private readonly IWindow _window;

    private bool _isDrawing = false;
    private Vector2 _lastPanPosition;
    private DateTime _mouseDownTime = DateTime.MinValue;
    private Vector2 _mouseDownPos;

    public InputManager(IWindow window, StrokeRenderer strokeRenderer, Camera camera, Document document, RadialMenu radialMenu)
    {
        _strokeRenderer = strokeRenderer;
        _camera = camera;
        _document = document;
        _window = window;
        _input = window.CreateInput();
        _radialMenu = radialMenu;;

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
            _mouseDownTime = DateTime.Now;
            _mouseDownPos = pos;

            if (!_radialMenu.IsOpen)
            {
                _strokeRenderer.BeginStroke(pos);
                _isDrawing = true;
            }
        }
        else if (button == MouseButton.Right || button == MouseButton.Middle)
        {
            _lastPanPosition = pos;
        }
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        var pos = GetMousePosition(mouse, _window);

        if (_radialMenu.IsOpen)
        {
            _radialMenu.OnMouseMove(pos);
            _strokeRenderer.SetDirty();
            return;
        }

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
        var pos = GetMousePosition(mouse, _window);

        if (button == MouseButton.Left)
        {
            var holdTime = (DateTime.Now - _mouseDownTime).TotalSeconds;

            if (_radialMenu.IsOpen)
            {
                _radialMenu.OnMouseUp(pos);
            }
            else if (holdTime > 0.30f && Vector2.Distance(_mouseDownPos, pos) < 20f)
            {
                _radialMenu.OpenAt(pos);
            }
            else if (_isDrawing)
            {
                _strokeRenderer.EndStroke();
                _isDrawing = false;
            }
        }
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
            _strokeRenderer.SetDirty();
        }

        if (key == Key.Escape && _radialMenu.IsOpen)
        {
            _radialMenu.Close();
            _strokeRenderer.SetDirty();
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
}