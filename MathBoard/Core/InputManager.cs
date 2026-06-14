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
    private readonly LibraryPanel? _libraryPanel;
    
    private IMouse? _mouse;
    private IKeyboard? _keyboard;
    private readonly IWindow _window;

    private bool _isDrawing = false;
    private Vector2 _lastPanPosition;
    private Vector2 _mouseDownPos;

    // Новое поведение: меню открывается после долгого нажатия (по таймеру)
    private bool _menuPending = false;
    private DateTime _mouseDownTime;

    public InputManager(IWindow window, StrokeRenderer strokeRenderer, Camera camera, Document document, RadialMenu radialMenu, LibraryManager libraryManager, LibraryPanel? libraryPanel)
    {
        _strokeRenderer = strokeRenderer;
        _camera = camera;
        _document = document;
        _window = window;
        _input = window.CreateInput();
        _radialMenu = radialMenu;
        _libraryManager = libraryManager;
        _libraryPanel = libraryPanel;

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

    // Вызывается каждый кадр из VulkanRenderer.Render
    public void Update()
    {
        if (_menuPending && !_radialMenu.IsOpen)
        {
            double elapsed = (DateTime.Now - _mouseDownTime).TotalSeconds;
            if (elapsed >= Settings.RadialMenuOpenThreshold)
            {
                // Открываем меню по таймеру (мышь не двигалась или двигалась слишком мало)
                var currentPos = GetMousePosition(_mouse!, _window);
                _radialMenu.OpenAt(_mouseDownPos);
                _radialMenu.OnMouseDown(currentPos);
                _menuPending = false;
                _strokeRenderer.SetDirty();
            }
        }
    }

    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        var pos = GetMousePosition(mouse, _window);
        
        if (_libraryPanel?.IsOpen == true && pos.X < _libraryPanel.Width)
        {
            if (_libraryPanel.HandleClick(pos))
                return;
        }

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

        // Проверяем, не нужно ли отменить открытие меню из-за быстрого движения
        if (_menuPending)
        {
            float elapsed = (float)(DateTime.Now - _mouseDownTime).TotalSeconds;
            float dist = Vector2.Distance(_mouseDownPos, pos);

            // Отмена меню при быстром движении (ещё не открыто)
            if (dist > Settings.RadialMenuEscapeDistance && elapsed < Settings.RadialMenuEscapeTime)
            {
                _menuPending = false;
                _strokeRenderer.BeginStroke(_mouseDownPos);
                _strokeRenderer.AddPoint(pos);
                _isDrawing = true;
                return;
            }

            // Если время уже превысило порог, но меню ещё не открыто – доверимся таймеру в Update()
            // Ничего не делаем, ждём следующий кадр.
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
            else if (_menuPending && !_radialMenu.IsOpen)
            {
                // Не дождались открытия меню – начинаем рисование
                _strokeRenderer.BeginStroke(_mouseDownPos);
                _strokeRenderer.AddPoint(pos);
                _isDrawing = true;
                _strokeRenderer.EndStroke();
                _isDrawing = false;
                _menuPending = false;
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
        
        if (key == Key.L && ctrl) // Ctrl+L — открыть/закрыть библиотеку
        {
            _libraryPanel?.Toggle();
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

        if (_libraryPanel?.IsOpen == true && screenPos.X < _libraryPanel.Width)
        {
            _libraryPanel.HandleScroll(wheel.Y);
            _strokeRenderer.SetDirty();
            return;
        }
        
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