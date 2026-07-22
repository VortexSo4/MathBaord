﻿using Silk.NET.Input;
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
    public event Action? OnActivity;
    public event Action? OnSceneChanged;

    private bool _isDrawing = false;
    private bool _isErasing = false;
    private Vector2 _lastPanPosition;
    private Vector2 _mouseDownPos;

    private bool _menuPending = false;
    private DateTime _mouseDownTime;

    public InputManager(IWindow window, StrokeRenderer strokeRenderer, Camera camera, Document document,
        RadialMenu radialMenu, LibraryManager libraryManager, LibraryPanel? libraryPanel)
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
            _keyboard.KeyChar += OnKeyChar;
        }
    }

    public void Update()
    {
        // Keep rendering while dialog is open (cursor blink)
        if (_libraryPanel?.IsDialogOpen == true)
        {
            _strokeRenderer.SetDirty();
        }

        if (_menuPending && !_radialMenu.IsOpen)
        {
            double elapsed = (DateTime.Now - _mouseDownTime).TotalSeconds;
            if (elapsed >= Settings.RadialMenuOpenThreshold)
            {
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
        OnActivity?.Invoke();
        var pos = GetMousePosition(mouse, _window);

        // Диалог имеет наивысший приоритет
        if (_libraryPanel?.IsDialogOpen == true)
        {
            if (button == MouseButton.Left)
                _libraryPanel.HandleMouseDown(pos);
            return;
        }

        // Панель библиотеки (включая drag&drop) перехватывает ЛКМ
        if (button == MouseButton.Left && _libraryPanel?.IsOpen == true && pos.X < _libraryPanel.Width)
        {
            if (_libraryPanel.HandleMouseDown(pos))
                return;
        }

        switch (button)
        {
            case MouseButton.Left:
            {
                _mouseDownPos = pos;
                _mouseDownTime = DateTime.Now;
                _menuPending = true;

                if (_radialMenu.IsOpen)
                {
                    _radialMenu.OnMouseDown(pos);
                }

                break;
            }
            case MouseButton.Right:
                _isErasing = true;
                _document.SaveState();
                _strokeRenderer.EraseAt(pos, saveState: false);
                break;
            case MouseButton.Middle:
                _lastPanPosition = pos;
                break;
        }
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        OnActivity?.Invoke();

        if (_libraryPanel?.IsDialogOpen == true)
            return;

        var pos = GetMousePosition(mouse, _window);

        // Drag&Drop в библиотеке — приоритет над рисованием
        if (_libraryPanel != null && (_libraryPanel.IsDragging || _libraryPanel.HasPendingDrag))
        {
            _libraryPanel.HandleMouseMove(pos);
            _strokeRenderer.SetDirty();
            return;
        }

        if (_isErasing)
        {
            _strokeRenderer.EraseAt(pos, saveState: false);
            _strokeRenderer.SetDirty();
            return;
        }

        if (_radialMenu.IsOpen)
        {
            _radialMenu.OnMouseMove(pos);
            _strokeRenderer.SetDirty();
            return;
        }

        if (_menuPending)
        {
            float elapsed = (float)(DateTime.Now - _mouseDownTime).TotalSeconds;
            float dist = Vector2.Distance(_mouseDownPos, pos);

            if (dist > Settings.RadialMenuEscapeDistance && elapsed < Settings.RadialMenuEscapeTime)
            {
                _menuPending = false;
                _strokeRenderer.BeginStroke(_mouseDownPos);
                _strokeRenderer.AddPoint(pos);
                _isDrawing = true;
                return;
            }
        }

        if (_isDrawing)
        {
            _strokeRenderer.AddPoint(pos);
        }
        else if (_mouse?.IsButtonPressed(MouseButton.Middle) == true)
        {
            var delta = pos - _lastPanPosition;
            _camera.Position += delta;
            _lastPanPosition = pos;
        }
    }

    private void OnMouseUp(IMouse mouse, MouseButton button)
    {
        OnActivity?.Invoke();

        if (_libraryPanel?.IsDialogOpen == true)
            return;

        var pos = GetMousePosition(mouse, _window);

        // Завершение Drag&Drop в библиотеке
        if (button == MouseButton.Left && _libraryPanel != null &&
            (_libraryPanel.IsDragging || _libraryPanel.HasPendingDrag))
        {
            _libraryPanel.HandleMouseUp(pos);
            _strokeRenderer.SetDirty();
            return;
        }

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
                _strokeRenderer.BeginStroke(_mouseDownPos);
                _strokeRenderer.AddPoint(pos);
                _isDrawing = true;
                _strokeRenderer.EndStroke();
                _isDrawing = false;
                _menuPending = false;
            }

            _menuPending = false;
        }
        else if (button == MouseButton.Right)
        {
            _isErasing = false;
        }
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        OnActivity?.Invoke();

        // Диалог перехватывает весь ввод с клавиатуры
        if (_libraryPanel?.IsDialogOpen == true)
        {
            bool ctrl = keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight);
            bool shift = keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight);

            if (key == Key.Enter)
            {
                _libraryPanel.ConfirmDialog();
                _strokeRenderer.SetDirty();
                return;
            }

            if (key == Key.Escape)
            {
                _libraryPanel.CancelDialog();
                _strokeRenderer.SetDirty();
                return;
            }

            var tb = _libraryPanel.TextBox;
            bool changed = false;

            // Управление курсором и редактирование (с поддержкой Ctrl и Shift)
            if (key == Key.Backspace)
            {
                tb.Backspace(ctrl);
                changed = true;
            }
            else if (key == Key.Delete)
            {
                tb.Delete(ctrl);
                changed = true;
            }
            else if (key == Key.Left)
            {
                tb.MoveCursor(-1, ctrl, shift);
                changed = true;
            }
            else if (key == Key.Right)
            {
                tb.MoveCursor(1, ctrl, shift);
                changed = true;
            }
            else if (key == Key.Home)
            {
                tb.MoveCursorToStart(shift);
                changed = true;
            }
            else if (key == Key.End)
            {
                tb.MoveCursorToEnd(shift);
                changed = true;
            }
            else if (ctrl && key == Key.A)
            {
                tb.SelectAll();
                changed = true;
            }

            if (changed)
            {
                _libraryPanel.RefreshDialogText();
                _strokeRenderer.SetDirty();
            }

            // Блокируем дальнейшую обработку клавиш (чтобы не рисовалось на холсте и не работал Ctrl+Z и т.д.)
            return;
        }

        // Обычный ввод (когда диалог закрыт)
        bool isCtrlPressed = keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight);
        bool isShiftPressed = keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight);

        if (isCtrlPressed && key == Key.Z)
        {
            if (isShiftPressed)
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

        if (key == Key.L && isCtrlPressed)
        {
            _libraryPanel?.Toggle();
            _strokeRenderer.SetDirty();
        }

        if (isCtrlPressed && key == Key.S)
        {
            _libraryManager?.SaveCanvas();
            _libraryPanel?.RefreshTree();
        }
        else if (isCtrlPressed && key == Key.O)
        {
            _libraryManager?.LoadLastSave();
        }
    }

    private void OnKeyChar(IKeyboard keyboard, char c)
    {
        if (_libraryPanel?.IsDialogOpen == true)
        {
            _libraryPanel.HandleCharInput(c);
            _strokeRenderer.SetDirty();
        }
    }

    private void OnMouseWheel(IMouse mouse, ScrollWheel wheel)
    {
        OnActivity?.Invoke();

        if (_libraryPanel?.IsDialogOpen == true)
            return;

        var screenPos = GetMousePosition(mouse, _window);

        bool ctrlPressed = _keyboard?.IsKeyPressed(Key.ControlLeft) == true ||
                           _keyboard?.IsKeyPressed(Key.ControlRight) == true;
        bool shiftPressed = _keyboard?.IsKeyPressed(Key.ShiftLeft) == true ||
                            _keyboard?.IsKeyPressed(Key.ShiftRight) == true;

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
            _mouse.MouseUp -= OnMouseUp;
            _mouse.MouseMove -= OnMouseMove;
            _mouse.Scroll -= OnMouseWheel;
        }

        if (_keyboard != null)
        {
            _keyboard.KeyDown -= OnKeyDown;
            _keyboard.KeyChar -= OnKeyChar;
        }
    }
}