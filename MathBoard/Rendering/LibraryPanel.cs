using System.Numerics;
using MathBoard.Core;

namespace MathBoard.Rendering;

public class LibraryPanel : IDisposable
{
    private const float HeaderHeight = 64f;
    private const float ToolbarHeight = 52f;
    private const float BottomBarHeight = 56f;
    private const float RowHeight = 34f;
    private const float Indent = 18f;
    private const float ActionSize = 28f;
    private const float DragThreshold = 6f;
    private const double AutoExpandSeconds = 0.6;
    private const double HighlightSeconds = 1.4;
    private const string PlaceholderKey = "\0placeholder";

    public bool IsOpen { get; private set; } = true;
    public float Width { get; } = 340f;

    private readonly StrokeRenderer _renderer;
    private readonly LibraryManager _libraryManager;
    private readonly SettingsPanel _settingsPanel;
    private readonly Updater _updater;
    public SettingsPanel SettingsPanel => _settingsPanel;

    private FileNode? _rootNode;
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);
    private float _scrollY;
    private Vector2 _screenSize = new(1280, 720);
    private Vector2 _mousePos = new(-1000);

    public enum DialogMode { None, Delete, Rename, Unsaved, PickLocation, Log }

    /// <summary>What the folder picker (name field + folder tree) is creating.</summary>
    private enum PickPurpose { NewFile, SaveAs, CreateFolder }
    private enum RowAction { Move, Rename, Delete }

    private DialogMode _dialogMode = DialogMode.None;
    private FileNode? _dialogTargetNode;
    private string? _selectedDestDir;
    private PickPurpose _pickPurpose;
    // Runs after the board was saved or discarded in the "unsaved board" prompt.
    private Action? _pendingAction;
    private List<string> _logLines = new();
    private float _logScroll;
    private bool _logDragging;
    private Vector2 _logDragStart;
    private float _logDragScroll;
    private const float LogLineHeight = 18f;

    private readonly TextBoxState _textBox = new()
    {
        Accepts = c => !char.IsControl(c) && Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0
    };

    public bool IsModalOpen => _dialogMode != DialogMode.None || _settingsPanel.IsOpen;
    private bool IsPopupDialog => _dialogMode is DialogMode.Delete or DialogMode.Rename or DialogMode.Unsaved;

    /// <summary>The text field that currently receives keyboard input, if any.</summary>
    public TextBoxState? ActiveTextBox =>
        _settingsPanel.IsOpen ? (_settingsPanel.HasTextInput ? _settingsPanel.TextBox : null)
        : _dialogMode is DialogMode.Rename or DialogMode.PickLocation ? _textBox
        : null;

    // Rows as laid out in the last frame; hit testing uses exactly what was drawn.
    private readonly record struct Row(FileNode? Node, int Depth, float Y, bool IsPlaceholder);
    private readonly List<Row> _rows = new();
    private readonly Dictionary<string, float> _animatedY = new();
    private float _contentHeight;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _lastRenderTime;

    // Pointer interaction inside the list.
    private enum Press { None, Row, Move, Scroll }
    private Press _press;
    private FileNode? _pressNode;
    private Vector2 _pressPos;
    private float _pressScroll;

    private bool _isDragging;
    private FileNode? _dragNode;
    private string? _dropTargetPath;
    private string? _hoverFolderPath;
    private DateTime _hoverFolderSince;

    private string? _highlightPath;
    private DateTime _highlightStart;

    public bool IsDragging => _isDragging;

    public LibraryPanel(StrokeRenderer renderer, LibraryManager libraryManager, Updater updater)
    {
        _renderer = renderer;
        _libraryManager = libraryManager;
        _updater = updater;
        _settingsPanel = new SettingsPanel(renderer, updater);
        _settingsPanel.OnOpenLog = OpenLogDialog;
        RefreshTree();
    }

    public void Toggle()
    {
        IsOpen = !IsOpen;
        CancelDrag();
        _renderer.SetDirty();
    }

    public void RefreshTree()
    {
        _rootNode = BuildTree(Settings.LibraryRootPath.Value);
        _renderer.SetDirty();
    }

    private FileNode BuildTree(string path)
    {
        var root = new FileNode(Path.GetFileName(path), path, true) { IsExpanded = !_collapsed.Contains(path) };
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            return root;
        }

        // Folders starting with a dot hold app data, such as the pictures of the lessons.
        foreach (var dir in Directory.GetDirectories(path).Where(d => !Path.GetFileName(d).StartsWith('.')))
            root.Children.Add(BuildTree(dir));

        foreach (var file in Directory.GetFiles(path, "*.mathboard"))
            root.Children.Add(new FileNode(Path.GetFileNameWithoutExtension(file), file, false));

        root.Children.Sort(CompareNodes);
        return root;
    }

    private static int CompareNodes(FileNode a, FileNode b)
    {
        int dirCmp = b.IsDirectory.CompareTo(a.IsDirectory);
        return dirCmp != 0 ? dirCmp : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    private void SetExpanded(FileNode node, bool expanded)
    {
        node.IsExpanded = expanded;
        if (expanded) _collapsed.Remove(node.FullPath);
        else _collapsed.Add(node.FullPath);
        _renderer.SetDirty();
    }

    // ------------------------------------------------------------- layout

    private float ListTop => _dialogMode == DialogMode.PickLocation ? HeaderHeight + 64f : HeaderHeight + ToolbarHeight;
    private float ListBottom => _screenSize.Y - BottomBarHeight;
    private float ListHeight => MathF.Max(0f, ListBottom - ListTop);

    private Vector2 ToolbarButtonPos(int index) => new(12f + index * 44f, HeaderHeight + 8f);
    private static readonly Vector2 ToolbarButtonSize = new(36f);
    private Vector2 CollapseButtonPos => new(Width - 44f, (HeaderHeight - 32f) * 0.5f);
    private Vector2 BottomButtonPos => new(12f, _screenSize.Y - BottomBarHeight + 10f);
    private Vector2 BottomButtonSize => new(Width - 24f - 44f, 36f);
    private Vector2 ThemeButtonPos => new(Width - 12f - 36f, BottomButtonPos.Y);

    /// <summary>Target row positions; while dragging, a placeholder is inserted where the item would land.</summary>
    private List<Row> LayoutRows()
    {
        var rows = new List<Row>();
        if (_rootNode == null) return rows;

        bool foldersOnly = _dialogMode == DialogMode.PickLocation;
        FileNode? placeholderParent = null;
        int placeholderIndex = -1;
        if (_isDragging && _dragNode != null && _dropTargetPath != null)
        {
            placeholderParent = FindNode(_rootNode, _dropTargetPath);
            if (placeholderParent is { IsExpanded: true })
            {
                placeholderIndex = placeholderParent.Children.Count(c => CompareNodes(c, _dragNode) < 0);
            }
        }

        float y = 0;
        void Visit(FileNode node, int depth)
        {
            rows.Add(new Row(node, depth, y, false));
            y += RowHeight;
            if (!node.IsDirectory || !node.IsExpanded) return;

            for (int i = 0; i <= node.Children.Count; i++)
            {
                if (node == placeholderParent && i == placeholderIndex)
                {
                    rows.Add(new Row(null, depth + 1, y, true));
                    y += RowHeight;
                }

                if (i == node.Children.Count) break;
                var child = node.Children[i];
                if (foldersOnly && !child.IsDirectory) continue;
                Visit(child, depth + 1);
            }
        }

        Visit(_rootNode, 0);
        _contentHeight = y;
        return rows;
    }

    private static FileNode? FindNode(FileNode node, string path)
    {
        if (string.Equals(node.FullPath, path, StringComparison.OrdinalIgnoreCase)) return node;
        foreach (var child in node.Children)
            if (child.IsDirectory && FindNode(child, path) is { } found)
                return found;
        return null;
    }

    private static string KeyOf(Row row) => row.IsPlaceholder ? PlaceholderKey : row.Node!.FullPath;

    /// <summary>Drives time-based behaviour that is not tied to input events.</summary>
    public void Update(float dt)
    {
        if (_isDragging)
        {
            // Auto-scroll near the list edges and auto-expand a hovered collapsed folder.
            const float edge = 32f;
            float speed = _mousePos.Y < ListTop + edge ? -1f : _mousePos.Y > ListBottom - edge ? 1f : 0f;
            if (speed != 0f && _mousePos.X <= Width)
            {
                ScrollBy(speed * 500f * dt);
                UpdateDropTarget(_mousePos);
            }

            if (_hoverFolderPath != null && _rootNode != null &&
                FindNode(_rootNode, _hoverFolderPath) is { IsExpanded: false } folder &&
                (DateTime.Now - _hoverFolderSince).TotalSeconds >= AutoExpandSeconds)
            {
                SetExpanded(folder, true);
            }
        }

        if (_highlightPath != null)
        {
            if ((DateTime.Now - _highlightStart).TotalSeconds > HighlightSeconds) _highlightPath = null;
            _renderer.SetDirty();
        }
    }

    /// <summary>True while <see cref="Update"/> needs to run at frame rate.</summary>
    public bool NeedsUpdates => _isDragging || _highlightPath != null;

    private void ScrollBy(float delta)
    {
        _scrollY = Math.Clamp(_scrollY + delta, 0f, MathF.Max(0f, _contentHeight - ListHeight + 8f));
        _renderer.SetDirty();
    }

    // ------------------------------------------------------------- rendering

    public void Render(UiBatch ui, Vector2 screenSize)
    {
        _screenSize = screenSize;
        if (!IsOpen) return;

        ui.Rect(Vector2.Zero, new Vector2(Width, screenSize.Y), Theme.PanelBg);
        ui.Rect(new Vector2(Width - 1f, 0), new Vector2(1f, screenSize.Y), Theme.Border);
        RenderHeader(ui);

        switch (_dialogMode)
        {
            case DialogMode.PickLocation:
                RenderLocationPicker(ui);
                break;
            default:
                RenderToolbar(ui);
                RenderList(ui);
                RenderBottomBar(ui, Icons.Settings, Localization.Get("tooltip_settings", "Settings"));
                break;
        }
    }

    private void RenderHeader(UiBatch ui)
    {
        ui.Rect(Vector2.Zero, new Vector2(Width - 1f, HeaderHeight), Theme.HeaderBg);

        string title, subtitle;
        bool dirty = false;
        switch (_dialogMode)
        {
            case DialogMode.PickLocation:
                title = _pickPurpose switch
                {
                    PickPurpose.NewFile => Localization.Get("dialog_new_file_title", "New file"),
                    PickPurpose.SaveAs => Localization.Get("panel_save_as", "Save As..."),
                    _ => Localization.Get("dialog_create_folder_title", "Create Folder")
                };
                subtitle = Localization.Get("dialog_choose_location", "Choose a name and a folder");
                break;
            default:
                title = _libraryManager.CurrentName ?? Localization.Get("canvas_untitled", "Untitled");
                dirty = _libraryManager.IsDocumentDirty();
                subtitle = _libraryManager.CurrentPath is { } path
                    ? RelativeFolder(path)
                    : Localization.Get("canvas_not_saved", "Not saved yet");
                if (dirty) subtitle = Localization.Get("canvas_unsaved_changes", "Unsaved changes") + " · " + subtitle;
                break;
        }

        float left = 16f;
        float right = _dialogMode == DialogMode.None ? CollapseButtonPos.X - 8f : Width - 16f;
        if (dirty)
        {
            ui.Circle(new Vector2(left + 4f, 22f), 4f, Theme.Accent);
            left += 14f;
        }

        if (_dialogMode == DialogMode.None && Hit(TitleRect.Pos, TitleRect.Size))
        {
            // The title is the canvas name and can be clicked to rename it.
            ui.Rect(TitleRect.Pos, TitleRect.Size, Theme.RowHover, 6f);
            float titleWidth = MathF.Min(ui.Measure(title, Theme.Title), right - left - 22f);
            ui.Icon(Icons.Edit, new Vector2(left + titleWidth + 12f, 22f), 12f, Theme.TextSecondary);
            right -= 22f;
        }

        ui.TextInRect(title, new Vector2(left, 10f), new Vector2(right - left, 24f), Theme.Text, Theme.Title);
        ui.TextInRect(subtitle, new Vector2(16f, 34f), new Vector2(right - 16f, 20f), Theme.TextSecondary, Theme.Small);

        if (_dialogMode == DialogMode.None)
        {
            var pos = CollapseButtonPos;
            ui.IconButton(Icons.ChevronLeft, pos, new Vector2(32f), Hit(pos, new Vector2(32f)), iconSize: 14f);
        }
    }

    private (Vector2 Pos, Vector2 Size) TitleRect => (new Vector2(8f, 6f), new Vector2(CollapseButtonPos.X - 16f, 52f));

    private static string RelativeFolder(string filePath)
    {
        string root = Settings.LibraryRootPath.Value;
        string dir = Path.GetDirectoryName(filePath) ?? root;
        string rel = Path.GetRelativePath(root, dir);
        string rootName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
        return rel == "." ? rootName : rootName + " / " + rel.Replace(Path.DirectorySeparatorChar.ToString(), " / ");
    }

    private void RenderToolbar(UiBatch ui)
    {
        string[] icons = [Icons.Save, Icons.NewFolder, Icons.Add];
        for (int i = 0; i < icons.Length; i++)
        {
            var pos = ToolbarButtonPos(i);
            bool hover = !IsModalOpen && Hit(pos, ToolbarButtonSize);
            ui.IconButton(icons[i], pos, ToolbarButtonSize, hover, UiBatch.ButtonKind.Secondary, 16f);
        }

        ui.Rect(new Vector2(12f, HeaderHeight + ToolbarHeight - 1f), new Vector2(Width - 24f, 1f), Theme.Border);
    }

    private void RenderBottomBar(UiBatch ui, string icon, string label)
    {
        ui.Rect(new Vector2(12f, ListBottom), new Vector2(Width - 24f, 1f), Theme.Border);
        var pos = BottomButtonPos;
        bool hover = !IsModalOpen && Hit(pos, BottomButtonSize);
        ui.ButtonBackground(pos, BottomButtonSize, hover, UiBatch.ButtonKind.Ghost);
        ui.Icon(icon, pos + new Vector2(20f, BottomButtonSize.Y * 0.5f), 16f, Theme.TextSecondary);
        if (_updater.Status == Updater.State.Available)
            ui.Circle(pos + new Vector2(28f, BottomButtonSize.Y * 0.5f - 8f), 4f, Theme.Accent);
        ui.TextInRect(label, pos + new Vector2(40f, 0), new Vector2(BottomButtonSize.X - 48f, BottomButtonSize.Y),
            Theme.Text, Theme.Body);

        // Shows the current theme: the moon in the dark one, the sun in the light one.
        ui.IconButton(Settings.LightTheme ? Icons.Sun : Icons.Moon, ThemeButtonPos, new Vector2(36f),
            !IsModalOpen && Hit(ThemeButtonPos, new Vector2(36f)), iconSize: 16f);
    }

    private void RenderList(UiBatch ui)
    {
        var rows = LayoutRows();
        _scrollY = Math.Clamp(_scrollY, 0f, MathF.Max(0f, _contentHeight - ListHeight + 8f));
        float top = ListTop + 4f - _scrollY;

        // Rows glide towards their slots, e.g. to make room for the drop placeholder.
        double now = _clock.Elapsed.TotalSeconds;
        float k = 1f - MathF.Exp(-(float)Math.Min(now - _lastRenderTime, 0.05) * 16f);
        _lastRenderTime = now;
        bool moving = false;

        FileNode? targetNode = _dropTargetPath != null && _rootNode != null ? FindNode(_rootNode, _dropTargetPath) : null;
        float targetTop = 0f, targetBottom = 0f;
        int targetDepth = -1;
        bool insideTarget = false;

        _rows.Clear();
        foreach (var row in rows)
        {
            string key = KeyOf(row);
            float animY = _animatedY.TryGetValue(key, out float prev) ? prev + (row.Y - prev) * k : row.Y;
            if (MathF.Abs(animY - row.Y) < 0.5f) animY = row.Y;
            else moving = true;
            _animatedY[key] = animY;

            float y = top + animY;
            _rows.Add(row with { Y = y });

            if (row.Node != null && row.Node == targetNode)
            {
                insideTarget = true;
                targetDepth = row.Depth;
                targetTop = y;
                targetBottom = y + RowHeight;
            }
            else if (insideTarget)
            {
                if (row.Depth > targetDepth) targetBottom = y + RowHeight;
                else insideTarget = false;
            }
        }

        if (moving) _renderer.SetDirty();
        ui.PushClip(new Vector2(0, ListTop), new Vector2(Width - 1f, ListHeight));

        if (targetDepth >= 0)
        {
            // Bracket the whole destination folder so it is obvious where the item goes.
            var size = new Vector2(Width - 9f, targetBottom - targetTop);
            ui.Rect(new Vector2(4f, targetTop), size, Theme.WithAlpha(Theme.Accent, 0.08f), 8f);
            ui.Border(new Vector2(4f, targetTop), size, Theme.WithAlpha(Theme.Accent, 0.6f), 1.5f, 8f);
        }

        foreach (var row in _rows)
        {
            if (row.Y + RowHeight < ListTop || row.Y > ListBottom) continue;
            if (row.IsPlaceholder) RenderPlaceholder(ui, row);
            else RenderRow(ui, row, row.Node == targetNode);
        }

        ui.PopClip();
        if (_animatedY.Count > rows.Count * 2) StaleAnimationCleanup(rows);
    }

    private void StaleAnimationCleanup(List<Row> rows)
    {
        var live = rows.Select(KeyOf).ToHashSet();
        foreach (var key in _animatedY.Keys.Where(k => !live.Contains(k)).ToList())
            _animatedY.Remove(key);
    }

    private void RenderRow(UiBatch ui, Row row, bool isDropTarget)
    {
        var node = row.Node!;
        var pos = new Vector2(4f, row.Y);
        var size = new Vector2(Width - 9f, RowHeight);
        bool hover = !IsModalOpen && !_isDragging && _press != Press.Scroll && Hit(pos, size);
        bool isCurrent = !node.IsDirectory && string.Equals(node.FullPath, _libraryManager.CurrentPath, StringComparison.OrdinalIgnoreCase);
        bool isDragged = _isDragging && node == _dragNode;
        bool isSelectedDest = _dialogMode == DialogMode.PickLocation && node.FullPath == _selectedDestDir;

        if (isSelectedDest || isCurrent) ui.Rect(pos, size, Theme.AccentSoft, 6f);
        else if (hover) ui.Rect(pos, size, Theme.RowHover, 6f);
        if (isCurrent) ui.Rect(new Vector2(4f, row.Y + 8f), new Vector2(3f, RowHeight - 16f), Theme.Accent, 1.5f);

        if (_highlightPath != null && string.Equals(node.FullPath, _highlightPath, StringComparison.OrdinalIgnoreCase))
        {
            float t = (float)((DateTime.Now - _highlightStart).TotalSeconds / HighlightSeconds);
            ui.Rect(pos, size, Theme.WithAlpha(Theme.Accent, 0.35f * (1f - Math.Clamp(t, 0f, 1f))), 6f);
        }

        float alpha = isDragged ? 0.35f : 1f;
        float x = 12f + row.Depth * Indent;
        float cy = row.Y + RowHeight * 0.5f;

        if (node.IsDirectory)
        {
            ui.Icon(node.IsExpanded ? Icons.ChevronDown : Icons.ChevronRight, new Vector2(x + 7f, cy), 10f,
                Theme.WithAlpha(Theme.TextSecondary, alpha));
            ui.Icon(node.IsExpanded ? Icons.FolderOpen : Icons.Folder, new Vector2(x + 27f, cy), 16f,
                Theme.WithAlpha(isDropTarget ? Theme.Accent : Theme.Text, alpha));
        }
        else
        {
            ui.Icon(Icons.Document, new Vector2(x + 27f, cy), 15f, Theme.WithAlpha(Theme.TextSecondary, alpha));
        }

        bool hasActions = node != _rootNode && _dialogMode == DialogMode.None;
        float nameRight = hasActions ? Width - 9f - 3 * (ActionSize + 2f) - 6f : Width - 16f;
        ui.TextInRect(node.Name, new Vector2(x + 42f, row.Y), new Vector2(nameRight - x - 42f, RowHeight),
            Theme.WithAlpha(Theme.Text, alpha), Theme.Body);

        if (!hasActions || isDragged) return;
        for (int i = 0; i < 3; i++)
        {
            var (bPos, bSize) = ActionRect(row, (RowAction)i);
            bool bHover = !IsModalOpen && !_isDragging && Hit(bPos, bSize);
            var icon = (RowAction)i switch { RowAction.Move => Icons.Gripper, RowAction.Rename => Icons.Edit, _ => Icons.Delete };
            var color = bHover ? ((RowAction)i == RowAction.Delete ? Theme.Danger : Theme.Text)
                : hover ? Theme.TextSecondary : Theme.TextMuted;
            ui.IconButton(icon, bPos, bSize, bHover, iconSize: 14f, iconColor: color);
        }
    }

    private (Vector2 Pos, Vector2 Size) ActionRect(Row row, RowAction action)
    {
        // Order from the right edge: delete, rename, move.
        int fromRight = action switch { RowAction.Delete => 0, RowAction.Rename => 1, _ => 2 };
        float x = Width - 9f - (fromRight + 1) * (ActionSize + 2f);
        return (new Vector2(x, row.Y + (RowHeight - ActionSize) * 0.5f), new Vector2(ActionSize));
    }

    private void RenderPlaceholder(UiBatch ui, Row row)
    {
        var pos = new Vector2(8f + row.Depth * Indent, row.Y + 3f);
        var size = new Vector2(Width - 13f - row.Depth * Indent, RowHeight - 6f);
        ui.Rect(pos, size, Theme.AccentSoft, 6f);
        ui.Border(pos, size, Theme.Accent, 1.5f, 6f);
        float x = 12f + row.Depth * Indent;
        ui.Icon(_dragNode!.IsDirectory ? Icons.Folder : Icons.Document, new Vector2(x + 27f, row.Y + RowHeight * 0.5f), 15f, Theme.Accent);
        ui.TextInRect(_dragNode.Name, new Vector2(x + 42f, row.Y), new Vector2(Width - x - 58f, RowHeight), Theme.Accent, Theme.Body);
    }

    private void RenderLocationPicker(UiBatch ui)
    {
        ui.TextField(_textBox, new Vector2(12f, HeaderHeight + 14f), new Vector2(Width - 24f, 38f), true, Theme.Body, Theme.Text);
        RenderList(ui);

        ui.Rect(new Vector2(12f, ListBottom), new Vector2(Width - 24f, 1f), Theme.Border);
        var (cancelPos, okPos, btnSize) = PanelDialogButtons();
        string confirm = _pickPurpose == PickPurpose.SaveAs
            ? Localization.Get("dialog_button_save", "Save")
            : Localization.Get("dialog_button_create", "Create");
        ui.TextButton(Localization.Get("dialog_button_cancel", "Cancel"), cancelPos, btnSize, Hit(cancelPos, btnSize), UiBatch.ButtonKind.Secondary);
        ui.TextButton(confirm, okPos, btnSize, Hit(okPos, btnSize), UiBatch.ButtonKind.Primary);
    }

    private (Vector2 Cancel, Vector2 Ok, Vector2 Size) PanelDialogButtons()
    {
        var size = new Vector2((Width - 36f) * 0.5f, 36f);
        float y = _screenSize.Y - BottomBarHeight + 10f;
        return (new Vector2(12f, y), new Vector2(24f + size.X, y), size);
    }

    private (Vector2 Pos, Vector2 Size) LogRect()
    {
        var size = new Vector2(MathF.Min(960f, _screenSize.X - 48f), MathF.Max(200f, _screenSize.Y - 64f));
        return ((_screenSize - size) * 0.5f, size);
    }

    private (Vector2 Close, Vector2 Folder, Vector2 Size) LogButtons()
    {
        var (pos, size) = LogRect();
        var btn = new Vector2(150f, 38f);
        float y = pos.Y + size.Y - 16f - btn.Y;
        return (new Vector2(pos.X + size.X - 20f - btn.X, y), new Vector2(pos.X + 20f, y), btn);
    }

    private (Vector2 Pos, Vector2 Size) LogTextRect()
    {
        var (pos, size) = LogRect();
        return (pos + new Vector2(20f, 64f), new Vector2(size.X - 40f, size.Y - 64f - 70f));
    }

    private float MaxLogScroll => MathF.Max(0f, _logLines.Count * LogLineHeight - LogTextRect().Size.Y);

    private void RenderLogPopup(UiBatch ui)
    {
        ui.Rect(Vector2.Zero, _screenSize, Theme.Overlay);
        var (pos, size) = LogRect();
        ui.Rect(pos, size, Theme.PopupBg, Theme.PopupRadius);
        ui.Border(pos, size, Theme.Border, 1f, Theme.PopupRadius);
        ui.TextInRect(Localization.Get("tooltip_open_log", "Latest Log"), pos + new Vector2(20f, 12f),
            new Vector2(size.X - 40f, 26f), Theme.Text, Theme.Title);
        ui.TextInRect(LogPath, pos + new Vector2(20f, 36f), new Vector2(size.X - 40f, 20f), Theme.TextMuted, Theme.Small);

        var (textPos, textSize) = LogTextRect();
        _logScroll = Math.Clamp(_logScroll, 0f, MaxLogScroll);
        ui.Rect(textPos, textSize, Theme.Field, 8f);
        ui.PushClip(textPos + new Vector2(0, 4f), textSize - new Vector2(0, 8f));
        int first = (int)(_logScroll / LogLineHeight);
        for (int i = first; i < _logLines.Count; i++)
        {
            float y = textPos.Y + 6f + i * LogLineHeight - _logScroll;
            if (y > textPos.Y + textSize.Y) break;
            ui.TextInRect(_logLines[i], new Vector2(textPos.X + 12f, y), new Vector2(textSize.X - 30f, LogLineHeight),
                Theme.TextSecondary, Theme.Small);
        }

        ui.PopClip();
        if (MaxLogScroll > 0)
        {
            float h = MathF.Max(36f, textSize.Y * textSize.Y / (textSize.Y + MaxLogScroll));
            float y = textPos.Y + _logScroll / MaxLogScroll * (textSize.Y - h);
            ui.Rect(new Vector2(textPos.X + textSize.X - 10f, y), new Vector2(6f, h), Theme.Border, 3f);
        }

        var (closePos, folderPos, btnSize) = LogButtons();
        ui.TextButton(Localization.Get("log_open_folder", "Show file"), folderPos, btnSize, Hit(folderPos, btnSize), UiBatch.ButtonKind.Secondary);
        ui.TextButton(Localization.Get("dialog_button_close", "Close"), closePos, btnSize, Hit(closePos, btnSize), UiBatch.ButtonKind.Primary);
    }

    public void RenderOverlay(UiBatch ui, Vector2 screenSize)
    {
        _screenSize = screenSize;

        if (IsPopupDialog) RenderPopupDialog(ui);
        if (_dialogMode == DialogMode.Log) RenderLogPopup(ui);
        if (_settingsPanel.IsOpen) _settingsPanel.Render(ui, screenSize);
        if (_isDragging && _dragNode != null) RenderDragCard(ui);

        if (!IsOpen || IsModalOpen || _isDragging || _press != Press.None) return;
        string? tooltip = null;
        if (Hit(ToolbarButtonPos(0), ToolbarButtonSize)) tooltip = Localization.Get("panel_save_as", "Save As...");
        else if (Hit(ToolbarButtonPos(1), ToolbarButtonSize)) tooltip = Localization.Get("tooltip_create_folder", "Create Folder");
        else if (Hit(ToolbarButtonPos(2), ToolbarButtonSize)) tooltip = Localization.Get("tooltip_new_file", "New File");
        else if (Hit(CollapseButtonPos, new Vector2(32f))) tooltip = Localization.Get("tooltip_hide_panel", "Hide panel");
        else if (Hit(TitleRect.Pos, TitleRect.Size)) tooltip = Localization.Get("tooltip_rename_canvas", "Rename");
        if (tooltip != null) ui.Tooltip(tooltip, _mousePos, screenSize);
    }

    private void RenderDragCard(UiBatch ui)
    {
        var node = _dragNode!;
        string hint = _dropTargetPath != null
            ? Localization.Get("drag_move_to", "Move to") + ": " + Path.GetFileName(_dropTargetPath)
            : DropRejectReason(node);

        float w = MathF.Min(260f, MathF.Max(ui.Measure(node.Name, Theme.Body), ui.Measure(hint, Theme.Small)) + 56f);
        var pos = _mousePos + new Vector2(14f, -20f);
        var size = new Vector2(w, 52f);
        ui.Rect(pos + new Vector2(0, 3f), size, new Vector4(0, 0, 0, 0.35f), 10f);
        ui.Rect(pos, size, Theme.PopupBg, 10f);
        ui.Border(pos, size, _dropTargetPath != null ? Theme.Accent : Theme.Border, 1.5f, 10f);
        ui.Icon(node.IsDirectory ? Icons.Folder : Icons.Document, pos + new Vector2(22f, 18f), 16f, Theme.Text);
        ui.TextInRect(node.Name, pos + new Vector2(40f, 6f), new Vector2(w - 50f, 24f), Theme.Text, Theme.Body);
        ui.TextInRect(hint, pos + new Vector2(40f, 27f), new Vector2(w - 50f, 18f),
            _dropTargetPath != null ? Theme.Accent : Theme.TextMuted, Theme.Small);
    }

    private string DropRejectReason(FileNode node)
    {
        if (_hoverFolderPath == null) return Localization.Get("drag_no_target", "Drop onto a folder");
        if (string.Equals(Path.GetDirectoryName(node.FullPath), _hoverFolderPath, StringComparison.OrdinalIgnoreCase))
            return Localization.Get("drag_already_here", "Already in this folder");
        if (node.IsDirectory && (string.Equals(node.FullPath, _hoverFolderPath, StringComparison.OrdinalIgnoreCase) ||
                                 _hoverFolderPath.StartsWith(node.FullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            return Localization.Get("drag_into_itself", "Can't move a folder into itself");
        return Localization.Get("drag_name_taken", "An item with this name is already there");
    }

    private (Vector2 Pos, Vector2 Size) PopupRect()
    {
        float w = MathF.Min(420f, _screenSize.X - 32f);
        const float h = 186f;
        return (new Vector2((_screenSize.X - w) * 0.5f, (_screenSize.Y - h) * 0.5f), new Vector2(w, h));
    }

    private List<(string Label, UiBatch.ButtonKind Kind, Action Click)> PopupActions()
    {
        var cancel = (Localization.Get("dialog_button_cancel", "Cancel"), UiBatch.ButtonKind.Secondary, (Action)CancelDialog);
        return _dialogMode switch
        {
            DialogMode.Delete => [cancel, (Localization.Get("dialog_button_delete", "Delete"), UiBatch.ButtonKind.Danger, ConfirmDialog)],
            DialogMode.Unsaved =>
            [
                cancel,
                (Localization.Get("dialog_button_dont_save", "Don't save"), UiBatch.ButtonKind.Secondary, DiscardAndContinue),
                (Localization.Get("dialog_button_save", "Save"), UiBatch.ButtonKind.Primary, SaveAndContinue)
            ],
            _ => [cancel, (Localization.Get("dialog_button_ok", "OK"), UiBatch.ButtonKind.Primary, ConfirmDialog)]
        };
    }

    private (Vector2 Pos, Vector2 Size) PopupButton(int index, int count)
    {
        var (pos, size) = PopupRect();
        var btn = new Vector2(MathF.Min(120f, (size.X - 40f - (count - 1) * 10f) / count), 38f);
        float x = pos.X + size.X - 20f - (count - index) * btn.X - (count - 1 - index) * 10f;
        return (new Vector2(x, pos.Y + size.Y - 20f - btn.Y), btn);
    }

    private void RenderPopupDialog(UiBatch ui)
    {
        ui.Rect(Vector2.Zero, _screenSize, Theme.Overlay);
        var (pos, size) = PopupRect();
        ui.Rect(pos, size, Theme.PopupBg, Theme.PopupRadius);
        ui.Border(pos, size, Theme.Border, 1f, Theme.PopupRadius);

        string title = _dialogMode switch
        {
            DialogMode.Rename => Localization.Get("dialog_rename_title", "Enter new name:"),
            DialogMode.Delete => Localization.Get("dialog_delete_title", "Delete?"),
            _ => Localization.Get("dialog_unsaved_title", "This board is not saved")
        };

        var inner = new Vector2(size.X - 40f, 0);
        ui.TextInRect(title, pos + new Vector2(20f, 18f), inner with { Y = 26f }, Theme.Text, Theme.Title);

        var bodyPos = pos + new Vector2(20f, 62f);
        switch (_dialogMode)
        {
            case DialogMode.Rename:
                ui.TextField(_textBox, bodyPos, inner with { Y = 40f }, true, Theme.Body, Theme.Text);
                break;
            case DialogMode.Delete when _dialogTargetNode != null:
                ui.Icon(_dialogTargetNode.IsDirectory ? Icons.Folder : Icons.Document, bodyPos + new Vector2(10f, 20f), 16f, Theme.Danger);
                ui.TextInRect(_dialogTargetNode.Name, bodyPos + new Vector2(28f, 0), new Vector2(inner.X - 28f, 40f), Theme.Text, Theme.Body);
                break;
            case DialogMode.Unsaved:
                ui.TextInRect(Localization.Get("dialog_unsaved_message", "Save it before continuing?"), bodyPos,
                    inner with { Y = 40f }, Theme.TextSecondary, Theme.Body);
                break;
        }

        var actions = PopupActions();
        for (int i = 0; i < actions.Count; i++)
        {
            var (bPos, bSize) = PopupButton(i, actions.Count);
            ui.TextButton(actions[i].Label, bPos, bSize, Hit(bPos, bSize), actions[i].Kind);
        }
    }

    private bool Hit(Vector2 pos, Vector2 size) => UiBatch.Contains(pos, size, _mousePos);

    // ------------------------------------------------------------- dialogs

    private void OpenDialog(DialogMode mode, FileNode? target, string text)
    {
        CancelDrag();
        _dialogMode = mode;
        _dialogTargetNode = target;
        _textBox.SetText(text, selectAll: true);
        _renderer.SetDirty();
    }

    public void OpenRenameDialog(FileNode node) => OpenDialog(DialogMode.Rename, node, node.Name);

    public void OpenDeleteDialog(FileNode node) => OpenDialog(DialogMode.Delete, node, "");

    private void OpenPicker(PickPurpose purpose, string name)
    {
        OpenDialog(DialogMode.PickLocation, null, name);
        _pickPurpose = purpose;
        _selectedDestDir = _libraryManager.CurrentPath is { } current
            ? Path.GetDirectoryName(current)
            : Settings.LibraryRootPath.Value;
        IsOpen = true;
        _scrollY = 0;
    }

    private static string NewLessonName() => $"Lesson_{DateTime.Now:yyyy-MM-dd_HH-mm}";

    public void OpenSaveAsDialog() => OpenPicker(PickPurpose.SaveAs, _libraryManager.CurrentName ?? NewLessonName());

    public void OpenCreateFolderDialog() => OpenPicker(PickPurpose.CreateFolder, $"NewFolder_{DateTime.Now:HHmm}");

    /// <summary>Asks for a name and a folder and creates the file right away, even while it is empty.</summary>
    public void RequestNewFile() => LeaveCurrentBoard(() => OpenPicker(PickPurpose.NewFile, NewLessonName()));

    public void RequestOpen(string path) => LeaveCurrentBoard(() => _libraryManager.LoadFile(path));

    /// <summary>Renames the open file, or asks where to save the board if it has no file yet.</summary>
    public void RenameCurrent()
    {
        if (_libraryManager.CurrentPath is { } path)
            OpenRenameDialog(new FileNode(Path.GetFileNameWithoutExtension(path), path, false));
        else
            OpenSaveAsDialog();
    }

    /// <summary>
    /// Runs <paramref name="action"/> once the current board is safe: a board with a file is saved silently,
    /// an unsaved board with drawings asks first.
    /// </summary>
    private void LeaveCurrentBoard(Action action)
    {
        if (_libraryManager.NeedsSavePrompt)
        {
            OpenDialog(DialogMode.Unsaved, null, "");
            _pendingAction = action;
            return;
        }

        _libraryManager.SaveIfHasFile();
        action();
        RefreshTree();
    }

    private void SaveAndContinue()
    {
        var pending = _pendingAction;
        CloseDialog();
        OpenSaveAsDialog();
        _pendingAction = pending;
    }

    private void DiscardAndContinue()
    {
        var pending = _pendingAction;
        CloseDialog();
        pending?.Invoke();
        RefreshTree();
    }

    public void OpenSettingsDialog()
    {
        CancelDrag();
        _settingsPanel.Open();
    }

    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MathBoard", "latest.log");

    public void OpenLogDialog()
    {
        _dialogMode = DialogMode.Log;
        _logLines.Clear();
        try
        {
            // The log is still open for writing by this process.
            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            _logLines = lines.Skip(Math.Max(0, lines.Length - 500)).Select(l => l.TrimEnd('\r')).ToList();
        }
        catch (Exception ex)
        {
            _logLines.Add($"Error reading log: {ex.Message}");
        }

        _logScroll = float.MaxValue;
        _renderer.SetDirty();
    }

    public void ConfirmDialog()
    {
        if (_settingsPanel.IsOpen)
        {
            if (_settingsPanel.HasTextInput) _settingsPanel.ConfirmEdit();
            else _settingsPanel.Close();
            return;
        }

        string text = _textBox.ToString().Trim();
        Action? continuation = null;
        switch (_dialogMode)
        {
            case DialogMode.Delete when _dialogTargetNode != null:
                if (_dialogTargetNode.IsDirectory) _libraryManager.DeleteDirectory(_dialogTargetNode.FullPath);
                else _libraryManager.DeleteFile(_dialogTargetNode.FullPath);
                break;
            case DialogMode.Rename when _dialogTargetNode != null && text.Length > 0:
                if (_dialogTargetNode.IsDirectory) _libraryManager.RenameDirectory(_dialogTargetNode.FullPath, text);
                else _libraryManager.RenameFile(_dialogTargetNode.FullPath, text);
                break;
            case DialogMode.PickLocation when _selectedDestDir != null && text.Length > 0:
                switch (_pickPurpose)
                {
                    case PickPurpose.NewFile:
                        _libraryManager.CreateFile(_selectedDestDir, text);
                        break;
                    case PickPurpose.SaveAs:
                        _libraryManager.SaveAs(_selectedDestDir, text);
                        continuation = _pendingAction;
                        break;
                    default:
                        _libraryManager.CreateFolder(_selectedDestDir, text);
                        break;
                }

                _collapsed.Remove(_selectedDestDir);
                break;
            case DialogMode.Rename or DialogMode.PickLocation:
                return;
        }

        CloseDialog();
        continuation?.Invoke();
        RefreshTree();
    }

    public void CancelDialog()
    {
        if (_settingsPanel.IsOpen)
        {
            if (_settingsPanel.HasTextInput) _settingsPanel.CancelEdit();
            else _settingsPanel.Cancel();
            return;
        }

        CloseDialog();
    }

    private void CloseDialog()
    {
        _dialogMode = DialogMode.None;
        _dialogTargetNode = null;
        _selectedDestDir = null;
        _pendingAction = null;
        _renderer.SetDirty();
    }

    public void HandleCharInput(char c)
    {
        if (_settingsPanel.IsOpen) _settingsPanel.HandleCharInput(c);
        else ActiveTextBox?.Insert(c);
        _renderer.SetDirty();
    }

    // ------------------------------------------------------------- pointer input

    /// <summary>Returns true if the point is covered by the panel or one of its modal dialogs.</summary>
    public bool Contains(Vector2 pos) => IsModalOpen || (IsOpen && pos.X < Width);

    public void HandleMouseDown(Vector2 pos)
    {
        _mousePos = pos;
        _renderer.SetDirty();

        if (_settingsPanel.IsOpen)
        {
            _settingsPanel.HandleMouseDown(pos);
            return;
        }

        if (IsPopupDialog)
        {
            var actions = PopupActions();
            for (int i = 0; i < actions.Count; i++)
            {
                var (bPos, bSize) = PopupButton(i, actions.Count);
                if (!UiBatch.Contains(bPos, bSize, pos)) continue;
                actions[i].Click();
                break;
            }

            return;
        }

        if (_dialogMode == DialogMode.Log)
        {
            var (closePos, folderPos, btnSize) = LogButtons();
            if (UiBatch.Contains(closePos, btnSize, pos)) CancelDialog();
            else if (UiBatch.Contains(folderPos, btnSize, pos)) ShowLogFile();
            else
            {
                _logDragging = true;
                _logDragStart = pos;
                _logDragScroll = _logScroll;
            }

            return;
        }

        if (!IsOpen || pos.X >= Width) return;

        if (_dialogMode == DialogMode.PickLocation)
        {
            var (cancelPos, okPos, btnSize) = PanelDialogButtons();
            if (UiBatch.Contains(okPos, btnSize, pos)) ConfirmDialog();
            else if (UiBatch.Contains(cancelPos, btnSize, pos)) CancelDialog();
            else if (pos.Y >= ListTop && pos.Y < ListBottom) BeginPress(Press.Row, RowAt(pos)?.Node, pos);
            return;
        }

        if (Hit(CollapseButtonPos, new Vector2(32f))) { Toggle(); return; }
        if (Hit(TitleRect.Pos, TitleRect.Size)) { RenameCurrent(); return; }
        if (Hit(ToolbarButtonPos(0), ToolbarButtonSize)) { OpenSaveAsDialog(); return; }
        if (Hit(ToolbarButtonPos(1), ToolbarButtonSize)) { OpenCreateFolderDialog(); return; }
        if (Hit(ToolbarButtonPos(2), ToolbarButtonSize)) { RequestNewFile(); return; }

        if (Hit(BottomButtonPos, BottomButtonSize)) { OpenSettingsDialog(); return; }
        if (Hit(ThemeButtonPos, new Vector2(36f)))
        {
            Settings.LightTheme.Value = !Settings.LightTheme.Value;
            Settings.Save();
            _renderer.SetDirty();
            return;
        }

        if (pos.Y < ListTop || pos.Y >= ListBottom) return;

        if (RowAt(pos) is not { } row) return;
        if (row.Node != _rootNode)
        {
            for (int i = 0; i < 3; i++)
            {
                var (bPos, bSize) = ActionRect(row, (RowAction)i);
                if (!UiBatch.Contains(bPos, bSize, pos)) continue;
                switch ((RowAction)i)
                {
                    case RowAction.Move: BeginPress(Press.Move, row.Node, pos); break;
                    case RowAction.Rename: OpenRenameDialog(row.Node!); break;
                    default: OpenDeleteDialog(row.Node!); break;
                }

                return;
            }
        }

        BeginPress(Press.Row, row.Node, pos);
    }

    private void BeginPress(Press press, FileNode? node, Vector2 pos)
    {
        _press = press;
        _pressNode = node;
        _pressPos = pos;
        _pressScroll = _scrollY;
    }

    private Row? RowAt(Vector2 pos)
    {
        foreach (var row in _rows)
            if (!row.IsPlaceholder && pos.Y >= row.Y && pos.Y < row.Y + RowHeight)
                return row;
        return null;
    }

    private static void ShowLogFile()
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{LogPath}\"");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not open Explorer: {ex.Message}");
        }
    }

    public void HandleMouseMove(Vector2 pos)
    {
        bool changed = Vector2.DistanceSquared(pos, _mousePos) > 0.01f;
        _mousePos = pos;

        if (_settingsPanel.IsOpen)
        {
            _settingsPanel.HandleMouseMove(pos);
            return;
        }

        if (_dialogMode == DialogMode.Log)
        {
            if (_logDragging) _logScroll = Math.Clamp(_logDragScroll - (pos.Y - _logDragStart.Y), 0f, MaxLogScroll);
            _renderer.SetDirty();
            return;
        }

        float moved = Vector2.Distance(pos, _pressPos);
        switch (_press)
        {
            case Press.Row when moved > DragThreshold:
                _press = Press.Scroll;
                break;
            case Press.Move when moved > DragThreshold && !_isDragging:
                _isDragging = true;
                _dragNode = _pressNode;
                break;
        }

        if (_press == Press.Scroll)
        {
            _scrollY = _pressScroll - (pos.Y - _pressPos.Y);
            ScrollBy(0f);
        }

        if (_isDragging) UpdateDropTarget(pos);
        if (changed && (IsOpen || IsModalOpen)) _renderer.SetDirty();
    }

    public void HandleMouseUp(Vector2 pos)
    {
        _mousePos = pos;
        _renderer.SetDirty();

        if (_settingsPanel.IsOpen)
        {
            _settingsPanel.HandleMouseUp(pos);
            return;
        }

        _logDragging = false;

        var press = _press;
        var node = _pressNode;
        _press = Press.None;
        _pressNode = null;

        if (_isDragging)
        {
            FinishDrag();
            return;
        }

        if (press != Press.Row || node == null || RowAt(pos)?.Node != node) return;

        if (_dialogMode == DialogMode.PickLocation)
        {
            if (pos.X < 12f + RowAt(pos)!.Value.Depth * Indent + 20f) SetExpanded(node, !node.IsExpanded);
            else _selectedDestDir = node.FullPath;
        }
        else if (node.IsDirectory)
        {
            SetExpanded(node, !node.IsExpanded);
        }
        else
        {
            if (!string.Equals(node.FullPath, _libraryManager.CurrentPath, StringComparison.OrdinalIgnoreCase))
                RequestOpen(node.FullPath);
        }
    }

    private void FinishDrag()
    {
        var node = _dragNode;
        string? target = _dropTargetPath;
        // The placeholder becomes the moved row, so start the new row exactly where the placeholder is.
        float? landingY = _animatedY.TryGetValue(PlaceholderKey, out float y) ? y : null;
        CancelDrag();
        if (node == null || target == null) return;

        string? newPath = LibraryManager.GetMoveDestination(node.FullPath, node.IsDirectory, target);
        if (newPath == null) return;

        if (node.IsDirectory) _libraryManager.MoveDirectory(node.FullPath, target);
        else _libraryManager.MoveFile(node.FullPath, target);

        _collapsed.Remove(target);
        RefreshTree();
        if (landingY != null) _animatedY[newPath] = landingY.Value;
        _highlightPath = newPath;
        _highlightStart = DateTime.Now;
    }

    public void CancelDrag()
    {
        _isDragging = false;
        _dragNode = null;
        _dropTargetPath = null;
        _hoverFolderPath = null;
        _press = Press.None;
        _animatedY.Remove(PlaceholderKey);
        _renderer.SetDirty();
    }

    private void UpdateDropTarget(Vector2 pos)
    {
        string? folder = null;
        if (pos.X >= 0 && pos.X < Width && pos.Y >= ListTop && pos.Y < ListBottom && _rootNode != null)
        {
            var row = RowAt(pos);
            if (row is { } r)
                folder = r.Node!.IsDirectory ? r.Node.FullPath : Path.GetDirectoryName(r.Node.FullPath);
            else if (_rows.Any(rr => rr.IsPlaceholder && pos.Y >= rr.Y && pos.Y < rr.Y + RowHeight))
                folder = _dropTargetPath;
            else
                folder = _rootNode.FullPath;
        }

        if (folder != _hoverFolderPath)
        {
            _hoverFolderPath = folder;
            _hoverFolderSince = DateTime.Now;
        }

        string? target = folder != null && _dragNode != null &&
                         LibraryManager.GetMoveDestination(_dragNode.FullPath, _dragNode.IsDirectory, folder) != null
            ? folder
            : null;

        if (target != _dropTargetPath)
        {
            _dropTargetPath = target;
            // Start the placeholder where the pointer is so it visibly slides into its slot.
            _animatedY[PlaceholderKey] = pos.Y - ListTop - 4f + _scrollY - RowHeight * 0.5f;
            if (target == null) _animatedY.Remove(PlaceholderKey);
        }
    }

    public void HandleScroll(float delta)
    {
        if (_settingsPanel.IsOpen)
        {
            _settingsPanel.HandleScroll(delta);
            return;
        }

        if (_dialogMode == DialogMode.Log)
        {
            _logScroll = Math.Clamp(_logScroll - delta * 40f, 0f, MaxLogScroll);
            _renderer.SetDirty();
            return;
        }

        if (IsPopupDialog) return;
        ScrollBy(-delta * 40f);
    }

    public void Dispose() { }
}
