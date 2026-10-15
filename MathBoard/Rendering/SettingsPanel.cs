using System.Globalization;
using System.Numerics;
using MathBoard.Core;

namespace MathBoard.Rendering;

public class SettingsPanel : IDisposable
{
    public bool IsOpen { get; private set; }

    private readonly StrokeRenderer _renderer;
    private Vector2 _screenSize = new(1280, 720);
    private Vector2 _mousePos = new(-1000);
    private float _scrollY;

    private const float TitleBarHeight = 56f;
    private const float BottomBarHeight = 64f;
    private const float ItemHeight = 48f;
    private const float ControlWidth = 190f;
    private const float ControlHeight = 34f;
    private const float Padding = 20f;
    private const float ScrollbarWidth = 6f;
    private const float DropdownOptionHeight = 34f;
    private const float DragThreshold = 6f;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly Dictionary<string, string> LanguageNames = new() { ["EN_US"] = "English", ["RU_RU"] = "Русский" };
    private static readonly string[] ScaleOptions = ["0", "1", "1.25", "1.5", "1.75", "2"];

    private enum SettingType { Numeric, IntNumeric, Enum, Bool, Action, Section }

    private class SettingItem
    {
        public string Name = "";
        public SettingType Type;
        public Func<string> GetValue = () => "";
        public Action<string>? SetValue;
        public Action? OnClick;
        public List<string>? Options;
        public Func<string, string> Label = v => v;
        public string Section = "";
    }

    // Every setting with its section header; the visible rows are the headers and the settings of the open sections.
    private readonly List<SettingItem> _all = new();
    private readonly List<SettingItem> _items = new();
    private readonly HashSet<string> _expanded = ["general"];
    private string _section = "";

    public TextBoxState TextBox { get; } = new() { Accepts = c => char.IsDigit(c) || c is '.' or '-' };
    public bool HasTextInput => _activeFieldIndex >= 0;

    private int _activeFieldIndex = -1;
    private int _openDropdownIndex = -1;

    private int _pressedRow = -1;
    private Vector2 _pressPos;
    private float _pressScroll;
    private bool _scrollingByDrag;
    private bool _isDraggingScrollbar;

    public Action? OnOpenLog { get; set; }
    public Action? OnCustomizeQuickButtons { get; set; }
    public Action? OnCustomizeRadialMenu { get; set; }

    private readonly Updater _updater;

    public SettingsPanel(StrokeRenderer renderer, Updater updater)
    {
        _renderer = renderer;
        _updater = updater;
    }

    public void Open()
    {
        IsOpen = true;
        _scrollY = 0;
        _activeFieldIndex = -1;
        _openDropdownIndex = -1;
        BuildSettingsList();
        _renderer.SetDirty();
    }

    public void Close()
    {
        ConfirmEdit();
        Cancel();
    }

    public void Cancel()
    {
        IsOpen = false;
        _activeFieldIndex = -1;
        _openDropdownIndex = -1;
        _pressedRow = -1;
        _isDraggingScrollbar = false;
        _renderer.SetDirty();
    }

    private static string Format(float v, string format) => v.ToString(format, Invariant);

    private SettingItem Numeric(string key, string fallback, Func<float> get, Action<float> set, string format = "0.##") =>
        new()
        {
            Name = Localization.Get(key, fallback),
            Type = SettingType.Numeric,
            GetValue = () => Format(get(), format),
            SetValue = v =>
            {
                if (float.TryParse(v, NumberStyles.Float, Invariant, out float f)) set(f);
            }
        };

    private void Section(string key, string fallback)
    {
        _section = key;
        _all.Add(new SettingItem { Name = Localization.Get("settings_section_" + key, fallback), Type = SettingType.Section, Section = key });
    }

    private void Add(SettingItem item)
    {
        item.Section = _section;
        _all.Add(item);
    }

    private void UpdateRows()
    {
        _items.Clear();
        _items.AddRange(_all.Where(i => i.Type == SettingType.Section || _expanded.Contains(i.Section)));
    }

    private void BuildSettingsList()
    {
        _all.Clear();

        Section("general", "General");
        Add(new SettingItem
        {
            Name = Localization.Get("settings_language", "Language"),
            Type = SettingType.Enum,
            GetValue = () => Settings.Language.Value,
            SetValue = v =>
            {
                Settings.Language.Value = v;
                Localization.Load(v);
                BuildSettingsList();
            },
            Options = ["EN_US", "RU_RU"],
            Label = v => LanguageNames.GetValueOrDefault(v, v)
        });

        Add(new SettingItem
        {
            Name = Localization.Get("settings_ui_scale", "Interface scale"),
            Type = SettingType.Enum,
            GetValue = () => Settings.UiScale.Value.ToString(Invariant),
            SetValue = v => Settings.UiScale.Value = float.Parse(v, Invariant),
            Options = [..ScaleOptions],
            Label = v => v == "0"
                ? Localization.Get("settings_auto", "Auto")
                : (float.Parse(v, Invariant) * 100f).ToString("0", Invariant) + "%"
        });

        Add(new SettingItem
        {
            Name = Localization.Get("settings_on_screen_keyboard", "On-screen keyboard"),
            Type = SettingType.Enum,
            GetValue = () => Settings.OnScreenKeyboard.Value,
            SetValue = v => Settings.OnScreenKeyboard.Value = v,
            Options = ["Auto", "Always", "Never"],
            Label = v => Localization.Get("settings_osk_" + v.ToLowerInvariant(), v)
        });

        Add(new SettingItem
        {
            Name = Localization.Get("settings_quick_buttons", "Customize quick buttons"),
            Type = SettingType.Action,
            GetValue = () => Localization.Get("settings_quick_buttons_action", "Arrange"),
            OnClick = () =>
            {
                Close();
                OnCustomizeQuickButtons?.Invoke();
            }
        });

        Add(new SettingItem
        {
            Name = Localization.Get("settings_radial_menu", "Radial menu"),
            Type = SettingType.Action,
            GetValue = () => Localization.Get("settings_quick_buttons_action", "Arrange"),
            OnClick = () =>
            {
                Close();
                OnCustomizeRadialMenu?.Invoke();
            }
        });

        Section("brush", "Brush");
        Add(Numeric("settings_min_brush_width", "Min Brush Width",
            () => Settings.MinBrushWidth, v => Settings.MinBrushWidth.Value = Math.Max(1f, v)));
        Add(Numeric("settings_max_brush_width", "Max Brush Width",
            () => Settings.MaxBrushWidth, v => Settings.MaxBrushWidth.Value = Math.Max(Settings.MinBrushWidth, v)));

        Section("camera", "Camera");
        Add(Numeric("settings_camera_zoom_speed", "Camera Zoom Speed",
            () => Settings.CameraZoomSpeed, v => Settings.CameraZoomSpeed.Value = Math.Max(0.01f, v)));
        Add(Numeric("settings_camera_min_zoom", "Camera Min Zoom",
            () => Settings.CameraMinZoom, v => Settings.CameraMinZoom.Value = Math.Max(0.01f, v)));
        Add(Numeric("settings_camera_max_zoom", "Camera Max Zoom",
            () => Settings.CameraMaxZoom, v => Settings.CameraMaxZoom.Value = Math.Max(Settings.CameraMinZoom, v)));
        Add(Numeric("settings_camera_pan_speed", "Camera Pan Speed",
            () => Settings.CameraPanSpeed, v => Settings.CameraPanSpeed.Value = Math.Max(1f, v)));

        Add(new SettingItem
        {
            Name = Localization.Get("settings_grid", "Background grid"),
            Type = SettingType.Enum,
            GetValue = () => Settings.GridMode.Value,
            SetValue = v => Settings.GridMode.Value = v,
            Options = ["None", "Squares", "Dots", "Axes"],
            Label = v => Localization.Get("grid_" + v.ToLowerInvariant(), v)
        });

        Section("menu", "Menu");
        Add(new SettingItem
        {
            Name = Localization.Get("settings_radialmenu_close_on_tool_select", "RadialMenu Close On Tool Select"),
            Type = SettingType.Bool,
            GetValue = () => Settings.RadialMenuCloseOnToolSelect.Value.ToString(),
            OnClick = () => Settings.RadialMenuCloseOnToolSelect.Value = !Settings.RadialMenuCloseOnToolSelect.Value
        });

        Add(Numeric("settings_radialmenu_long_press", "RadialMenu Long Press",
            () => Settings.RadialMenuLongPressThreshold, v => Settings.RadialMenuLongPressThreshold.Value = Math.Max(0.1f, v)));
        Add(Numeric("settings_radialmenu_open_threshold", "RadialMenu Open Threshold",
            () => Settings.RadialMenuOpenThreshold, v => Settings.RadialMenuOpenThreshold.Value = Math.Max(0.05f, v)));
        Add(Numeric("settings_radialmenu_escape_time", "RadialMenu Escape Time",
            () => Settings.RadialMenuEscapeTime, v => Settings.RadialMenuEscapeTime.Value = Math.Max(0.02f, v)));
        Add(Numeric("settings_radialmenu_escape_distance", "RadialMenu Escape Distance",
            () => Settings.RadialMenuEscapeDistance, v => Settings.RadialMenuEscapeDistance.Value = Math.Max(1f, v)));

        Section("pen", "Pen");
        Add(Numeric("settings_hold_tolerance", "Hold tolerance, px",
            () => Settings.HoldTolerance, v => Settings.HoldTolerance.Value = Math.Clamp(v, 2f, 60f), "0"));
        Add(Numeric("settings_palm_erase", "Palm erases from, mm (0 = off)",
            () => Settings.PalmEraseMillimetres, v => Settings.PalmEraseMillimetres.Value = Math.Max(0f, v), "0"));
        Add(Numeric("settings_selection_tap_size", "Selection box smaller than this is a tap, px",
            () => Settings.SelectionTapSize, v => Settings.SelectionTapSize.Value = Math.Clamp(v, 0f, 200f), "0"));
        Add(Numeric("settings_selection_radius", "Tap selects the nearest within, px",
            () => Settings.SelectionRadius, v => Settings.SelectionRadius.Value = Math.Clamp(v, 1f, 200f), "0"));

        Section("system", "System");
        Add(new SettingItem
        {
            Name = Localization.Get("settings_render_backend", "Renderer (after restart)"),
            Type = SettingType.Enum,
            GetValue = () => Settings.RenderBackend.Value,
            SetValue = v => Settings.RenderBackend.Value = v,
            Options = [BackendLoader.Auto, ..Settings.AvailableBackends],
            Label = v => v == BackendLoader.Auto ? Localization.Get("settings_auto", "Auto") : v
        });

        Add(new SettingItem
        {
            Name = Localization.Get("settings_autosave_interval", "AutoSave Interval (min)"),
            Type = SettingType.IntNumeric,
            GetValue = () => Settings.AutoSaveIntervalMinutes.Value.ToString(Invariant),
            SetValue = v =>
            {
                if (int.TryParse(v, NumberStyles.Integer, Invariant, out int val))
                    Settings.AutoSaveIntervalMinutes.Value = Math.Clamp(val, 1, 60);
            }
        });

        Add(new SettingItem
        {
            Name = Localization.Get("settings_check_updates", "Check for updates at startup"),
            Type = SettingType.Bool,
            GetValue = () => Settings.CheckUpdates.Value.ToString(),
            OnClick = () => Settings.CheckUpdates.Value = !Settings.CheckUpdates.Value
        });

        Add(new SettingItem
        {
            Name = Localization.Get("settings_version", "Version") + " " + Updater.CurrentVersion.ToString(3),
            Type = SettingType.Action,
            GetValue = () => _updater.Status switch
            {
                Updater.State.Checking => Localization.Get("update_checking", "Checking..."),
                Updater.State.UpToDate => Localization.Get("update_latest", "Up to date"),
                Updater.State.Available => Localization.Get("update_install", "Install") + " " + _updater.AvailableVersion!.ToString(3),
                Updater.State.Downloading or Updater.State.Installing => $"{_updater.Progress:P0}",
                Updater.State.Failed => Localization.Get("update_failed", "Failed, retry"),
                _ => Localization.Get("update_check", "Check")
            },
            OnClick = () =>
            {
                if (_updater.Status == Updater.State.Available) _updater.Install();
                else _updater.Check();
            }
        });

        Add(new SettingItem
        {
            Name = Localization.Get("settings_open_latest_log", "Open Latest Log"),
            Type = SettingType.Action,
            GetValue = () => Localization.Get("settings_open_action", "Open"),
            OnClick = () =>
            {
                Close();
                OnOpenLog?.Invoke();
            }
        });

        UpdateRows();
    }

    private void Apply(Action action)
    {
        action();
        Settings.Save();
        _renderer.SetDirty();
    }

    public void ConfirmEdit()
    {
        if (_activeFieldIndex < 0) return;
        var item = _items[_activeFieldIndex];
        _activeFieldIndex = -1;
        Apply(() => item.SetValue?.Invoke(TextBox.ToString()));
    }

    public void CancelEdit()
    {
        _activeFieldIndex = -1;
        _renderer.SetDirty();
    }

    public void HandleCharInput(char c)
    {
        if (_activeFieldIndex < 0) return;
        if (c == ',') c = '.';
        if (c == '.' && (_items[_activeFieldIndex].Type == SettingType.IntNumeric || TextBox.ToString().Contains('.'))) return;
        if (c == '-' && TextBox.CursorPos > 0) return;
        TextBox.Insert(c);
    }

    // ------------------------------------------------------------- layout

    private (Vector2 Pos, Vector2 Size) PanelRect()
    {
        var size = new Vector2(MathF.Min(560f, _screenSize.X - 32f), MathF.Min(620f, _screenSize.Y - 32f));
        return ((_screenSize - size) * 0.5f, size);
    }

    private (float Top, float Bottom) ContentRange()
    {
        var (pos, size) = PanelRect();
        return (pos.Y + TitleBarHeight, pos.Y + size.Y - BottomBarHeight);
    }

    private float MaxScroll()
    {
        var (top, bottom) = ContentRange();
        return MathF.Max(0f, _items.Count * ItemHeight + Padding - (bottom - top));
    }

    private float RowY(int index) => ContentRange().Top + Padding * 0.5f + index * ItemHeight - _scrollY;

    private Vector2 ControlPos(int index)
    {
        var (pos, size) = PanelRect();
        return new Vector2(pos.X + size.X - Padding - ScrollbarWidth - ControlWidth, RowY(index) + (ItemHeight - ControlHeight) * 0.5f);
    }

    private (Vector2 Pos, Vector2 Size) CloseButton()
    {
        var (pos, size) = PanelRect();
        return (new Vector2(pos.X + size.X - 12f - 36f, pos.Y + (TitleBarHeight - 36f) * 0.5f), new Vector2(36f));
    }

    private (Vector2 Reset, Vector2 Save, Vector2 Size) BottomButtons()
    {
        var (pos, size) = PanelRect();
        var btn = new Vector2(140f, 38f);
        float y = pos.Y + size.Y - BottomBarHeight + (BottomBarHeight - btn.Y) * 0.5f;
        return (new Vector2(pos.X + Padding, y), new Vector2(pos.X + size.X - Padding - btn.X, y), btn);
    }

    private (Vector2 Pos, Vector2 Size) ScrollbarRect()
    {
        var (pos, size) = PanelRect();
        var (top, bottom) = ContentRange();
        float view = bottom - top;
        float total = view + MaxScroll();
        float h = MathF.Max(36f, view * view / total);
        float y = top + (MaxScroll() > 0 ? _scrollY / MaxScroll() * (view - h) : 0f);
        return (new Vector2(pos.X + size.X - ScrollbarWidth - 6f, y), new Vector2(ScrollbarWidth, h));
    }

    private (Vector2 Pos, float Height) DropdownList()
    {
        var item = _items[_openDropdownIndex];
        var control = ControlPos(_openDropdownIndex);
        float h = item.Options!.Count * DropdownOptionHeight + 8f;
        float y = control.Y + ControlHeight + 4f;
        if (y + h > ContentRange().Bottom) y = control.Y - h - 4f;
        return (new Vector2(control.X, y), h);
    }

    private int RowAt(Vector2 pos)
    {
        var (panelPos, panelSize) = PanelRect();
        var (top, bottom) = ContentRange();
        if (pos.X < panelPos.X || pos.X > panelPos.X + panelSize.X - ScrollbarWidth - 8f || pos.Y < top || pos.Y >= bottom)
            return -1;
        for (int i = 0; i < _items.Count; i++)
        {
            float y = RowY(i);
            if (pos.Y >= y && pos.Y < y + ItemHeight) return i;
        }

        return -1;
    }

    // ------------------------------------------------------------- input

    public void HandleScroll(float delta)
    {
        _openDropdownIndex = -1;
        _scrollY = Math.Clamp(_scrollY - delta * 40f, 0, MaxScroll());
        _renderer.SetDirty();
    }

    public void HandleMouseDown(Vector2 pos)
    {
        _mousePos = pos;
        _renderer.SetDirty();

        if (_openDropdownIndex >= 0)
        {
            var (listPos, listH) = DropdownList();
            var item = _items[_openDropdownIndex];
            int option = (int)((pos.Y - listPos.Y - 4f) / DropdownOptionHeight);
            _openDropdownIndex = -1;
            if (UiBatch.Contains(listPos, new Vector2(ControlWidth, listH), pos) && option >= 0 && option < item.Options!.Count)
                Apply(() => item.SetValue?.Invoke(item.Options[option]));
            return;
        }

        var (closePos, closeSize) = CloseButton();
        var (resetPos, savePos, btnSize) = BottomButtons();
        if (UiBatch.Contains(closePos, closeSize, pos) || UiBatch.Contains(savePos, btnSize, pos))
        {
            Close();
            return;
        }

        if (UiBatch.Contains(resetPos, btnSize, pos))
        {
            _activeFieldIndex = -1;
            Settings.ResetToDefaults();
            Localization.Load(Settings.Language.Value);
            BuildSettingsList();
            _renderer.SetDirty();
            return;
        }

        var (sbPos, sbSize) = ScrollbarRect();
        if (MaxScroll() > 0 && UiBatch.Contains(sbPos - new Vector2(6f, 0), sbSize + new Vector2(12f, 0), pos))
        {
            _isDraggingScrollbar = true;
            _pressPos = pos;
            _pressScroll = _scrollY;
            return;
        }

        int row = RowAt(pos);
        if (row != _activeFieldIndex) ConfirmEdit();
        _pressedRow = row;
        _pressPos = pos;
        _pressScroll = _scrollY;
        _scrollingByDrag = false;
    }

    public void HandleMouseMove(Vector2 pos)
    {
        _mousePos = pos;
        _renderer.SetDirty();

        if (_isDraggingScrollbar)
        {
            var (top, bottom) = ContentRange();
            float view = bottom - top;
            float track = view - ScrollbarRect().Size.Y;
            if (track > 0) _scrollY = Math.Clamp(_pressScroll + (pos.Y - _pressPos.Y) / track * MaxScroll(), 0, MaxScroll());
            return;
        }

        if (_pressedRow >= 0 && !_scrollingByDrag && MathF.Abs(pos.Y - _pressPos.Y) > DragThreshold)
            _scrollingByDrag = true;
        if (_scrollingByDrag)
            _scrollY = Math.Clamp(_pressScroll - (pos.Y - _pressPos.Y), 0, MaxScroll());
    }

    public void HandleMouseUp(Vector2 pos)
    {
        _mousePos = pos;
        _isDraggingScrollbar = false;
        int row = _pressedRow;
        _pressedRow = -1;
        _renderer.SetDirty();
        if (_scrollingByDrag || row < 0 || RowAt(pos) != row) return;

        var item = _items[row];
        switch (item.Type)
        {
            case SettingType.Numeric:
            case SettingType.IntNumeric:
                if (_activeFieldIndex != row)
                {
                    _activeFieldIndex = row;
                    TextBox.SetText(item.GetValue(), selectAll: true);
                }

                break;
            case SettingType.Enum:
                _openDropdownIndex = row;
                break;
            case SettingType.Bool:
                Apply(item.OnClick!);
                break;
            case SettingType.Action:
                item.OnClick?.Invoke();
                break;
            case SettingType.Section:
                if (!_expanded.Remove(item.Section)) _expanded.Add(item.Section);
                UpdateRows();
                break;
        }
    }

    // ------------------------------------------------------------- rendering

    public void Render(UiBatch ui, Vector2 screenSize)
    {
        if (!IsOpen) return;
        _screenSize = screenSize;
        _scrollY = Math.Clamp(_scrollY, 0, MaxScroll());

        ui.Rect(Vector2.Zero, screenSize, Theme.Overlay);
        var (pos, size) = PanelRect();
        ui.Rect(pos, size, Theme.PopupBg, Theme.PopupRadius);
        ui.Border(pos, size, Theme.Border, 1f, Theme.PopupRadius);

        ui.TextInRect(Localization.Get("settings_title", "Settings"), pos + new Vector2(Padding, 0),
            new Vector2(size.X - Padding * 2 - 48f, TitleBarHeight), Theme.Text, Theme.Title);
        var (closePos, closeSize) = CloseButton();
        ui.IconButton(Icons.Close, closePos, closeSize, UiBatch.Contains(closePos, closeSize, _mousePos), iconSize: 14f);

        var (top, bottom) = ContentRange();
        ui.Rect(new Vector2(pos.X, top), new Vector2(size.X, 1f), Theme.Border);
        ui.Rect(new Vector2(pos.X, bottom - 1f), new Vector2(size.X, 1f), Theme.Border);

        ui.PushClip(new Vector2(pos.X, top), new Vector2(size.X, bottom - top));
        float labelWidth = size.X - Padding * 2 - ScrollbarWidth - ControlWidth - 16f;
        for (int i = 0; i < _items.Count; i++)
        {
            float y = RowY(i);
            if (y + ItemHeight < top || y > bottom) continue;
            var item = _items[i];

            bool hover = _openDropdownIndex < 0 && !_isDraggingScrollbar && !_scrollingByDrag && RowAt(_mousePos) == i;
            if (hover) ui.Rect(new Vector2(pos.X + 8f, y + 2f), new Vector2(size.X - 16f - ScrollbarWidth, ItemHeight - 4f), Theme.RowHover, 8f);

            if (item.Type == SettingType.Section)
            {
                RenderSectionHeader(ui, item, new Vector2(pos.X + Padding, y), size.X - Padding * 2 - ScrollbarWidth);
                continue;
            }

            ui.TextInRect(item.Name, new Vector2(pos.X + Padding, y), new Vector2(labelWidth, ItemHeight), Theme.Text, Theme.Body);
            RenderControl(ui, i, item, hover);
        }

        ui.PopClip();

        if (MaxScroll() > 0)
        {
            var (sbPos, sbSize) = ScrollbarRect();
            ui.Rect(sbPos, sbSize, _isDraggingScrollbar ? Theme.TextMuted : Theme.Border, ScrollbarWidth * 0.5f);
        }

        var (resetPos, savePos, btnSize) = BottomButtons();
        ui.TextButton(Localization.Get("settings_reset", "Reset"), resetPos, btnSize, UiBatch.Contains(resetPos, btnSize, _mousePos), UiBatch.ButtonKind.Secondary);
        ui.TextButton(Localization.Get("dialog_button_save", "Save"), savePos, btnSize, UiBatch.Contains(savePos, btnSize, _mousePos), UiBatch.ButtonKind.Primary);

        if (_openDropdownIndex >= 0) RenderDropdownList(ui);
    }

    /// <summary>The section's name, a rounded line after it and the chevron that opens or closes the section.</summary>
    private void RenderSectionHeader(UiBatch ui, SettingItem item, Vector2 pos, float width)
    {
        float mid = pos.Y + ItemHeight * 0.5f;
        float nameWidth = MathF.Min(ui.Measure(item.Name, Theme.Label), width - 60f);
        ui.TextInRect(item.Name, pos, new Vector2(nameWidth + 2f, ItemHeight), Theme.Text, Theme.Label);
        float lineStart = pos.X + nameWidth + 14f, lineEnd = pos.X + width - 30f;
        ui.Rect(new Vector2(lineStart, mid - 1.5f), new Vector2(lineEnd - lineStart, 3f), Theme.Surface, 1.5f);
        ui.Icon(_expanded.Contains(item.Section) ? Icons.ChevronDown : Icons.ChevronRight, new Vector2(pos.X + width - 12f, mid), 12f,
            Theme.TextSecondary);
    }

    private void RenderControl(UiBatch ui, int index, SettingItem item, bool rowHover)
    {
        var pos = ControlPos(index);
        var size = new Vector2(ControlWidth, ControlHeight);

        switch (item.Type)
        {
            case SettingType.Numeric:
            case SettingType.IntNumeric:
                if (_activeFieldIndex == index)
                    ui.TextField(TextBox, pos, size, true, Theme.Body, Theme.Text);
                else
                {
                    ui.Rect(pos, size, Theme.WithAlpha(Theme.Field, 0.7f), 6f);
                    ui.Border(pos, size, rowHover ? Theme.TextMuted : Theme.Border, 1f, 6f);
                    ui.TextInRect(item.GetValue(), pos + new Vector2(10f, 0), size - new Vector2(20f, 0), Theme.Text, Theme.Body);
                }

                break;
            case SettingType.Enum:
            {
                bool open = _openDropdownIndex == index;
                ui.Rect(pos, size, rowHover || open ? Theme.SurfaceHover : Theme.Surface, 6f);
                if (open) ui.Border(pos, size, Theme.Accent, 1.5f, 6f);
                ui.TextInRect(item.Label(item.GetValue()), pos + new Vector2(10f, 0), size - new Vector2(40f, 0), Theme.Text, Theme.Body);
                ui.Icon(Icons.ChevronDown, pos + new Vector2(size.X - 18f, size.Y * 0.5f), 11f, Theme.TextSecondary);
                break;
            }
            case SettingType.Bool:
            {
                bool on = item.GetValue() == "True";
                var trackSize = new Vector2(44f, 24f);
                var trackPos = new Vector2(pos.X + size.X - trackSize.X, pos.Y + (size.Y - trackSize.Y) * 0.5f);
                ui.Rect(trackPos, trackSize, on ? Theme.Accent : Theme.Surface, 12f);
                if (!on) ui.Border(trackPos, trackSize, Theme.TextMuted, 1f, 12f);
                float knobX = on ? trackPos.X + trackSize.X - 12f : trackPos.X + 12f;
                ui.Circle(new Vector2(knobX, trackPos.Y + 12f), on ? 8f : 6f, on ? Vector4.One : Theme.TextSecondary);
                break;
            }
            case SettingType.Action:
                ui.TextButton(item.GetValue(), pos, size, rowHover, UiBatch.ButtonKind.Secondary);
                break;
        }
    }

    private void RenderDropdownList(UiBatch ui)
    {
        var item = _items[_openDropdownIndex];
        var (pos, h) = DropdownList();
        var size = new Vector2(ControlWidth, h);
        ui.Rect(pos + new Vector2(0, 3f), size, new Vector4(0, 0, 0, 0.3f), 8f);
        ui.Rect(pos, size, Theme.PopupBg, 8f);
        ui.Border(pos, size, Theme.Border, 1f, 8f);

        string current = item.GetValue();
        for (int i = 0; i < item.Options!.Count; i++)
        {
            var optPos = new Vector2(pos.X + 4f, pos.Y + 4f + i * DropdownOptionHeight);
            var optSize = new Vector2(ControlWidth - 8f, DropdownOptionHeight);
            bool selected = item.Options[i] == current;
            if (UiBatch.Contains(optPos, optSize, _mousePos)) ui.Rect(optPos, optSize, Theme.SurfaceHover, 6f);
            else if (selected) ui.Rect(optPos, optSize, Theme.AccentSoft, 6f);
            ui.TextInRect(item.Label(item.Options[i]), optPos + new Vector2(10f, 0), optSize - new Vector2(36f, 0), Theme.Text, Theme.Body);
            if (selected) ui.Icon(Icons.CheckMark, optPos + new Vector2(optSize.X - 16f, optSize.Y * 0.5f), 12f, Theme.Accent);
        }
    }

    public void Dispose() { }
}
