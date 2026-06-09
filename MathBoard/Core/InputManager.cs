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
    private readonly RadialMenu _radialMenu;
    private readonly LibraryManager? _libraryManager;
    
    private IMouse? _mouse;
    private IKeyboard? _keyboard;
    private readonly IWindow _window;

    private bool _isDrawing = false;
    private Vector2 _lastPanPosition;
    private Vector2 _mouseDownPos;

    // Новое поведение: меню открывается только после долгого нажатия
    private bool _menuPending = false;
    private DateTime _mouseDownTime;

    public InputManager(IWindow window, StrokeRenderer strokeRenderer, Camera camera, Document document, RadialMenu radialMenu, LibraryManager libraryManager)
    {
        _strokeRenderer = strokeRenderer;
        _camera = camera;
        _document = document;
        _window = window;
        _input = window.CreateInput();
        _radialMenu = radialMenu;
        _libraryManager = libraryManager;

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
            _mouseDownPos = pos;
            _mouseDownTime = DateTime.Now;
            _menuPending = true;

            if (_radialMenu.IsOpen)
            {
                _radialMenu.OnMouseDown(pos);
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

        // Если меню уже открыто — передаём движение в меню
        if (_radialMenu.IsOpen)
        {
            _radialMenu.OnMouseMove(pos);
            _strokeRenderer.SetDirty();
            return;
        }

        // Проверка отмены открытия меню при быстром движении
        if (_menuPending)
        {
            float elapsed = (float)(DateTime.Now - _mouseDownTime).TotalSeconds;
            float dist = Vector2.Distance(_mouseDownPos, pos);

            // Отмена меню при быстром движении
            if (dist > Settings.RadialMenuEscapeDistance && elapsed < Settings.RadialMenuEscapeTime)
            {
                _menuPending = false;
                _strokeRenderer.BeginStroke(_mouseDownPos);
                _strokeRenderer.AddPoint(pos);
                _isDrawing = true;
                return;
            }

            if (elapsed >= Settings.RadialMenuOpenThreshold)
            {
                _menuPending = false;
                _radialMenu.OpenAt(_mouseDownPos);
                _radialMenu.OnMouseDown(pos);
                _strokeRenderer.SetDirty();
                return;
            }
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
            if (_radialMenu.IsOpen)
            {
                _radialMenu.OnMouseUp(pos);
            }
            else if (_isDrawing)
            {
                _strokeRenderer.EndStroke();
                _isDrawing = false;
            }

            _menuPending = false;
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

        if (ctrl && key == Key.S)
            _libraryManager?.SaveCanvas();
        else if (ctrl && key == Key.O)
            _libraryManager?.LoadLastSave();
    }

    private void OnMouseWheel(IMouse mouse, ScrollWheel wheel)
    {
        var screenPos = GetMousePosition(mouse, _window);

        bool ctrlPressed  = _keyboard?.IsKeyPressed(Key.ControlLeft)  == true ||
                            _keyboard?.IsKeyPressed(Key.ControlRight) == true;
        bool shiftPressed = _keyboard?.IsKeyPressed(Key.ShiftLeft)    == true ||
                            _keyboard?.IsKeyPressed(Key.ShiftRight)   == true;

        if (ctrlPressed)
        {
            var worldBefore = _strokeRenderer.ScreenToWorld(screenPos);
            float zoomFactor = 1.0f + wheel.Y * Settings.CameraZoomSpeed;
            _camera.Zoom = Math.Clamp(_camera.Zoom * zoomFactor, Settings.CameraMinZoom, Settings.CameraMaxZoom);
            _camera.Position = screenPos - worldBefore * _camera.Zoom;
        }
        else if (shiftPressed)
        {
            _camera.Position += new Vector2(wheel.Y * Settings.CameraPanSpeed, 0);
        }
        else
        {
            _camera.Position += new Vector2(0, wheel.Y * Settings.CameraPanSpeed);
        }

        _strokeRenderer.SetDirty();
    }

    private static Vector2 GetMousePosition(IMouse mouse, IWindow window)
    {
        var pos = mouse.Position;
        return new Vector2(pos.X, pos.Y);
    }

    public void Dispose()
    {
        if (_mouse != null)
        {
            _mouse.MouseDown -= OnMouseDown;
            _mouse.MouseUp   -= OnMouseUp;
            _mouse.MouseMove -= OnMouseMove;
            _mouse.Scroll    -= OnMouseWheel;
        }

        if (_keyboard != null)
            _keyboard.KeyDown -= OnKeyDown;
    }
}