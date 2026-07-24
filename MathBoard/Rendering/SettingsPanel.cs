using System.Numerics;
using MathBoard.Core;

namespace MathBoard.Rendering;

public class SettingsPanel : IDisposable
{
    public bool IsOpen { get; private set; }

    private readonly StrokeRenderer _renderer;
    private readonly Action _onRequestAtlasRebuild;
    private Vector2 _screenSize = new(1280, 720);
    private Vector2 _mousePos;
    private float _scrollY = 0f;

    private const float PanelWidth = 520f;
    private const float PanelHeight = 580f;
    private const float TitleBarHeight = 50f;
    private const float BottomBarHeight = 60f;
    private const float ItemHeight = 44f;
    private const float InputFieldWidth = 180f;
    private const float InputFieldHeight = 32f;
    private const float Padding = 20f;
    private const float ScrollbarWidth = 8f;
    private const float DropdownOptionHeight = 32f;

    private static readonly Vector4 TextColor = new(0.93f, 0.93f, 0.95f, 1f);
    private static readonly Vector4 ValueColor = new(0.6f, 0.8f, 1f, 1f);
    private static readonly Vector4 PanelBgColor = new(0.10f, 0.10f, 0.14f, 0.98f);
    private static readonly Vector4 TitleBarColor = new(0.13f, 0.13f, 0.17f, 1f);
    private static readonly Vector4 InputBgColor = new(0.05f, 0.05f, 0.07f, 1f);
    private static readonly Vector4 InputFocusBgColor = new(0.12f, 0.18f, 0.32f, 1f);
    private static readonly Vector4 HoverColor = new(0.15f, 0.15f, 0.20f, 0.5f);
    private static readonly Vector4 OutlineColor = new(0.30f, 0.32f, 0.40f, 1f);
    private static readonly Vector4 FocusOutlineColor = new(0.4f, 0.6f, 1.0f, 1f);
    private static readonly Vector4 ButtonBgColor = new(0.22f, 0.42f, 0.78f, 1f);
    private static readonly Vector4 CancelButtonBgColor = new(0.20f, 0.20f, 0.25f, 1f);
    private static readonly Vector4 DropdownBgColor = new(0.08f, 0.08f, 0.12f, 1f);
    private static readonly Vector4 DropdownHoverColor = new(0.20f, 0.30f, 0.50f, 0.8f);
    private static readonly Vector4 DropdownSelectedColor = new(0.15f, 0.25f, 0.40f, 0.9f);
    private static readonly Vector4 ScrollbarColor = new(0.3f, 0.3f, 0.35f, 0.6f);
    private static readonly Vector4 BoolOnColor = new(0.2f, 0.5f, 0.3f, 1f);
    private static readonly Vector4 BoolOffColor = new(0.3f, 0.2f, 0.2f, 1f);

    private enum SettingType { Numeric, IntNumeric, Enum, Bool, Action }

    private class SettingItem
    {
        public string Name = "";
        public SettingType Type;
        public Func<string> GetValue = () => "";
        public Action<string>? SetValue;
        public Action? OnClick;
        public List<string>? Options;
    }

    private readonly List<SettingItem> _items = new();

    public LibraryPanel.TextBoxState TextBox { get; } = new();
    public bool HasTextInput => _activeFieldIndex >= 0;

    private int _activeFieldIndex = -1;
    private int _openDropdownIndex = -1;

    private readonly List<(int index, float x, float y, float w, float h, SettingType type)> _hitRegions = new();
    private readonly List<(int optionIdx, float x, float y, float w, float h)> _dropdownOptionRegions = new();
    private Vector4 _closeButtonBounds;
    private Vector4 _saveButtonBounds;
    private Vector4 _resetButtonBounds;
    private Vector4 _scrollbarBounds;
    private bool _isDraggingScrollbar = false;

    public Action? OnOpenLog { get; set; }

    public SettingsPanel(StrokeRenderer renderer, Action onRequestAtlasRebuild)
    {
        _renderer = renderer;
        _onRequestAtlasRebuild = onRequestAtlasRebuild;
    }

    public void Open()
    {
        IsOpen = true;
        _scrollY = 0;
        _activeFieldIndex = -1;
        _openDropdownIndex = -1;
        BuildSettingsList();
        _onRequestAtlasRebuild?.Invoke();
    }

    public void Close()
    {
        ConfirmEdit();
        IsOpen = false;
        _activeFieldIndex = -1;
        _openDropdownIndex = -1;
        _renderer.SetDirty();
        _onRequestAtlasRebuild?.Invoke();
    }

    public void Cancel()
    {
        CancelEdit();
        IsOpen = false;
        _activeFieldIndex = -1;
        _openDropdownIndex = -1;
        _renderer.SetDirty();
        _onRequestAtlasRebuild?.Invoke();
    }

    private void BuildSettingsList()
    {
        _items.Clear();

        _items.Add(new SettingItem
        {
            Name = Localization.Get("settings_language", "Language"),
            Type = SettingType.Enum,
            GetValue = () => Settings.Language.Value,
            SetValue = v => ExecuteAction(() =>
            {
                Settings.Language.Value = v;
                Localization.Load(v);
                BuildSettingsList(); // Обновляем названия после смены языка
            }),
            Options = ["EN_US", "RU_RU"]
        });

        _items.Add(new SettingItem
        {
            Name = Localization.Get("settings_library_panel_on_top", "Library Panel On Top"),
            Type = SettingType.Bool,
            GetValue = () => Settings.LibraryPanelOnTop.Value.ToString(),
            OnClick = () => ExecuteAction(() => Settings.LibraryPanelOnTop.Value = !Settings.LibraryPanelOnTop.Value)
        });

        _items.Add(new SettingItem
        {
            Name = Localization.Get("settings_radialmenu_close_on_tool_select", "RadialMenu Close On Tool Select"),
            Type = SettingType.Bool,
            GetValue = () => Settings.RadialMenuCloseOnToolSelect.Value.ToString(),
            OnClick = () => ExecuteAction(() => Settings.RadialMenuCloseOnToolSelect.Value = !Settings.RadialMenuCloseOnToolSelect.Value)
        });

        _items.Add(new SettingItem
        {
            Name = Localization.Get("settings_autosave_interval", "AutoSave Interval (min)"),
            Type = SettingType.IntNumeric,
            GetValue = () => Settings.AutoSaveIntervalMinutes.Value.ToString(),
            SetValue = v => ExecuteAction(() => { if (int.TryParse(v, out int val)) Settings.AutoSaveIntervalMinutes.Value = Math.Clamp(val, 1, 60); })
        });

        _items.Add(new SettingItem
        {
            Name = Localization.Get("settings_stroke_circle_segments", "Stroke Circle Segments"),
            Type = SettingType.IntNumeric,
            GetValue = () => Settings.StrokeCircleSegments.Value.ToString(),
            SetValue = v => ExecuteAction(() => { if (int.TryParse(v, out int val)) Settings.StrokeCircleSegments.Value = Math.Clamp(val, 3, 64); })
        });

        _items.Add(new SettingItem
        {
            Name = Localization.Get("settings_ui_ring_segments", "UI Ring Segments"),
            Type = SettingType.IntNumeric,
            GetValue = () => Settings.UIRingSegments.Value.ToString(),
            SetValue = v => ExecuteAction(() => { if (int.TryParse(v, out int val)) Settings.UIRingSegments.Value = Math.Clamp(val, 16, 256); })
        });

        _items.Add(new SettingItem
        {
            Name = Localization.Get("settings_radialmenu_long_press", "RadialMenu Long Press"),
            Type = SettingType.Numeric,
            GetValue = () => Settings.RadialMenuLongPressThreshold.Value.ToString("F1"),
            SetValue = v => ExecuteAction(() => { if (float.TryParse(v, out float val)) Settings.RadialMenuLongPressThreshold.Value = Math.Max(0.1f, val); })
        });

        _items.Add(new SettingItem
        {
            Name = Localization.Get("settings_radialmenu_open_threshold", "RadialMenu Open Threshold"),
            Type = SettingType.Numeric,
            GetValue = () => Settings.RadialMenuOpenThreshold.Value.ToString("F2"),
            SetValue = v => ExecuteAction(() => { if (float.TryParse(v, out float val)) Settings.RadialMenuOpenThreshold.Value = Math.Max(0.05f, val); })
        });

        _items.Add(new SettingItem
        {
            Name = Localization.Get("settings_radialmenu_escape_time", "RadialMenu Escape Time"),
            Type = SettingType.Numeric,
            GetValue = () => Settings.RadialMenuEscapeTime.Value.ToString("F2"),
            SetValue = v => ExecuteAction(() => { if (float.TryParse(v, out float val)) Settings.RadialMenuEscapeTime.Value = Math.Max(0.02f, val); })
        });

        _items.Add(new SettingItem
        {
            Name = Localization.Get("settings_radialmenu_escape_distance", "RadialMenu Escape Distance"),
            Type = SettingType.Numeric,
            GetValue = () => Settings.RadialMenuEscapeDistance.Value.ToString("F1"),
            SetValue = v => ExecuteAction(() => { if (float.TryParse(v, out float val)) Settings.RadialMenuEscapeDistance.Value = Math.Max(1f, val); })
        });

        _items.Add(new SettingItem
        {
            Name = Localization.Get("settings_camera_zoom_speed", "Camera Zoom Speed"),
            Type = SettingType.Numeric,
            GetValue = () => Settings.CameraZoomSpeed.Value.ToString("F2"),
            SetValue = v => ExecuteAction(() => { if (float.TryParse(v, out float val)) Settings.CameraZoomSpeed.Value = Math.Max(0.01f, val); })
        });

        _items.Add(new SettingItem
        {
            Name = Localization.Get("settings_camera_min_zoom", "Camera Min Zoom"),
            Type = SettingType.Numeric,
            GetValue = () => Settings.CameraMinZoom.Value.ToString("F1"),
            SetValue = v => ExecuteAction(() => { if (float.TryParse(v, out float val)) Settings.CameraMinZoom.Value = Math.Max(0.1f, val); })
        });

        _items.Add(new SettingItem
        {
            Name = Localization.Get("settings_camera_max_zoom", "Camera Max Zoom"),
            Type = SettingType.Numeric,
            GetValue = () => Settings.CameraMaxZoom.Value.ToString("F1"),
            SetValue = v => ExecuteAction(() => { if (float.TryParse(v, out float val)) Settings.CameraMaxZoom.Value = Math.Max(Settings.CameraMinZoom, val); })
        });

        _items.Add(new SettingItem
        {
            Name = Localization.Get("settings_camera_pan_speed", "Camera Pan Speed"),
            Type = SettingType.Numeric,
            GetValue = () => Settings.CameraPanSpeed.Value.ToString("F1"),
            SetValue = v => ExecuteAction(() => { if (float.TryParse(v, out float val)) Settings.CameraPanSpeed.Value = Math.Max(1f, val); })
        });

        _items.Add(new SettingItem
        {
            Name = Localization.Get("settings_default_brush_width", "Default Brush Width"),
            Type = SettingType.Numeric,
            GetValue = () => Settings.DefaultBrushWidth.Value.ToString("F1"),
            SetValue = v => ExecuteAction(() => { if (float.TryParse(v, out float val)) Settings.DefaultBrushWidth.Value = Math.Clamp(val, Settings.MinBrushWidth, Settings.MaxBrushWidth); })
        });

        _items.Add(new SettingItem
        {
            Name = Localization.Get("settings_default_eraser_size", "Default Eraser Size"),
            Type = SettingType.Numeric,
            GetValue = () => Settings.DefaultEraserSize.Value.ToString("F1"),
            SetValue = v => ExecuteAction(() => { if (float.TryParse(v, out float val)) Settings.DefaultEraserSize.Value = Math.Clamp(val, 1f, 100f); })
        });

        _items.Add(new SettingItem
        {
            Name = Localization.Get("settings_min_brush_width", "Min Brush Width"),
            Type = SettingType.Numeric,
            GetValue = () => Settings.MinBrushWidth.Value.ToString("F1"),
            SetValue = v => ExecuteAction(() => { if (float.TryParse(v, out float val)) Settings.MinBrushWidth.Value = Math.Max(1f, val); })
        });

        _items.Add(new SettingItem
        {
            Name = Localization.Get("settings_max_brush_width", "Max Brush Width"),
            Type = SettingType.Numeric,
            GetValue = () => Settings.MaxBrushWidth.Value.ToString("F1"),
            SetValue = v => ExecuteAction(() => { if (float.TryParse(v, out float val)) Settings.MaxBrushWidth.Value = Math.Max(Settings.MinBrushWidth, val); })
        });

        _items.Add(new SettingItem
        {
            Name = Localization.Get("settings_radialmenu_outer_radius", "RadialMenu Outer Radius"),
            Type = SettingType.Numeric,
            GetValue = () => Settings.RadialMenuOuterRadius.Value.ToString("F0"),
            SetValue = v => ExecuteAction(() => { if (float.TryParse(v, out float val)) Settings.RadialMenuOuterRadius.Value = Math.Max(50f, val); })
        });

        _items.Add(new SettingItem
        {
            Name = Localization.Get("settings_radialmenu_inner_radius", "RadialMenu Inner Radius"),
            Type = SettingType.Numeric,
            GetValue = () => Settings.RadialMenuInnerRadius.Value.ToString("F0"),
            SetValue = v => ExecuteAction(() => { if (float.TryParse(v, out float val)) Settings.RadialMenuInnerRadius.Value = Math.Max(10f, val); })
        });

        _items.Add(new SettingItem
        {
            Name = Localization.Get("settings_radialmenu_center_radius", "RadialMenu Center Radius"),
            Type = SettingType.Numeric,
            GetValue = () => Settings.RadialMenuCenterRadius.Value.ToString("F0"),
            SetValue = v => ExecuteAction(() => { if (float.TryParse(v, out float val)) Settings.RadialMenuCenterRadius.Value = Math.Max(5f, val); })
        });

        _items.Add(new SettingItem
        {
            Name = Localization.Get("settings_open_latest_log", "Open Latest Log"),
            Type = SettingType.Action,
            GetValue = () => "",
            OnClick = () => { Close(); OnOpenLog?.Invoke(); }
        });
    }

    private void ExecuteAction(Action act)
    {
        act.Invoke();
        Settings.Save();
        _renderer.SetDirty();
        _onRequestAtlasRebuild?.Invoke();
    }

    public void ConfirmEdit()
    {
        if (_activeFieldIndex < 0) return;
        var item = _items[_activeFieldIndex];
        var text = TextBox.ToString();
        try { item.SetValue?.Invoke(text); } catch { }
        _activeFieldIndex = -1;
        _onRequestAtlasRebuild?.Invoke();
        _renderer.SetDirty();
    }

    public void CancelEdit()
    {
        _activeFieldIndex = -1;
        _onRequestAtlasRebuild?.Invoke();
        _renderer.SetDirty();
    }

    public void UpdateMousePos(Vector2 pos)
    {
        _mousePos = pos;
        if (_isDraggingScrollbar)
        {
            UpdateScrollFromMouse(pos);
            _renderer.SetDirty();
        }
    }

    private void UpdateScrollFromMouse(Vector2 pos)
    {
        float py = (_screenSize.Y - PanelHeight) * 0.5f;
        float contentTop = py + TitleBarHeight;
        float contentBottom = py + PanelHeight - BottomBarHeight;
        float contentHeight = contentBottom - contentTop;
        float totalContentHeight = _items.Count * ItemHeight + Padding * 2;
        float maxScroll = Math.Max(0, totalContentHeight - contentHeight);
        if (maxScroll <= 0) return;

        float sbHeight = Math.Max(30f, contentHeight * (contentHeight / totalContentHeight));
        float relativeY = pos.Y - contentTop;
        float scrollPercent = Math.Clamp((relativeY - sbHeight * 0.5f) / (contentHeight - sbHeight), 0, 1);
        _scrollY = scrollPercent * maxScroll;
    }

    public void HandleScroll(float delta)
    {
        if (_openDropdownIndex >= 0)
        {
            _openDropdownIndex = -1;
            _renderer.SetDirty();
            return;
        }
        _scrollY -= delta * 28f;
        float contentHeight = PanelHeight - TitleBarHeight - BottomBarHeight - Padding * 2;
        float totalContentHeight = _items.Count * ItemHeight;
        float maxScroll = Math.Max(0, totalContentHeight - contentHeight);
        _scrollY = Math.Clamp(_scrollY, 0, maxScroll);
    }

    public void HandleCharInput(char c)
    {
        if (_activeFieldIndex < 0) return;
        var item = _items[_activeFieldIndex];
        if (item.Type != SettingType.Numeric && item.Type != SettingType.IntNumeric) return;

        if (!char.IsDigit(c) && c != '.' && c != '-' && c != ',') return;
        if (item.Type == SettingType.IntNumeric && (c == '.' || c == ',')) return;
        if (c == '-' && TextBox.CursorPos > 0) return;
        if (c == ',') c = '.';
        if (c == '.' && TextBox.ToString().Contains('.')) return;

        TextBox.Insert(c);
        _onRequestAtlasRebuild?.Invoke();
    }

    public void HandleMouseDown(Vector2 pos)
    {
        _mousePos = pos;
        if (_screenSize.X < 1 || _screenSize.Y < 1) return;

        if (HitTest(_scrollbarBounds, pos))
        {
            _isDraggingScrollbar = true;
            UpdateScrollFromMouse(pos);
            return;
        }

        if (HitTest(_closeButtonBounds, pos)) { Close(); return; }
        if (HitTest(_saveButtonBounds, pos)) { Close(); return; }

        if (HitTest(_resetButtonBounds, pos))
        {
            Settings.ResetToDefaults();
            Localization.Load(Settings.Language.Value);
            BuildSettingsList();
            _onRequestAtlasRebuild?.Invoke();
            _renderer.SetDirty();
            return;
        }

        if (_openDropdownIndex >= 0)
        {
            for (int i = 0; i < _dropdownOptionRegions.Count; i++)
            {
                var (optIdx, ox, oy, ow, oh) = _dropdownOptionRegions[i];
                if (pos.X >= ox && pos.X <= ox + ow && pos.Y >= oy && pos.Y <= oy + oh)
                {
                    var item = _items[_openDropdownIndex];
                    item.SetValue?.Invoke(item.Options![optIdx]);
                    _openDropdownIndex = -1;
                    return;
                }
            }
            _openDropdownIndex = -1;
            _renderer.SetDirty();
            return;
        }

        float py = (_screenSize.Y - PanelHeight) * 0.5f;
        float contentTop = py + TitleBarHeight;
        float contentBottom = py + PanelHeight - BottomBarHeight;
        if (pos.Y < contentTop || pos.Y > contentBottom)
        {
            if (_activeFieldIndex >= 0)
                ConfirmEdit();
            return;
        }

        for (int i = 0; i < _hitRegions.Count; i++)
        {
            var (idx, x, y, w, h, type) = _hitRegions[i];
            if (pos.X >= x && pos.X <= x + w && pos.Y >= y && pos.Y <= y + h)
            {
                var item = _items[idx];
                switch (type)
                {
                    case SettingType.Numeric:
                    case SettingType.IntNumeric:
                        if (_activeFieldIndex >= 0 && _activeFieldIndex != idx)
                            ConfirmEdit();
                        _activeFieldIndex = idx;
                        TextBox.SetText(item.GetValue());
                        _onRequestAtlasRebuild?.Invoke();
                        _renderer.SetDirty();
                        break;
                    case SettingType.Enum:
                        _openDropdownIndex = (_openDropdownIndex == idx) ? -1 : idx;
                        _onRequestAtlasRebuild?.Invoke(); // Фикс: сразу добавляем опции в атлас
                        _renderer.SetDirty();
                        break;
                    case SettingType.Bool:
                    case SettingType.Action:
                        item.OnClick?.Invoke();
                        break;
                }
                return;
            }
        }

        if (_activeFieldIndex >= 0)
            ConfirmEdit();
    }

    public void HandleMouseUp(Vector2 pos)
    {
        _isDraggingScrollbar = false;
    }

    private bool HitTest(Vector4 bounds, Vector2 pos)
        => pos.X >= bounds.X && pos.X <= bounds.X + bounds.Z &&
           pos.Y >= bounds.Y && pos.Y <= bounds.Y + bounds.W;

    public void RequestAtlasEntries(TextAtlas atlas)
    {
        if (!IsOpen) return;

        atlas.Request(Localization.Get("settings_title", "Settings"));
        atlas.Request("X");
        atlas.Request(Localization.Get("dialog_button_save", "Save"));
        atlas.Request(Localization.Get("settings_reset", "Reset"));
        atlas.Request("\u25B2"); // ▲
        atlas.Request("\u25BC"); // ▼
        atlas.Request("ON");
        atlas.Request("OFF");

        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            atlas.Request(item.Name);
            var val = item.GetValue();
            if (!string.IsNullOrEmpty(val)) atlas.Request(val);

            if (item.Type == SettingType.Enum && _openDropdownIndex == i && item.Options != null)
            {
                foreach (var opt in item.Options)
                    atlas.Request(opt);
            }
        }

        if (HasTextInput && !string.IsNullOrEmpty(TextBox.ToString()))
            atlas.Request(TextBox.ToString());
    }

    public void RenderToVertices(List<UICommand> cmds, Vector2 screenSize)
    {
        if (!IsOpen) return;

        _screenSize = screenSize;
        var atlas = _renderer.TextAtlas;

        DrawRect(cmds, Vector2.Zero, screenSize, new Vector4(0, 0, 0, 0.55f));

        float px = (screenSize.X - PanelWidth) * 0.5f;
        float py = (screenSize.Y - PanelHeight) * 0.5f;

        DrawRect(cmds, new Vector2(px, py), new Vector2(PanelWidth, PanelHeight), PanelBgColor);
        DrawRectOutline(cmds, new Vector2(px, py), new Vector2(PanelWidth, PanelHeight), OutlineColor, 2f);

        float contentTop = py + TitleBarHeight;
        float contentBottom = py + PanelHeight - BottomBarHeight;
        float contentHeight = contentBottom - contentTop;

        DrawRect(cmds, new Vector2(px, contentTop), new Vector2(PanelWidth, contentHeight), new Vector4(0.06f, 0.06f, 0.085f, 1f));

        _hitRegions.Clear();
        _dropdownOptionRegions.Clear();

        float inputX = px + PanelWidth - InputFieldWidth - Padding - ScrollbarWidth;
        float itemY = contentTop + Padding - _scrollY;

        float dropdownListY = -1;
        if (_openDropdownIndex >= 0)
        {
            var ddItem = _items[_openDropdownIndex];
            float ddItemY = contentTop + Padding + _openDropdownIndex * ItemHeight - _scrollY;
            float ddInputY = ddItemY + (ItemHeight - InputFieldHeight) * 0.5f;
            float ddListY = ddInputY + InputFieldHeight + 2f;
            float ddListH = ddItem.Options!.Count * DropdownOptionHeight;
            if (ddListY + ddListH > contentBottom) {
                ddListY = ddInputY - ddListH - 2f;
            }
            dropdownListY = ddListY;
        }

        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];

            if (itemY + ItemHeight < contentTop || itemY > contentBottom)
            {
                itemY += ItemHeight;
                continue;
            }

            bool isHover = _mousePos.X >= px + Padding && _mousePos.X <= px + PanelWidth - ScrollbarWidth - Padding &&
                          _mousePos.Y >= itemY && _mousePos.Y <= itemY + ItemHeight && !_isDraggingScrollbar;

            if (isHover && item.Type != SettingType.Numeric && item.Type != SettingType.IntNumeric)
                DrawRect(cmds, new Vector2(px + 2, itemY), new Vector2(PanelWidth - 4, ItemHeight), HoverColor);

            var nameSize = atlas.Measure(item.Name);
            atlas.Emit(item.Name, new Vector2(px + Padding, itemY + (ItemHeight - nameSize.Y) * 0.5f), TextColor);

            float inputY = itemY + (ItemHeight - InputFieldHeight) * 0.5f;

            switch (item.Type)
            {
                case SettingType.Numeric:
                case SettingType.IntNumeric:
                    RenderTextInputField(cmds, atlas, i, inputX, inputY, item);
                    _hitRegions.Add((i, inputX, inputY, InputFieldWidth, InputFieldHeight, item.Type));
                    break;
                case SettingType.Enum:
                    RenderDropdownField(cmds, atlas, i, inputX, inputY, item);
                    _hitRegions.Add((i, inputX, inputY, InputFieldWidth, InputFieldHeight, item.Type));
                    break;
                case SettingType.Bool:
                    RenderBoolToggle(cmds, atlas, inputX, inputY, item);
                    _hitRegions.Add((i, inputX, inputY, InputFieldWidth, InputFieldHeight, item.Type));
                    break;
                case SettingType.Action:
                    RenderActionButton(cmds, atlas, inputX, inputY, item);
                    _hitRegions.Add((i, inputX, inputY, InputFieldWidth, InputFieldHeight, item.Type));
                    break;
            }

            itemY += ItemHeight;
        }

        float totalContentHeight = _items.Count * ItemHeight + Padding * 2;
        float maxScroll = Math.Max(0, totalContentHeight - contentHeight);
        if (maxScroll > 0)
        {
            float sbHeight = Math.Max(30f, contentHeight * (contentHeight / totalContentHeight));
            float sbY = contentTop + (_scrollY / maxScroll) * (contentHeight - sbHeight);
            _scrollbarBounds = new Vector4(px + PanelWidth - ScrollbarWidth - 2f, sbY, ScrollbarWidth, sbHeight);
            DrawRect(cmds, new Vector2(_scrollbarBounds.X, _scrollbarBounds.Y),
                new Vector2(_scrollbarBounds.Z, _scrollbarBounds.W), ScrollbarColor);
        }

        // Рендер верхней панели ПЕРЕД dropdown, но ПОСЛЕ элементов
        DrawRect(cmds, new Vector2(px, py), new Vector2(PanelWidth, TitleBarHeight), TitleBarColor);
        var title = Localization.Get("settings_title", "Settings");
        var titleSize = atlas.Measure(title);
        atlas.Emit(title, new Vector2(px + (PanelWidth - titleSize.X) * 0.5f, py + (TitleBarHeight - titleSize.Y) * 0.5f), TextColor);

        float closeBtnSize = 32f;
        float closeBtnX = px + PanelWidth - closeBtnSize - 12f;
        float closeBtnY = py + (TitleBarHeight - closeBtnSize) * 0.5f;
        _closeButtonBounds = new Vector4(closeBtnX, closeBtnY, closeBtnSize, closeBtnSize);
        bool closeHover = HitTest(_closeButtonBounds, _mousePos);
        DrawRect(cmds, new Vector2(closeBtnX, closeBtnY), new Vector2(closeBtnSize, closeBtnSize),
            closeHover ? new Vector4(0.42f, 0.22f, 0.22f, 1f) : CancelButtonBgColor);
        var xSize = atlas.Measure("X");
        atlas.Emit("X", new Vector2(closeBtnX + (closeBtnSize - xSize.X) * 0.5f, closeBtnY + (closeBtnSize - xSize.Y) * 0.5f), TextColor);

        // Рендер нижней панели ПЕРЕД dropdown, но ПОСЛЕ элементов
        DrawRect(cmds, new Vector2(px, py + PanelHeight - BottomBarHeight),
            new Vector2(PanelWidth, BottomBarHeight), TitleBarColor);

        float btnY = py + PanelHeight - BottomBarHeight + (BottomBarHeight - 36f) * 0.5f;
        float btnW = 130f;
        float btnH = 36f;

        float resetX = px + Padding;
        _resetButtonBounds = new Vector4(resetX, btnY, btnW, btnH);
        bool resetHover = HitTest(_resetButtonBounds, _mousePos);
        DrawTextButton(cmds, atlas, Localization.Get("settings_reset", "Reset"), _resetButtonBounds,
            resetHover ? new Vector4(0.30f, 0.20f, 0.20f, 1f) : CancelButtonBgColor);

        float saveX = px + PanelWidth - btnW - Padding;
        _saveButtonBounds = new Vector4(saveX, btnY, btnW, btnH);
        bool saveHover = HitTest(_saveButtonBounds, _mousePos);
        DrawTextButton(cmds, atlas, Localization.Get("dialog_button_save", "Save"), _saveButtonBounds,
            saveHover ? new Vector4(0.30f, 0.50f, 0.85f, 1f) : ButtonBgColor);

        // Рендер выпадающего списка в самом конце — поверх всего
        if (_openDropdownIndex >= 0 && _openDropdownIndex < _items.Count)
            RenderDropdownList(cmds, atlas, contentTop, inputX, dropdownListY);
    }

    private void RenderTextInputField(List<UICommand> cmds, TextAtlas atlas, int index, float x, float y, SettingItem item)
    {
        bool isActive = _activeFieldIndex == index;
        Vector4 bg = isActive ? InputFocusBgColor : InputBgColor;
        Vector4 outline = isActive ? FocusOutlineColor : OutlineColor;

        DrawRect(cmds, new Vector2(x, y), new Vector2(InputFieldWidth, InputFieldHeight), bg);
        DrawRectOutline(cmds, new Vector2(x, y), new Vector2(InputFieldWidth, InputFieldHeight), outline, isActive ? 2f : 1f);

        if (isActive)
        {
            string text = TextBox.ToString();

            if (TextBox.HasSelection)
            {
                string beforeSel = text.Substring(0, TextBox.SelStart);
                string selText = text.Substring(TextBox.SelStart, TextBox.SelEnd - TextBox.SelStart);
                var beforeSize = atlas.Measure(beforeSel);
                var selSize = atlas.Measure(selText);
                DrawRect(cmds, new Vector2(x + 10f + beforeSize.X, y + 4f),
                    new Vector2(selSize.X, InputFieldHeight - 8f), new Vector4(0.2f, 0.4f, 0.8f, 0.6f));
            }

            var inputSize = atlas.Measure(text);
            float textY = y + (InputFieldHeight - inputSize.Y) * 0.5f;
            atlas.Emit(text, new Vector2(x + 10f, textY), TextColor);

            bool showCursor = ((DateTime.Now - TextBox.LastEditTime).TotalMilliseconds % 1000) < 500;
            if (showCursor)
            {
                string beforeCursor = text.Substring(0, TextBox.CursorPos);
                var beforeSize = atlas.Measure(beforeCursor);
                float cursorX = x + 10f + beforeSize.X + 1f;
                float cursorH = Math.Max(inputSize.Y - 6f, 12f);
                DrawRect(cmds, new Vector2(cursorX, y + (InputFieldHeight - cursorH) * 0.5f),
                    new Vector2(2f, cursorH), new Vector4(0.9f, 0.9f, 0.95f, 1f));
            }
        }
        else
        {
            string val = item.GetValue();
            var valSize = atlas.Measure(val);
            atlas.Emit(val, new Vector2(x + 10f, y + (InputFieldHeight - valSize.Y) * 0.5f), ValueColor);
        }
    }

    private void RenderDropdownField(List<UICommand> cmds, TextAtlas atlas, int index, float x, float y, SettingItem item)
    {
        bool isOpen = _openDropdownIndex == index;
        bool isHover = _mousePos.X >= x && _mousePos.X <= x + InputFieldWidth &&
                       _mousePos.Y >= y && _mousePos.Y <= y + InputFieldHeight;
        Vector4 bg = isOpen ? InputFocusBgColor : (isHover ? new Vector4(0.10f, 0.10f, 0.14f, 1f) : InputBgColor);

        DrawRect(cmds, new Vector2(x, y), new Vector2(InputFieldWidth, InputFieldHeight), bg);
        DrawRectOutline(cmds, new Vector2(x, y), new Vector2(InputFieldWidth, InputFieldHeight), OutlineColor, 1f);

        string val = item.GetValue();
        var valSize = atlas.Measure(val);
        atlas.Emit(val, new Vector2(x + 10f, y + (InputFieldHeight - valSize.Y) * 0.5f), ValueColor);

        string arrow = isOpen ? "\u25B2" : "\u25BC";
        var arrowSize = atlas.Measure(arrow);
        atlas.Emit(arrow, new Vector2(x + InputFieldWidth - arrowSize.X - 10f, y + (InputFieldHeight - arrowSize.Y) * 0.5f), TextColor);
    }

    private void RenderDropdownList(List<UICommand> cmds, TextAtlas atlas, float contentTop, float inputX, float listY)
    {
        var item = _items[_openDropdownIndex];
        float listH = item.Options!.Count * DropdownOptionHeight;

        DrawRect(cmds, new Vector2(inputX, listY), new Vector2(InputFieldWidth, listH), DropdownBgColor);
        DrawRectOutline(cmds, new Vector2(inputX, listY), new Vector2(InputFieldWidth, listH), OutlineColor, 1f);

        _dropdownOptionRegions.Clear();
        string currentVal = item.GetValue();

        for (int i = 0; i < item.Options.Count; i++)
        {
            float optY = listY + i * DropdownOptionHeight;
            bool isHover = _mousePos.X >= inputX && _mousePos.X <= inputX + InputFieldWidth &&
                           _mousePos.Y >= optY && _mousePos.Y <= optY + DropdownOptionHeight;
            bool isSelected = item.Options[i] == currentVal;

            if (isSelected)
                DrawRect(cmds, new Vector2(inputX, optY), new Vector2(InputFieldWidth, DropdownOptionHeight), DropdownSelectedColor);
            else if (isHover)
                DrawRect(cmds, new Vector2(inputX, optY), new Vector2(InputFieldWidth, DropdownOptionHeight), DropdownHoverColor);

            var optSize = atlas.Measure(item.Options[i]);
            atlas.Emit(item.Options[i], new Vector2(inputX + 10f, optY + (DropdownOptionHeight - optSize.Y) * 0.5f), TextColor);

            _dropdownOptionRegions.Add((i, inputX, optY, InputFieldWidth, DropdownOptionHeight));
        }
    }

    private void RenderBoolToggle(List<UICommand> cmds, TextAtlas atlas, float x, float y, SettingItem item)
    {
        bool val = item.GetValue() == "True";
        Vector4 bg = val ? BoolOnColor : BoolOffColor;
        bool isHover = _mousePos.X >= x && _mousePos.X <= x + InputFieldWidth &&
                       _mousePos.Y >= y && _mousePos.Y <= y + InputFieldHeight;

        DrawRect(cmds, new Vector2(x, y), new Vector2(InputFieldWidth, InputFieldHeight),
            isHover ? Vector4.Lerp(bg, Vector4.One, 0.15f) : bg);
        DrawRectOutline(cmds, new Vector2(x, y), new Vector2(InputFieldWidth, InputFieldHeight), OutlineColor, 1f);

        string label = val ? "ON" : "OFF";
        var labelSize = atlas.Measure(label);
        atlas.Emit(label, new Vector2(x + (InputFieldWidth - labelSize.X) * 0.5f, y + (InputFieldHeight - labelSize.Y) * 0.5f), TextColor);
    }

    private void RenderActionButton(List<UICommand> cmds, TextAtlas atlas, float x, float y, SettingItem item)
    {
        bool isHover = _mousePos.X >= x && _mousePos.X <= x + InputFieldWidth &&
                       _mousePos.Y >= y && _mousePos.Y <= y + InputFieldHeight;
        Vector4 bg = isHover ? new Vector4(0.30f, 0.40f, 0.60f, 1f) : new Vector4(0.20f, 0.25f, 0.40f, 1f);

        DrawRect(cmds, new Vector2(x, y), new Vector2(InputFieldWidth, InputFieldHeight), bg);
        DrawRectOutline(cmds, new Vector2(x, y), new Vector2(InputFieldWidth, InputFieldHeight), OutlineColor, 1f);

        var nameSize = atlas.Measure(item.Name);
        atlas.Emit(item.Name, new Vector2(x + (InputFieldWidth - nameSize.X) * 0.5f, y + (InputFieldHeight - nameSize.Y) * 0.5f), TextColor);
    }

    private void DrawTextButton(List<UICommand> cmd, TextAtlas atlas, string text, Vector4 bounds, Vector4 bg)
    {
        DrawRect(cmd, new Vector2(bounds.X, bounds.Y), new Vector2(bounds.Z, bounds.W), bg);
        var size = atlas.Measure(text);
        atlas.Emit(text, new Vector2(bounds.X + (bounds.Z - size.X) * 0.5f, bounds.Y + (bounds.W - size.Y) * 0.5f), TextColor);
    }

    private static void DrawRectOutline(List<UICommand> cmds, Vector2 pos, Vector2 size, Vector4 color, float thickness)
        => cmds.Add(new UICommand { P1P2 = new Vector4(pos.X, pos.Y, size.X, size.Y), Color = color, Params = new Vector4(thickness, 3, 0, 0) });

    private static void DrawRect(List<UICommand> cmds, Vector2 pos, Vector2 size, Vector4 color)
        => cmds.Add(new UICommand { P1P2 = new Vector4(pos.X, pos.Y, size.X, size.Y), Color = color, Params = new Vector4(0, 0, 0, 0) });

    public void Dispose() { }
}