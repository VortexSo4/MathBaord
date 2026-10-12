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
    private readonly ColorEditor _colorEditor;
    private readonly ShapePicker _shapePicker;
    private readonly PlotEditor _plotEditor;
    private readonly IPopup[] _popups;
    private readonly TouchGestures? _gestures;

    private readonly nint _hwnd;
    private readonly IMouse? _mouse;
    private readonly IKeyboard? _keyboard;

    /// <summary>Who receives pointer events until the button that started the gesture is released.</summary>
    private enum Capture { None, Panel, QuickBar, Radial, Popup, Canvas, Erase, Pan }

    private Capture _capture = Capture.None;
    private MouseButton _captureButton;

    private bool _menuPending;
    private bool _isDrawing;
    private bool _isPanning;
    private bool _isShaping;
    private IPopup? _capturedPopup;
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
    private bool _textWasActive;
    private bool _keyboardAutoShown;

    public QuickBar QuickBar => _quickBar;

    public InputManager(IWindow window, StrokeRenderer strokeRenderer, Camera camera, Document document,
        RadialMenu radialMenu, LibraryManager libraryManager, LibraryPanel libraryPanel, Updater updater)
    {
        _strokeRenderer = strokeRenderer;
        _camera = camera;
        _document = document;
        _radialMenu = radialMenu;
        _libraryManager = libraryManager;
        _libraryPanel = libraryPanel;
        _colorEditor = new ColorEditor(strokeRenderer);
        _shapePicker = new ShapePicker(strokeRenderer);
        _plotEditor = new PlotEditor(strokeRenderer);
        _popups = [_colorEditor, _shapePicker, _plotEditor, new UpdatePopup(strokeRenderer, updater)];
        foreach (var popup in _popups) strokeRenderer.AddOverlay(popup);
        // The screenshot overlay is drawn by the renderer itself because it hides the rest of the interface.
        _popups = [.._popups, strokeRenderer.Screenshot];
        strokeRenderer.EditPlotRequested += plot => _plotEditor.Open(plot, edited => _strokeRenderer.ReplaceObject(plot, edited));
        _quickBar = new QuickBar(strokeRenderer, Execute, IsActive, pos => _libraryPanel.IsOpen && pos.X < _libraryPanel.Width,
            (index, center) => _colorEditor.Open(index, center));
        _libraryPanel.SettingsPanel.OnCustomizeQuickButtons = _quickBar.BeginEditing;

        (_repeatDelay, _repeatInterval) = ReadKeyboardRepeat();

        if (OperatingSystem.IsWindows() && window.Native?.Win32 is { } win32)
        {
            _hwnd = win32.Hwnd;
            _gestures = new TouchGestures(win32.Hwnd);
            _gestures.Started += OnGestureStarted;
            _gestures.Changed += OnGestureChanged;
        }

        window.FileDrop += OnFileDrop;
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
        ActiveTextBox != null || _repeatKey != null;

    /// <summary>The text field that receives keyboard input: a popup's field first, then the library panel's.</summary>
    private TextBoxState? ActiveTextBox => _plotEditor.ActiveTextBox ?? _libraryPanel.ActiveTextBox;

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

        var textBox = ActiveTextBox;
        UpdateTouchKeyboard(textBox != null);
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

    /// <summary>Shows the touch keyboard while a text field is active if the setting asks for it.</summary>
    private void UpdateTouchKeyboard(bool textActive)
    {
        if (textActive == _textWasActive) return;
        _textWasActive = textActive;

        if (textActive)
        {
            bool show = Settings.OnScreenKeyboard.Value switch
            {
                "Always" => true,
                "Never" => false,
                _ => !TouchKeyboard.IsHardwareKeyboardPresent()
            };
            if (show && !TouchKeyboard.IsVisible)
            {
                TouchKeyboard.Show();
                _keyboardAutoShown = true;
            }
        }
        else if (_keyboardAutoShown)
        {
            TouchKeyboard.Hide();
            _keyboardAutoShown = false;
        }
    }

    private void OnGestureStarted()
    {
        // The first finger already started something through mouse emulation; a pinch replaces it.
        if (_capture == Capture.Canvas)
        {
            if (_isShaping) _strokeRenderer.CancelShape();
            else if (_isDrawing && _strokeRenderer.Tool == Tool.Brush) _strokeRenderer.CancelStroke();
            else if (_isDrawing) _strokeRenderer.EndStroke();
            if (_strokeRenderer.CurrentSelectionState != StrokeRenderer.SelectionState.None)
                _strokeRenderer.HandleSelectionUp(_mouseDownPos);
            _capture = Capture.None;
        }

        _menuPending = false;
        _isDrawing = false;
        _isPanning = false;
        _isShaping = false;
    }

    private void OnGestureChanged(Vector2 center, Vector2 delta, float zoom)
    {
        if (_capture != Capture.None || _libraryPanel.IsModalOpen) return;
        PanBy(ToLogical(delta));
        _strokeRenderer.ZoomAt(ToLogical(center), zoom);
    }

    private Vector2 MousePosition => ToLogical(_mouse?.Position ?? Vector2.Zero);

    /// <summary>Converts window pixels to the logical pixels the whole UI works in.</summary>
    private Vector2 ToLogical(Vector2 windowPos) => windowPos / _strokeRenderer.UiScale;
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
                if (!_libraryManager.SaveCurrent()) _libraryPanel.OpenSaveAsDialog();
                _libraryPanel.RefreshTree();
                break;
            case UiAction.NewFile:
                _libraryPanel.RequestNewFile();
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
            case UiAction.Keyboard:
                TouchKeyboard.Toggle();
                break;
            case UiAction.Shapes:
                _shapePicker.Open(MousePosition);
                break;
            case UiAction.Grid:
                Settings.GridMode.Value = Settings.GridMode.Value switch { "None" => "Squares", "Squares" => "Axes", _ => "None" };
                Settings.Save();
                break;
            case UiAction.Screenshot when _hwnd != 0:
                _strokeRenderer.CancelStroke();
                if (ImageSources.CaptureScreen(_hwnd) is { } capture) _strokeRenderer.Screenshot.Open(capture);
                break;
            case UiAction.InsertImage when _hwnd != 0:
                if (ImageSources.PickFile(_hwnd, Localization.Get("action_InsertImage", "Insert picture")) is { } path)
                    InsertImageFile(path, CanvasCenter);
                break;
        }

        _strokeRenderer.SetDirty();
    }

    private void InsertImageFile(string path, Vector2 screenPos)
    {
        using var bitmap = ImageStore.Load(path);
        if (bitmap != null) _strokeRenderer.InsertImage(bitmap, screenPos);
    }

    private void OnFileDrop(string[] paths)
    {
        var pos = MousePosition;
        foreach (string path in paths)
        {
            InsertImageFile(path, pos);
            pos += new Vector2(24f);
        }

        _strokeRenderer.SetDirty();
    }

    private void PasteImage()
    {
        if (_hwnd == 0) return;
        using var bitmap = ImageSources.ReadClipboard();
        if (bitmap != null) _strokeRenderer.InsertImage(bitmap, CanvasCenter);
    }

    private bool IsActive(UiAction action) => action switch
    {
        UiAction.ToggleLibrary => _libraryPanel.IsOpen,
        UiAction.Brush => _strokeRenderer.Tool == Tool.Brush,
        UiAction.Eraser => _strokeRenderer.Tool == Tool.Eraser,
        UiAction.Select => _strokeRenderer.Tool == Tool.Select,
        UiAction.Pan => _strokeRenderer.Tool == Tool.Pan,
        UiAction.Shapes => _strokeRenderer.Tool == Tool.Shape,
        UiAction.Grid => Settings.GridMode.Value != "None",
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
        var pos = ToLogical(mouse.Position);
        _strokeRenderer.SetDirty();
        if (_capture != Capture.None || _gestures?.IsActive == true) return;

        if (_popups.LastOrDefault(p => p.IsOpen) is { } popup)
        {
            if (button != MouseButton.Left) return;
            _capturedPopup = popup;
            Begin(Capture.Popup, button, () => popup.OnMouseDown(pos));
            return;
        }

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

        if (_strokeRenderer.Tool == Tool.Shape && _strokeRenderer.IsAnglePending)
        {
            // The second arm of an angle starts right away; the menu is still reachable with an empty canvas press.
            _menuPending = false;
            _isShaping = true;
            _strokeRenderer.BeginShape(pos);
        }
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        var pos = ToLogical(position);
        if (_gestures?.IsActive == true) return;

        switch (_capture)
        {
            case Capture.Popup:
                _capturedPopup?.OnMouseMove(pos);
                return;
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

        foreach (var popup in _popups)
            if (popup.IsOpen) popup.OnMouseMove(pos);
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
                case Tool.Shape:
                    _strokeRenderer.BeginShape(_mouseDownPos);
                    _isShaping = true;
                    break;
            }
        }

        _lastMovePos = pos;
        _lastMoveTime = DateTime.Now;
        _strokeRenderer.IsQuickShapeApplied = false;

        if (_isDrawing) _strokeRenderer.AddPoint(pos);
        else if (_isShaping) _strokeRenderer.UpdateShape(pos);
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
        var pos = ToLogical(mouse.Position);
        _strokeRenderer.SetDirty();
        if (_capture == Capture.None || button != _captureButton) return;

        var capture = _capture;
        _capture = Capture.None;

        switch (capture)
        {
            case Capture.Popup:
                _capturedPopup?.OnMouseUp(pos);
                _capturedPopup = null;
                break;
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
            else if (tool == Tool.Shape)
            {
                _strokeRenderer.TapShape(pos);
            }
        }
        else if (_isShaping)
        {
            _strokeRenderer.EndShape(pos);
        }
        else if (_isDrawing)
        {
            _strokeRenderer.EndStroke();
        }

        if (tool == Tool.Select) _strokeRenderer.HandleSelectionUp(pos);
        _isDrawing = false;
        _isPanning = false;
        _isShaping = false;
    }

    private void OnMouseWheel(IMouse mouse, ScrollWheel wheel)
    {
        var pos = ToLogical(mouse.Position);
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
            else if (_libraryPanel.ActiveTextBox is { } box) HandleTextKey(box, key, ctrl, shift, keyboard);
            return;
        }

        if (_quickBar.IsEditing)
        {
            if (key is Key.Escape or Key.Enter) _quickBar.FinishEditing();
            return;
        }

        if (_plotEditor.IsOpen)
        {
            if (key is Key.Enter or Key.KeypadEnter) _plotEditor.Confirm();
            else if (key == Key.Escape) _plotEditor.Close();
            else if (key == Key.Tab) _plotEditor.NextField();
            else HandleTextKey(_plotEditor.ActiveTextBox!, key, ctrl, shift, keyboard);
            return;
        }

        if (_popups.LastOrDefault(p => p.IsOpen) is { } popup)
        {
            if (key is Key.Escape or Key.Enter) popup.Close();
            return;
        }

        if (key == Key.Escape)
        {
            if (_radialMenu.IsOpen) _radialMenu.Close();
            else if (_strokeRenderer.IsAnglePending) _strokeRenderer.CancelShape();
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
                case Key.S when shift: _libraryPanel.OpenSaveAsDialog(); break;
                case Key.S: Execute(UiAction.Save); break;
                case Key.O: Execute(UiAction.OpenAutoSave); break;
                case Key.N: Execute(UiAction.NewFile); break;
                case Key.L: Execute(UiAction.ToggleLibrary); break;
                case Key.V: PasteImage(); break;
            }
        }
        else if (key is Key.Delete or Key.Backspace && (_strokeRenderer.SelectedStrokes.Count > 0 || _strokeRenderer.SelectedObjects.Count > 0))
        {
            _strokeRenderer.DeleteSelection();
        }
        else if (key == Key.E)
        {
            Execute(_strokeRenderer.Tool == Tool.Eraser ? UiAction.Brush : UiAction.Eraser);
        }
    }

    private void HandleTextKey(TextBoxState box, Key key, bool ctrl, bool shift, IKeyboard keyboard)
    {
        if (box.HandleKey(key, ctrl, shift, keyboard) && key is Key.Left or Key.Right or Key.Backspace or Key.Delete)
        {
            _repeatKey = key;
            _repeatAt = DateTime.Now + _repeatDelay;
        }
    }

    private void OnKeyUp(IKeyboard keyboard, Key key, int scancode)
    {
        if (key == _repeatKey) _repeatKey = null;
    }

    private void OnKeyChar(IKeyboard keyboard, char c)
    {
        if (_plotEditor.ActiveTextBox is { } box)
        {
            box.Insert(c);
            _strokeRenderer.SetDirty();
            return;
        }

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
        _gestures?.Dispose();
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
