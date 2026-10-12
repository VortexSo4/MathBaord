using System.Numerics;
using System.Runtime.InteropServices;
using MathBoard.Rendering;
using Silk.NET.Input;
using Silk.NET.Windowing;

namespace MathBoard.Core;

public sealed partial class InputManager : IDisposable
{
    // Moving farther than this always means "draw", even after the radial menu escape time.
    private const float MenuCancelDistance = 12f;
    private const double QuickShapeHoldSeconds = 1.0;

    private readonly IInputContext _input;
    private readonly StrokeRenderer _strokeRenderer;
    private readonly Camera _camera;
    private readonly Document _document;
    private readonly RadialMenu _radialMenu;
    private readonly LibraryManager _libraryManager;
    private readonly LibraryPanel _libraryPanel;
    private readonly QuickBar _quickBar;

    private readonly IMouse? _mouse;
    private readonly IKeyboard? _keyboard;

    /// <summary>Who receives pointer events until the button that started the gesture is released.</summary>
    private enum Capture { None, Panel, QuickBar, Radial, Canvas, Erase, Pan }

    private Capture _capture = Capture.None;
    private MouseButton _captureButton;

    private bool _menuPending;
    private bool _isDrawing;
    private bool _isPanning;
    private Vector2 _mouseDownPos;
    private DateTime _mouseDownTime;
    private Vector2 _lastPanPosition;
    private Vector2 _lastMovePos;
    private DateTime _lastMoveTime = DateTime.Now;

    private Key? _repeatKey;
    private DateTime _repeatAt;
    private readonly TimeSpan _repeatDelay;
    private readonly TimeSpan _repeatInterval;
    private int _caretPhase;

    public QuickBar QuickBar => _quickBar;

    public InputManager(IWindow window, StrokeRenderer strokeRenderer, Camera camera, Document document,
        RadialMenu radialMenu, LibraryManager libraryManager, LibraryPanel libraryPanel)
    {
        _strokeRenderer = strokeRenderer;
        _camera = camera;
        _document = document;
        _radialMenu = radialMenu;
        _libraryManager = libraryManager;
        _libraryPanel = libraryPanel;
        _quickBar = new QuickBar(strokeRenderer, Execute, IsActive, pos => _libraryPanel.IsOpen && pos.X < _libraryPanel.Width);
        _libraryPanel.SettingsPanel.OnCustomizeQuickButtons = _quickBar.BeginEditing;

        (_repeatDelay, _repeatInterval) = ReadKeyboardRepeat();

        _input = window.CreateInput();
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
            _keyboard.KeyUp += OnKeyUp;
            _keyboard.KeyChar += OnKeyChar;
        }
    }

    /// <summary>True while timers are running that need the loop to tick at frame rate.</summary>
    public bool NeedsFrequentUpdates =>
        _menuPending || _isDrawing || _quickBar.IsPressed || _libraryPanel.NeedsUpdates ||
        _libraryPanel.ActiveTextBox != null || _repeatKey != null;

    public void Update(float dt)
    {
        var now = DateTime.Now;

        if (_menuPending && !_radialMenu.IsOpen && (now - _mouseDownTime).TotalSeconds >= Settings.RadialMenuOpenThreshold)
        {
            _menuPending = false;
            if (_strokeRenderer.CurrentSelectionState == StrokeRenderer.SelectionState.DrawingBox)
                _strokeRenderer.CurrentSelectionState = StrokeRenderer.SelectionState.None;
            _radialMenu.OpenAt(_mouseDownPos);
            _radialMenu.OnMouseDown(MousePosition);
            _capture = Capture.Radial;
        }

        if (_isDrawing && _strokeRenderer.Tool == Tool.Brush && !_strokeRenderer.IsQuickShapeApplied &&
            (now - _lastMoveTime).TotalSeconds > QuickShapeHoldSeconds)
        {
            _strokeRenderer.ApplyQuickShape();
            _strokeRenderer.IsQuickShapeApplied = true;
        }

        _quickBar.Update();
        _libraryPanel.Update(dt);

        var textBox = _libraryPanel.ActiveTextBox;
        if (textBox != null)
        {
            if (_repeatKey is { } key && now >= _repeatAt && _keyboard != null)
            {
                textBox.HandleKey(key, IsCtrl, IsShift, _keyboard);
                _repeatAt = now + _repeatInterval;
                _strokeRenderer.SetDirty();
            }

            int phase = (int)((now - textBox.LastEditTime).TotalMilliseconds / 500);
            if (phase != _caretPhase)
            {
                _caretPhase = phase;
                _strokeRenderer.SetDirty();
            }
        }
        else
        {
            _repeatKey = null;
        }
    }

    private Vector2 MousePosition => _mouse?.Position ?? Vector2.Zero;
    private bool IsCtrl => _keyboard?.IsKeyPressed(Key.ControlLeft) == true || _keyboard?.IsKeyPressed(Key.ControlRight) == true;
    private bool IsShift => _keyboard?.IsKeyPressed(Key.ShiftLeft) == true || _keyboard?.IsKeyPressed(Key.ShiftRight) == true;

    // ------------------------------------------------------------- actions

    public void Execute(UiAction action)
    {
        switch (action)
        {
            case UiAction.ToggleLibrary:
                _libraryPanel.Toggle();
                break;
            case UiAction.Undo:
                _strokeRenderer.Undo();
                break;
            case UiAction.Redo:
                _strokeRenderer.Redo();
                break;
            case UiAction.Save:
                if (!_libraryManager.SaveCurrent()) _libraryPanel.OpenSaveDialog();
                _libraryPanel.RefreshTree();
                break;
            case UiAction.NewFile:
                if (_document.IsDirty) _libraryPanel.OpenNewFileDialog();
                else _libraryManager.NewFile();
                break;
            case UiAction.OpenAutoSave:
                _libraryManager.LoadLastSave();
                break;
            case UiAction.Settings:
                _libraryPanel.OpenSettingsDialog();
                break;
            case UiAction.Brush:
                _strokeRenderer.SetTool(Tool.Brush);
                break;
            case UiAction.Eraser:
                _strokeRenderer.SetTool(Tool.Eraser);
                break;
            case UiAction.Select:
                _strokeRenderer.SetTool(Tool.Select);
                break;
            case UiAction.Pan:
                _strokeRenderer.SetTool(Tool.Pan);
                break;
            case UiAction.Color1 or UiAction.Color2 or UiAction.Color3:
                _strokeRenderer.SetColor(Settings.Colors[action - UiAction.Color1]);
                if (_strokeRenderer.Tool is Tool.Eraser or Tool.Pan) _strokeRenderer.SetTool(Tool.Brush);
                break;
            case UiAction.ZoomIn:
                _strokeRenderer.ZoomAt(CanvasCenter, 1.25f);
                break;
            case UiAction.ZoomOut:
                _strokeRenderer.ZoomAt(CanvasCenter, 0.8f);
                break;
            case UiAction.FitView:
                _strokeRenderer.FitToContent();
                break;
        }

        _strokeRenderer.SetDirty();
    }

    private bool IsActive(UiAction action) => action switch
    {
        UiAction.ToggleLibrary => _libraryPanel.IsOpen,
        UiAction.Brush => _strokeRenderer.Tool == Tool.Brush,
        UiAction.Eraser => _strokeRenderer.Tool == Tool.Eraser,
        UiAction.Select => _strokeRenderer.Tool == Tool.Select,
        UiAction.Pan => _strokeRenderer.Tool == Tool.Pan,
        UiAction.Color1 or UiAction.Color2 or UiAction.Color3 =>
            Vector4.DistanceSquared(_strokeRenderer.CurrentColor, Settings.Colors[action - UiAction.Color1]) < 1e-4f,
        _ => false
    };

    private Vector2 CanvasCenter
    {
        get
        {
            var screen = _strokeRenderer.ScreenSize;
            float left = _libraryPanel.IsOpen ? _libraryPanel.Width : 0f;
            return new Vector2((left + screen.X) * 0.5f, screen.Y * 0.5f);
        }
    }

    // ------------------------------------------------------------- mouse

    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        var pos = mouse.Position;
        _strokeRenderer.SetDirty();
        if (_capture != Capture.None) return;

        if (_libraryPanel.IsModalOpen)
        {
            if (button == MouseButton.Left) Begin(Capture.Panel, button, () => _libraryPanel.HandleMouseDown(pos));
            return;
        }

        if (button == MouseButton.Left)
        {
            if (_radialMenu.IsOpen)
                Begin(Capture.Radial, button, () => _radialMenu.OnMouseDown(pos));
            else if (_quickBar.Contains(pos))
                Begin(Capture.QuickBar, button, () => _quickBar.OnMouseDown(pos));
            else if (_libraryPanel.Contains(pos))
                Begin(Capture.Panel, button, () => _libraryPanel.HandleMouseDown(pos));
            else
                Begin(Capture.Canvas, button, () => BeginCanvasPress(pos));
            return;
        }

        if (_radialMenu.IsOpen || _quickBar.IsEditing || _libraryPanel.Contains(pos)) return;

        if (button == MouseButton.Right)
        {
            Begin(Capture.Erase, button, () =>
            {
                _strokeRenderer.BeginErase();
                _strokeRenderer.EraseAt(pos);
            });
        }
        else if (button == MouseButton.Middle)
        {
            Begin(Capture.Pan, button, () => _lastPanPosition = pos);
        }
    }

    private void Begin(Capture capture, MouseButton button, Action start)
    {
        _capture = capture;
        _captureButton = button;
        start();
    }

    private void BeginCanvasPress(Vector2 pos)
    {
        _mouseDownPos = pos;
        _mouseDownTime = DateTime.Now;
        _menuPending = true;
        _isPanning = false;
        if (_strokeRenderer.Tool == Tool.Select)
            _strokeRenderer.HandleSelectionDown(pos);
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        var pos = mouse.Position;

        switch (_capture)
        {
            case Capture.Panel:
                _libraryPanel.HandleMouseMove(pos);
                return;
            case Capture.QuickBar:
                _quickBar.OnMouseMove(pos);
                return;
            case Capture.Radial:
                _radialMenu.OnMouseMove(pos);
                _strokeRenderer.SetDirty();
                return;
            case Capture.Erase:
                _strokeRenderer.EraseAt(pos);
                UpdateHover(pos);
                return;
            case Capture.Pan:
                PanBy(pos - _lastPanPosition);
                _lastPanPosition = pos;
                return;
            case Capture.Canvas:
                MoveCanvasPress(pos);
                return;
        }

        if (_libraryPanel.IsModalOpen || (_libraryPanel.IsOpen && pos.X < _libraryPanel.Width))
            _libraryPanel.HandleMouseMove(pos);
        if (_radialMenu.IsOpen)
        {
            _radialMenu.OnMouseMove(pos);
            _strokeRenderer.SetDirty();
        }

        _quickBar.UpdateHover(pos);
        UpdateHover(pos);
    }

    private void UpdateHover(Vector2 pos)
    {
        bool overCanvas = !_libraryPanel.Contains(pos) && !_quickBar.Contains(pos);
        _strokeRenderer.HoverPosition = overCanvas ? pos : null;
        if (_strokeRenderer.Tool == Tool.Eraser) _strokeRenderer.SetDirty();
    }

    private void MoveCanvasPress(Vector2 pos)
    {
        if (_menuPending)
        {
            float elapsed = (float)(DateTime.Now - _mouseDownTime).TotalSeconds;
            float dist = Vector2.Distance(_mouseDownPos, pos);
            bool escaped = (dist > Settings.RadialMenuEscapeDistance && elapsed < Settings.RadialMenuEscapeTime) ||
                           dist > MenuCancelDistance;
            if (!escaped) return;

            _menuPending = false;
            switch (_strokeRenderer.Tool)
            {
                case Tool.Brush:
                case Tool.Eraser:
                    _strokeRenderer.BeginStroke(_mouseDownPos);
                    _isDrawing = true;
                    break;
                case Tool.Pan:
                    _isPanning = true;
                    _lastPanPosition = _mouseDownPos;
                    break;
            }
        }

        _lastMovePos = pos;
        _lastMoveTime = DateTime.Now;
        _strokeRenderer.IsQuickShapeApplied = false;

        if (_isDrawing) _strokeRenderer.AddPoint(pos);
        else if (_isPanning)
        {
            PanBy(pos - _lastPanPosition);
            _lastPanPosition = pos;
        }
        else if (_strokeRenderer.Tool == Tool.Select) _strokeRenderer.HandleSelectionMove(pos);

        UpdateHover(pos);
    }

    private void PanBy(Vector2 delta)
    {
        _camera.Position += delta;
        _strokeRenderer.SetDirty();
    }

    private void OnMouseUp(IMouse mouse, MouseButton button)
    {
        var pos = mouse.Position;
        _strokeRenderer.SetDirty();
        if (_capture == Capture.None || button != _captureButton) return;

        var capture = _capture;
        _capture = Capture.None;

        switch (capture)
        {
            case Capture.Panel:
                _libraryPanel.HandleMouseUp(pos);
                break;
            case Capture.QuickBar:
                _quickBar.OnMouseUp(pos);
                break;
            case Capture.Radial:
                _radialMenu.OnMouseUp(pos);
                break;
            case Capture.Canvas:
                EndCanvasPress(pos);
                break;
        }
    }

    private void EndCanvasPress(Vector2 pos)
    {
        var tool = _strokeRenderer.Tool;
        if (_menuPending)
        {
            _menuPending = false;
            if (tool is Tool.Brush or Tool.Eraser)
            {
                // A tap leaves a dot (or erases under the pen).
                _strokeRenderer.BeginStroke(_mouseDownPos);
                _strokeRenderer.AddPoint(pos);
                _strokeRenderer.EndStroke();
            }
        }
        else if (_isDrawing)
        {
            _strokeRenderer.EndStroke();
        }

        if (tool == Tool.Select) _strokeRenderer.HandleSelectionUp(pos);
        _isDrawing = false;
        _isPanning = false;
    }

    private void OnMouseWheel(IMouse mouse, ScrollWheel wheel)
    {
        var pos = mouse.Position;
        if (_capture is Capture.Canvas or Capture.Radial) return;

        if (_libraryPanel.Contains(pos))
        {
            _libraryPanel.HandleScroll(wheel.Y);
            return;
        }

        if (IsCtrl) _strokeRenderer.ZoomAt(pos, 1.0f + wheel.Y * Settings.CameraZoomSpeed);
        else if (IsShift) PanBy(new Vector2(wheel.Y * Settings.CameraPanSpeed, 0));
        else PanBy(new Vector2(0, wheel.Y * Settings.CameraPanSpeed));
    }

    // ------------------------------------------------------------- keyboard

    private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        _strokeRenderer.SetDirty();
        bool ctrl = IsCtrl, shift = IsShift;

        if (_libraryPanel.IsModalOpen)
        {
            if (key is Key.Enter or Key.KeypadEnter) _libraryPanel.ConfirmDialog();
            else if (key == Key.Escape) _libraryPanel.CancelDialog();
            else if (_libraryPanel.ActiveTextBox?.HandleKey(key, ctrl, shift, keyboard) == true &&
                     key is Key.Left or Key.Right or Key.Backspace or Key.Delete)
            {
                _repeatKey = key;
                _repeatAt = DateTime.Now + _repeatDelay;
            }

            return;
        }

        if (_quickBar.IsEditing)
        {
            if (key is Key.Escape or Key.Enter) _quickBar.FinishEditing();
            return;
        }

        if (key == Key.Escape)
        {
            if (_radialMenu.IsOpen) _radialMenu.Close();
            else _strokeRenderer.ClearSelection();
            return;
        }

        if (_capture != Capture.None) return;

        if (ctrl)
        {
            switch (key)
            {
                case Key.Z: Execute(shift ? UiAction.Redo : UiAction.Undo); break;
                case Key.Y: Execute(UiAction.Redo); break;
                case Key.S when shift: _libraryPanel.OpenSaveDialog(); break;
                case Key.S: Execute(UiAction.Save); break;
                case Key.O: Execute(UiAction.OpenAutoSave); break;
                case Key.N: Execute(UiAction.NewFile); break;
                case Key.L: Execute(UiAction.ToggleLibrary); break;
            }
        }
        else if (key == Key.E)
        {
            Execute(_strokeRenderer.Tool == Tool.Eraser ? UiAction.Brush : UiAction.Eraser);
        }
    }

    private void OnKeyUp(IKeyboard keyboard, Key key, int scancode)
    {
        if (key == _repeatKey) _repeatKey = null;
    }

    private void OnKeyChar(IKeyboard keyboard, char c)
    {
        if (_libraryPanel.ActiveTextBox == null) return;
        _libraryPanel.HandleCharInput(c);
    }

    /// <summary>Uses the user's Windows keyboard repeat delay and rate for held editing keys.</summary>
    private static (TimeSpan Delay, TimeSpan Interval) ReadKeyboardRepeat()
    {
        const uint SPI_GETKEYBOARDSPEED = 0x000A;
        const uint SPI_GETKEYBOARDDELAY = 0x0016;
        int delay = 1, speed = 31;
        if (OperatingSystem.IsWindows())
        {
            SystemParametersInfo(SPI_GETKEYBOARDDELAY, 0, ref delay, 0);
            SystemParametersInfo(SPI_GETKEYBOARDSPEED, 0, ref speed, 0);
        }

        // Delay 0..3 maps to 250..1000 ms, speed 0..31 to roughly 2.5..30 repeats per second.
        double rate = 2.5 + Math.Clamp(speed, 0, 31) * (27.5 / 31.0);
        return (TimeSpan.FromMilliseconds(250 * (Math.Clamp(delay, 0, 3) + 1)), TimeSpan.FromSeconds(1.0 / rate));
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);

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
            _keyboard.KeyUp -= OnKeyUp;
            _keyboard.KeyChar -= OnKeyChar;
        }
    }
}
