﻿using System.Numerics;
using System.Text;
using MathBoard.Core;

namespace MathBoard.Rendering;

public class LibraryPanel : IDisposable
{
    public bool IsOpen { get; private set; } = true;
    public float Width { get; set; } = 340f;

    private readonly StrokeRenderer _renderer;
    private readonly LibraryManager _libraryManager;
    private readonly SettingsPanel _settingsPanel;
    public SettingsPanel SettingsPanel => _settingsPanel;

    private FileNode? _rootNode;
    private readonly List<(FileNode node, float x, float y)> _flatList = new();
    private float _scrollY = 0f;
    private const float ItemHeight = 36f;

    private static readonly Vector4 TextColor = new(0.93f, 0.93f, 0.95f, 1f);
    private static readonly Vector4 ButtonTextColor = Vector4.One;
    private static readonly Vector4 FileNameColor = new(0.95f, 0.78f, 0.55f, 1f);
    private static readonly Vector4 DropHighlightColor = new(0.4f, 0.78f, 1.0f, 0.95f);

    public enum DialogMode { None, Delete, Rename, Save, CreateFolder, NewFile, Log }
    private enum IconAction { Edit, Delete, Move }

    private DialogMode _dialogMode = DialogMode.None;
    private FileNode? _dialogTargetNode;

    public bool IsDialogOpen => _dialogMode != DialogMode.None || _settingsPanel.IsOpen;
    public bool IsTreeDialog => _dialogMode == DialogMode.CreateFolder;
    public bool HasTextInput => _dialogMode == DialogMode.Rename || _dialogMode == DialogMode.Save || _dialogMode == DialogMode.CreateFolder || _settingsPanel.HasTextInput;

    private bool _dragPending = false;
    private Vector2 _dragStartPos;
    private FileNode? _dragNode;
    private bool _isDragging = false;
    private Vector2 _dragMousePos;
    private string? _dropTargetPath;
    private const float DragThreshold = 6f;
    private Vector2 _mousePos;

    public bool IsDragging => _isDragging;
    public bool HasPendingDrag => _dragPending;

    private List<string> _logLines = new();

    public class TextBoxState
    {
        public StringBuilder Text = new();
        public int CursorPos = 0;
        public int AnchorPos = 0;
        public DateTime LastEditTime = DateTime.Now;

        public int SelStart => Math.Min(CursorPos, AnchorPos);
        public int SelEnd => Math.Max(CursorPos, AnchorPos);
        public bool HasSelection => CursorPos != AnchorPos;

        public void SetText(string text)
        {
            Text.Clear();
            Text.Append(text);
            CursorPos = Text.Length;
            AnchorPos = CursorPos;
            LastEditTime = DateTime.Now;
        }

        public void Insert(char c)
        {
            if (HasSelection)
            {
                Text.Remove(SelStart, SelEnd - SelStart);
                CursorPos = SelStart;
                AnchorPos = CursorPos;
            }
            Text.Insert(CursorPos, c);
            CursorPos++;
            AnchorPos = CursorPos;
            LastEditTime = DateTime.Now;
        }

        public void Backspace(bool wholeWord)
        {
            if (HasSelection)
            {
                Text.Remove(SelStart, SelEnd - SelStart);
                CursorPos = SelStart;
                AnchorPos = CursorPos;
            }
            else
            {
                if (CursorPos <= 0) { LastEditTime = DateTime.Now; return; }

                int removeCount = wholeWord ? GetWordLength(-1) : 1;
                removeCount = Math.Min(removeCount, CursorPos);
                if (removeCount > 0)
                {
                    Text.Remove(CursorPos - removeCount, removeCount);
                    CursorPos -= removeCount;
                    AnchorPos = CursorPos;
                }
            }
            LastEditTime = DateTime.Now;
        }

        public void Delete(bool wholeWord)
        {
            if (HasSelection)
            {
                Text.Remove(SelStart, SelEnd - SelStart);
                CursorPos = SelStart;
                AnchorPos = CursorPos;
            }
            else
            {
                if (CursorPos >= Text.Length) { LastEditTime = DateTime.Now; return; }

                int removeCount = wholeWord ? GetWordLength(1) : 1;
                removeCount = Math.Min(removeCount, Text.Length - CursorPos);
                if (removeCount > 0)
                    Text.Remove(CursorPos, removeCount);
            }
            LastEditTime = DateTime.Now;
        }

        private int GetWordLength(int direction)
        {
            int count = 0;
            int i = CursorPos;
            if (direction == -1) i--;

            bool skippedSpace = false;
            while (i >= 0 && i < Text.Length)
            {
                if (char.IsWhiteSpace(Text[i]))
                {
                    if (skippedSpace) break;
                    count++;
                }
                else
                {
                    skippedSpace = true;
                    count++;
                }
                i += direction;
            }
            return count;
        }

        public void MoveCursor(int delta, bool wholeWord, bool shift)
        {
            int newPos = CursorPos;
            if (wholeWord) newPos += GetWordLength(delta);
            else newPos += delta;

            newPos = Math.Clamp(newPos, 0, Text.Length);
            CursorPos = newPos;
            if (!shift) AnchorPos = CursorPos;
            LastEditTime = DateTime.Now;
        }

        public void MoveCursorToStart(bool shift)
        {
            CursorPos = 0;
            if (!shift) AnchorPos = 0;
            LastEditTime = DateTime.Now;
        }

        public void MoveCursorToEnd(bool shift)
        {
            CursorPos = Text.Length;
            if (!shift) AnchorPos = Text.Length;
            LastEditTime = DateTime.Now;
        }

        public void SelectAll()
        {
            CursorPos = 0;
            AnchorPos = Text.Length;
            LastEditTime = DateTime.Now;
        }

        public override string ToString() => Text.ToString();
    }

    private readonly TextBoxState _textBox = new();
    public TextBoxState TextBox
    {
        get
        {
            if (_settingsPanel.HasTextInput) return _settingsPanel.TextBox;
            return _textBox;
        }
    }

    private string _dialogTitle = "";
    private string _dialogConfirmLabel = "OK";
    private string _dialogCancelLabel = "Cancel";
    private string _dialogMessage = "";

    private const float DialogWidth = 380f;
    private const float DialogHeight = 210f;
    private const float DialogBtnWidth = 100f;
    private const float DialogBtnHeight = 36f;
    private const float IconButtonSize = 32f;
    private const float IconButtonMargin = 4f;

    private readonly List<(FileNode node, IconAction action, float x, float y, float w, float h)> _iconHitRegions = new();
    private Vector4 _saveButtonBounds;
    private Vector4 _createFolderButtonBounds;
    private Vector4 _newButtonBounds;
    private Vector4 _settingsButtonBounds;
    private string? _selectedDestDir;

    private Vector2 _screenSize;
    private TextAtlas.Entry _saveIconEntry, _editIconEntry, _deleteIconEntry, _moveIconEntry, _folderIconEntry, _newIconEntry, _settingsIconEntry;

    public LibraryPanel(StrokeRenderer renderer, LibraryManager libraryManager)
    {
        _renderer = renderer;
        _libraryManager = libraryManager;
        _settingsPanel = new SettingsPanel(renderer, RebuildTextAtlas);
        _settingsPanel.OnOpenLog = OpenLogDialog;
        _saveButtonBounds = new Vector4(16, 78, IconButtonSize, IconButtonSize);
        _createFolderButtonBounds = new Vector4(16 + 36, 78, IconButtonSize, IconButtonSize);
        _newButtonBounds = new Vector4(16 + 72, 78, IconButtonSize, IconButtonSize);
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
        RebuildFlatList();
        RebuildTextAtlas();
        _renderer.SetDirty();
    }

    public void RefreshDialogText() => RebuildTextAtlas();

    private void RebuildTextAtlas()
    {
        var atlas = _renderer.TextAtlas;
        atlas.BeginBuild();

        atlas.Request(Localization.Get("panel_save_as"));
        atlas.Request(Localization.Get("panel_create_folder"));
        atlas.Request(Localization.Get("panel_new_file", "New File"));
        atlas.Request(Localization.Get("dialog_save_title"));
        atlas.Request(Localization.Get("dialog_rename_title"));
        atlas.Request(Localization.Get("dialog_delete_title"));
        atlas.Request(Localization.Get("dialog_create_folder_title"));
        atlas.Request(Localization.Get("dialog_new_file_title", "Create New File?"));
        atlas.Request(Localization.Get("dialog_button_ok"));
        atlas.Request(Localization.Get("dialog_button_save"));
        atlas.Request(Localization.Get("dialog_button_cancel"));
        atlas.Request(Localization.Get("dialog_button_create", "Create"));
        atlas.Request(Localization.Get("settings_title", "Settings"));

        atlas.Request(Localization.Get("tooltip_save", "Save"));
        atlas.Request(Localization.Get("tooltip_create_folder", "Create Folder"));
        atlas.Request(Localization.Get("tooltip_new_file", "New File"));
        atlas.Request(Localization.Get("tooltip_settings", "Settings"));
        atlas.Request(Localization.Get("tooltip_open_log", "Open Latest Log"));
        
        atlas.Request("S");
        atlas.Request("X");

        _saveIconEntry = atlas.RequestImage("resources/textures/save.png");
        _editIconEntry = atlas.RequestImage("resources/textures/edit.png");
        _deleteIconEntry = atlas.RequestImage("resources/textures/delete.png");
        _moveIconEntry = atlas.RequestImage("resources/textures/move.png");
        _folderIconEntry = atlas.RequestImage("resources/textures/folder.png");
        _newIconEntry = atlas.RequestImage("resources/textures/new.png");
        _settingsIconEntry = atlas.RequestImage("resources/textures/settings.png");

        _renderer.RequestRadialMenuIcons();

        foreach (var (node, _, _) in _flatList)
            atlas.Request(LabelFor(node));

        if (_dialogMode != DialogMode.None && !string.IsNullOrEmpty(_textBox.ToString()))
            atlas.Request(_textBox.ToString());
        
        if (_dialogMode == DialogMode.NewFile && !string.IsNullOrEmpty(_dialogMessage))
            atlas.Request(_dialogMessage);

        if (_dialogMode == DialogMode.Delete && _dialogTargetNode != null)
            atlas.Request(_dialogTargetNode.Name);

        if (_settingsPanel.IsOpen)
        {
            _settingsPanel.RequestAtlasEntries(atlas);
        }
        else if (_dialogMode == DialogMode.Log)
        {
            foreach (var line in _logLines)
                atlas.Request(line);
        }

        atlas.EndBuild();
        _renderer.SetDirty();
    }

    private static string LabelFor(FileNode node)
    {
        string prefix = node.IsDirectory
            ? (node.IsExpanded ? "\u25BC " : "\u25B6 ")
            : "   \u2022 ";
        return prefix + node.Name;
    }

    private FileNode BuildTree(string path)
    {
        var root = new FileNode(Path.GetFileName(path) ?? "Lessons", path, true);
        if (!Directory.Exists(path)) { Directory.CreateDirectory(path); return root; }

        foreach (var dir in Directory.GetDirectories(path))
            root.Children.Add(BuildTree(dir));

        foreach (var file in Directory.GetFiles(path, "*.mathboard"))
            root.Children.Add(new FileNode(Path.GetFileNameWithoutExtension(file), file, false));

        root.Children.Sort((a, b) =>
        {
            int dirCmp = b.IsDirectory.CompareTo(a.IsDirectory);
            return dirCmp != 0 ? dirCmp : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return root;
    }

    private void RebuildFlatList()
    {
        _flatList.Clear();
        if (_rootNode == null) return;
        FlattenTree(_rootNode, 24f, 130f);
    }

    private float FlattenTree(FileNode node, float x, float y)
    {
        _flatList.Add((node, x, y));
        float nextY = y + ItemHeight;
        if (node.IsDirectory && node.IsExpanded)
        {
            foreach (var child in node.Children)
            {
                nextY = FlattenTree(child, x + 24, nextY);
            }
        }
        return nextY;
    }

    public void RenderToVertices(List<UICommand> cmds, Vector2 screenSize)
    {
        _screenSize = screenSize;
        if (!IsOpen) return;

        DrawRect(cmds, Vector2.Zero, new Vector2(Width, screenSize.Y), new Vector4(0.06f, 0.06f, 0.085f, 0.985f));
        DrawRect(cmds, Vector2.Zero, new Vector2(Width, 68), new Vector4(0.11f, 0.11f, 0.15f, 1f));

        var atlas = _renderer.TextAtlas;

        if (_dialogMode != DialogMode.None && !IsTreeDialog && _dialogMode != DialogMode.Log)
        {
            RenderDialog(cmds, atlas, screenSize);
            return;
        }

        if (!IsTreeDialog && _dialogMode != DialogMode.Log)
            RenderTopButtons(cmds, atlas);

        if (_dialogMode == DialogMode.Log)
            RenderLogList(cmds, atlas);
        else
            RenderFileList(cmds, atlas, IsTreeDialog);

        if (IsTreeDialog)
            RenderTreeDialogUI(cmds, atlas, screenSize);

        RenderBottomButtons(cmds, atlas);

        string tooltip = null;
        if (_dialogMode == DialogMode.None && !_settingsPanel.IsOpen)
        {
            if (HitTest(_saveButtonBounds, _mousePos)) tooltip = Localization.Get("tooltip_save", "Save");
            else if (HitTest(_createFolderButtonBounds, _mousePos)) tooltip = Localization.Get("tooltip_create_folder", "Create Folder");
            else if (HitTest(_newButtonBounds, _mousePos)) tooltip = Localization.Get("tooltip_new_file", "New File");
            else if (HitTest(_settingsButtonBounds, _mousePos)) tooltip = Localization.Get("tooltip_settings", "Settings");
        }

        if (!string.IsNullOrEmpty(tooltip))
        {
            var size = atlas.Measure(tooltip);
            float pad = 6f;
            float bgX = _mousePos.X + 15f;
            float bgY = _mousePos.Y - size.Y - 15f;
            DrawRect(cmds, new Vector2(bgX, bgY), new Vector2(size.X + pad * 2, size.Y + pad * 2), new Vector4(0, 0, 0, 0.8f));
            atlas.Emit(tooltip, new Vector2(bgX + pad, bgY + pad), TextColor);
        }

        if (_settingsPanel.IsOpen)
            _settingsPanel.RenderToVertices(cmds, screenSize);

        if (_isDragging && _dragNode != null)
            RenderDragPreview(cmds, atlas);
    }

    private void RenderTopButtons(List<UICommand> cmds, TextAtlas atlas)
    {
        DrawIconButton(cmds, atlas, _saveIconEntry, _saveButtonBounds.X, _saveButtonBounds.Y, new Vector4(0.22f, 0.42f, 0.78f, 1f));
        DrawIconButton(cmds, atlas, _folderIconEntry, _createFolderButtonBounds.X, _createFolderButtonBounds.Y, new Vector4(0.18f, 0.38f, 0.28f, 1f));
        DrawIconButton(cmds, atlas, _newIconEntry, _newButtonBounds.X, _newButtonBounds.Y, new Vector4(0.42f, 0.22f, 0.22f, 1f));
    }

    private void RenderBottomButtons(List<UICommand> cmds, TextAtlas atlas)
    {
        _settingsButtonBounds = new Vector4(16, _screenSize.Y - 48, IconButtonSize, IconButtonSize);
        DrawIconButton(cmds, atlas, _settingsIconEntry, _settingsButtonBounds.X, _settingsButtonBounds.Y, new Vector4(0.2f, 0.2f, 0.2f, 1f));
    }

    private void RenderFileList(List<UICommand> cmds, TextAtlas atlas, bool isTreeDialog)
    {
        _iconHitRegions.Clear();
        float cullY = isTreeDialog ? 68f : 120f;

        foreach (var (node, x, y) in _flatList)
        {
            float screenY = y - _scrollY;
            if (screenY < cullY || screenY > _screenSize.Y + 50) continue;

            if (isTreeDialog)
            {
                if (!node.IsDirectory) continue;

                if (_selectedDestDir == node.FullPath)
                    DrawRect(cmds, new Vector2(0, screenY), new Vector2(Width, ItemHeight), new Vector4(0.3f, 0.5f, 0.8f, 0.35f));

                string label = LabelFor(node);
                var size = atlas.Measure(label);
                atlas.Emit(label, new Vector2(x, screenY + (ItemHeight - size.Y) * 0.5f), TextColor);
            }
            else
            {
                if (_isDragging && _dropTargetPath != null && _dropTargetPath == node.FullPath)
                    DrawRectOutline(cmds, new Vector2(0, screenY), new Vector2(Width, ItemHeight), DropHighlightColor, 2f);

                if (node != _rootNode)
                {
                    float iY = screenY + (ItemHeight - IconButtonSize) * 0.5f;

                    float delX = Width - IconButtonSize - IconButtonMargin;
                    DrawIconButton(cmds, atlas, _deleteIconEntry, delX, iY, new Vector4(0.22f, 0.10f, 0.10f, 0.85f));
                    _iconHitRegions.Add((node, IconAction.Delete, delX, iY, IconButtonSize, IconButtonSize));

                    float editX = delX - IconButtonSize - IconButtonMargin;
                    DrawIconButton(cmds, atlas, _editIconEntry, editX, iY, new Vector4(0.10f, 0.12f, 0.20f, 0.85f));
                    _iconHitRegions.Add((node, IconAction.Edit, editX, iY, IconButtonSize, IconButtonSize));

                    float moveX = editX - IconButtonSize - IconButtonMargin;
                    DrawIconButton(cmds, atlas, _moveIconEntry, moveX, iY, new Vector4(0.10f, 0.18f, 0.12f, 0.85f));
                    _iconHitRegions.Add((node, IconAction.Move, moveX, iY, IconButtonSize, IconButtonSize));
                }

                string label = LabelFor(node);
                var size = atlas.Measure(label);
                atlas.Emit(label, new Vector2(x, screenY + (ItemHeight - size.Y) * 0.5f), TextColor);
            }
        }
    }

    private void RenderDragPreview(List<UICommand> cmds, TextAtlas atlas)
    {
        string label = LabelFor(_dragNode!);
        var labelSize = atlas.Measure(label);

        float padding = 12f;
        float previewW = Math.Max(labelSize.X + padding * 2, 120f);
        float previewH = ItemHeight;
        float previewX = _dragMousePos.X - previewW * 0.5f;
        float previewY = _dragMousePos.Y - previewH * 0.5f;

        DrawRect(cmds, new Vector2(previewX, previewY), new Vector2(previewW, previewH), new Vector4(0.15f, 0.15f, 0.20f, 0.90f));
        DrawRectOutline(cmds, new Vector2(previewX, previewY), new Vector2(previewW, previewH), new Vector4(0.4f, 0.78f, 1.0f, 0.8f), 1.5f);

        if (labelSize.X > 0)
            atlas.Emit(label, new Vector2(previewX + padding, previewY + (previewH - labelSize.Y) * 0.5f), TextColor);
    }

    private void RenderTreeDialogUI(List<UICommand> cmds, TextAtlas atlas, Vector2 screenSize)
    {
        var titleSize = atlas.Measure(_dialogTitle);
        atlas.Emit(_dialogTitle, new Vector2((Width - titleSize.X) * 0.5f, 24), TextColor);

        if (_dialogMode == DialogMode.CreateFolder)
        {
            float fieldX = 16f;
            float fieldW = Width - 32f;
            float fieldH = 38f;
            float fieldY = 78f;

            DrawRect(cmds, new Vector2(fieldX, fieldY), new Vector2(fieldW, fieldH), new Vector4(0.05f, 0.05f, 0.07f, 1f));
            DrawRectOutline(cmds, new Vector2(fieldX, fieldY), new Vector2(fieldW, fieldH), new Vector4(0.30f, 0.32f, 0.40f, 1f), 1.5f);

            RenderTextInput(cmds, atlas, new Vector2(fieldX, fieldY), new Vector2(fieldW, fieldH));
        }

        float btnY = screenSize.Y - DialogBtnHeight - 20f;
        float cancelX = 16;
        DrawDialogButton(cmds, atlas, _dialogCancelLabel, cancelX, btnY, new Vector4(0.20f, 0.20f, 0.25f, 1f));

        float okX = Width - DialogBtnWidth - 16;
        DrawDialogButton(cmds, atlas, _dialogConfirmLabel, okX, btnY, new Vector4(0.22f, 0.42f, 0.78f, 1f));
    }

    private void RenderDialog(List<UICommand> cmds, TextAtlas atlas, Vector2 screenSize)
    {
        DrawRect(cmds, Vector2.Zero, screenSize, new Vector4(0, 0, 0, 0.55f));

        float dx = (screenSize.X - DialogWidth) * 0.5f;
        float dy = (screenSize.Y - DialogHeight) * 0.5f;

        DrawRect(cmds, new Vector2(dx, dy), new Vector2(DialogWidth, DialogHeight), new Vector4(0.10f, 0.10f, 0.14f, 0.98f));
        DrawRectOutline(cmds, new Vector2(dx, dy), new Vector2(DialogWidth, DialogHeight), new Vector4(0.35f, 0.37f, 0.45f, 1f), 2f);

        float yPos = dy + 28f;
        var titleSize = atlas.Measure(_dialogTitle);
        atlas.Emit(_dialogTitle, new Vector2(dx + (DialogWidth - titleSize.X) * 0.5f, yPos), TextColor);
        yPos += 38f;

        if (_dialogMode == DialogMode.Rename || _dialogMode == DialogMode.Save)
        {
            float fieldX = dx + 28f;
            float fieldW = DialogWidth - 56f;
            float fieldH = 38f;

            DrawRect(cmds, new Vector2(fieldX, yPos), new Vector2(fieldW, fieldH), new Vector4(0.05f, 0.05f, 0.07f, 1f));
            DrawRectOutline(cmds, new Vector2(fieldX, yPos), new Vector2(fieldW, fieldH), new Vector4(0.30f, 0.32f, 0.40f, 1f), 1.5f);

            RenderTextInput(cmds, atlas, new Vector2(fieldX, yPos), new Vector2(fieldW, fieldH));
        }
        else if (_dialogMode == DialogMode.Delete && _dialogTargetNode != null)
        {
            var nameSize = atlas.Measure(_dialogTargetNode.Name);
            atlas.Emit(_dialogTargetNode.Name, new Vector2(dx + (DialogWidth - nameSize.X) * 0.5f, yPos), FileNameColor);
        }
        else if (_dialogMode == DialogMode.NewFile)
        {
            var msgSize = atlas.Measure(_dialogMessage);
            atlas.Emit(_dialogMessage, new Vector2(dx + (DialogWidth - msgSize.X) * 0.5f, yPos), TextColor);
        }

        float btnY = dy + DialogHeight - DialogBtnHeight - 20f;
        float cancelX = dx + DialogWidth - DialogBtnWidth * 2 - 28;
        DrawDialogButton(cmds, atlas, _dialogCancelLabel, cancelX, btnY, new Vector4(0.20f, 0.20f, 0.25f, 1f));

        float okX = dx + DialogWidth - DialogBtnWidth - 16;
        DrawDialogButton(cmds, atlas, _dialogConfirmLabel, okX, btnY, new Vector4(0.22f, 0.42f, 0.78f, 1f));
    }

    private void RenderLogList(List<UICommand> cmds, TextAtlas atlas)
    {
        var title = Localization.Get("tooltip_open_log", "Latest Log");
        var titleSize = atlas.Measure(title);
        atlas.Emit(title, new Vector2((Width - titleSize.X) * 0.5f, 24), TextColor);

        DrawRect(cmds, new Vector2(0, 68), new Vector2(Width, _screenSize.Y - 68), new Vector4(0.06f, 0.06f, 0.085f, 1f));

        float cullY = 68f;
        float bottomCull = _screenSize.Y - 60f;
        float y = 78f - _scrollY;
        float lineH = 20f;

        foreach (var line in _logLines)
        {
            if (y + lineH > cullY && y < bottomCull)
            {
                atlas.Emit(line, new Vector2(16f, y), TextColor);
            }
            y += lineH;
        }

        float btnY = _screenSize.Y - 48;
        float cancelX = 16;
        DrawTextButton(cmds, atlas, "X", new Vector4(cancelX, btnY, IconButtonSize, IconButtonSize), new Vector4(0.42f, 0.22f, 0.22f, 1f));
    }

    private void RenderTextInput(List<UICommand> cmds, TextAtlas atlas, Vector2 fieldPos, Vector2 fieldSize)
    {
        string text = _textBox.ToString();
        float fieldX = fieldPos.X;
        float fieldY = fieldPos.Y;
        float fieldW = fieldSize.X;
        float fieldH = fieldSize.Y;

        if (_textBox.HasSelection)
        {
            string beforeSel = text.Substring(0, _textBox.SelStart);
            string selText = text.Substring(_textBox.SelStart, _textBox.SelEnd - _textBox.SelStart);

            var beforeSize = atlas.Measure(beforeSel);
            var selSize = atlas.Measure(selText);

            DrawRect(cmds, new Vector2(fieldX + 12f + beforeSize.X, fieldY + 4f), new Vector2(selSize.X, fieldH - 8f), new Vector4(0.2f, 0.4f, 0.8f, 0.8f));
        }

        var inputSize = atlas.Measure(text);
        float textY = fieldY + (fieldH - inputSize.Y) * 0.5f;
        atlas.Emit(text, new Vector2(fieldX + 12f, textY), TextColor);

        bool showCursor = ((DateTime.Now - _textBox.LastEditTime).TotalMilliseconds % 1000) < 500;
        if (showCursor)
        {
            string beforeCursor = text.Substring(0, _textBox.CursorPos);
            var beforeSize = atlas.Measure(beforeCursor);
            float cursorX = fieldX + 12f + beforeSize.X + 2f;
            float cursorH = Math.Max(inputSize.Y - 6f, 12f);
            DrawRect(cmds, new Vector2(cursorX, fieldY + (fieldH - cursorH) * 0.5f), new Vector2(2f, cursorH), new Vector4(0.9f, 0.9f, 0.95f, 1f));
        }
    }

    private void DrawIconButton(List<UICommand> cmd, TextAtlas atlas, TextAtlas.Entry icon, float x, float y, Vector4 bg)
    {
        DrawRect(cmd, new Vector2(x, y), new Vector2(IconButtonSize, IconButtonSize), bg);
        if (icon.Width > 0)
        {
            float pad = 6f;
            atlas.EmitImage(icon, new Vector2(x + pad, y + pad), new Vector2(IconButtonSize - pad * 2, IconButtonSize - pad * 2), Vector4.One);
        }
    }

    private void DrawTextButton(List<UICommand> cmd, TextAtlas atlas, string text, Vector4 bounds, Vector4 bg)
    {
        DrawRect(cmd, new Vector2(bounds.X, bounds.Y), new Vector2(bounds.Z, bounds.W), bg);
        var size = atlas.Measure(text);
        atlas.Emit(text, new Vector2(bounds.X + (bounds.Z - size.X) * 0.5f, bounds.Y + (bounds.W - size.Y) * 0.5f), ButtonTextColor);
    }

    private void DrawDialogButton(List<UICommand> cmd, TextAtlas atlas, string text, float x, float y, Vector4 bg)
    {
        DrawRect(cmd, new Vector2(x, y), new Vector2(DialogBtnWidth, DialogBtnHeight), bg);
        var size = atlas.Measure(text);
        atlas.Emit(text, new Vector2(x + (DialogBtnWidth - size.X) * 0.5f, y + (DialogBtnHeight - size.Y) * 0.5f), ButtonTextColor);
    }

    private static void DrawRectOutline(List<UICommand> cmds, Vector2 pos, Vector2 size, Vector4 color, float thickness)
        => cmds.Add(new UICommand { P1P2 = new Vector4(pos.X, pos.Y, size.X, size.Y), Color = color, Params = new Vector4(thickness, 3, 0, 0) });

    private static void DrawRect(List<UICommand> cmds, Vector2 pos, Vector2 size, Vector4 color)
        => cmds.Add(new UICommand { P1P2 = new Vector4(pos.X, pos.Y, size.X, size.Y), Color = color, Params = new Vector4(0, 0, 0, 0) });

    public void OpenSaveDialog()
    {
        _dialogMessage = "";
        _dialogMode = DialogMode.Save;
        _dialogTargetNode = null;
        _textBox.SetText($"Lesson_{DateTime.Now:yyyy-MM-dd_HH-mm}");
        _dialogTitle = Localization.Get("dialog_save_title");
        _dialogConfirmLabel = Localization.Get("dialog_button_save");
        _dialogCancelLabel = Localization.Get("dialog_button_cancel");
        RebuildTextAtlas();
    }

    public void OpenRenameDialog(FileNode node)
    {
        _dialogMessage = "";
        _dialogMode = DialogMode.Rename;
        _dialogTargetNode = node;
        _textBox.SetText(node.Name);
        _dialogTitle = Localization.Get("dialog_rename_title");
        _dialogConfirmLabel = Localization.Get("dialog_button_ok");
        _dialogCancelLabel = Localization.Get("dialog_button_cancel");
        RebuildTextAtlas();
    }

    public void OpenDeleteDialog(FileNode node)
    {
        _dialogMessage = "";
        _dialogMode = DialogMode.Delete;
        _dialogTargetNode = node;
        _textBox.SetText("");
        _dialogTitle = Localization.Get("dialog_delete_title");
        _dialogConfirmLabel = Localization.Get("dialog_button_ok");
        _dialogCancelLabel = Localization.Get("dialog_button_cancel");
        RebuildTextAtlas();
    }

    public void OpenCreateFolderDialog()
    {
        _dialogMessage = "";
        _dialogMode = DialogMode.CreateFolder;
        _dialogTargetNode = null;
        _selectedDestDir = Settings.LibraryRootPath.Value;
        _textBox.SetText($"NewFolder_{DateTime.Now:HHmm}");
        _dialogTitle = Localization.Get("dialog_create_folder_title");
        _dialogConfirmLabel = Localization.Get("dialog_button_ok");
        _dialogCancelLabel = Localization.Get("dialog_button_cancel");
        RebuildTextAtlas();
    }

    public void OpenSettingsDialog()
    {
        _settingsPanel.Open();
    }

    public void OpenLogDialog()
    {
        _dialogMode = DialogMode.Log;
        _scrollY = 0;
        _logLines.Clear();
        try
        {
            if (File.Exists("latest.log"))
            {
                var lines = File.ReadAllLines("latest.log");
                _logLines = lines.Skip(Math.Max(0, lines.Length - 500)).ToList();
            }
            else
            {
                _logLines.Add("Log file not found.");
            }
        }
        catch (Exception ex)
        {
            _logLines.Add($"Error reading log: {ex.Message}");
        }
        RebuildTextAtlas();
    }

    public void ConfirmDialog()
    {
        if (_settingsPanel.IsOpen)
        {
            _settingsPanel.Close();
            return;
        }

        if (_dialogMode == DialogMode.None) return;

        switch (_dialogMode)
        {
            case DialogMode.Save:
                if (!string.IsNullOrWhiteSpace(_textBox.ToString()))
                    _libraryManager.SaveCanvas(_textBox.ToString());
                break;
            case DialogMode.Delete when _dialogTargetNode != null:
                if (_dialogTargetNode.IsDirectory)
                    _libraryManager.DeleteDirectory(_dialogTargetNode.FullPath);
                else
                    _libraryManager.DeleteFile(_dialogTargetNode.FullPath);
                break;
            case DialogMode.Rename when _dialogTargetNode != null:
                if (!string.IsNullOrWhiteSpace(_textBox.ToString()))
                {
                    if (_dialogTargetNode.IsDirectory)
                        _libraryManager.RenameDirectory(_dialogTargetNode.FullPath, _textBox.ToString());
                    else
                        _libraryManager.RenameFile(_dialogTargetNode.FullPath, _textBox.ToString());
                }
                break;
            case DialogMode.CreateFolder when _selectedDestDir != null:
                if (!string.IsNullOrWhiteSpace(_textBox.ToString()))
                    _libraryManager.CreateFolder(_selectedDestDir, _textBox.ToString());
                break;
            case DialogMode.NewFile:
                _libraryManager.NewFile();
                break;
        }

        _dialogMode = DialogMode.None;
        _dialogTargetNode = null;
        _selectedDestDir = null;
        RefreshTree();
    }

    public void CancelDialog()
    {
        if (_settingsPanel.IsOpen)
        {
            _settingsPanel.Cancel();
            return;
        }

        _dialogMode = DialogMode.None;
        _dialogTargetNode = null;
        _selectedDestDir = null;
        _dialogMessage = ""; 
        _renderer.SetDirty();
    }

    public void HandleCharInput(char c)
    {
        if (_settingsPanel.IsOpen)
        {
            _settingsPanel.HandleCharInput(c);
            return;
        }

        if (_dialogMode != DialogMode.Rename && _dialogMode != DialogMode.Save && _dialogMode != DialogMode.CreateFolder) return;
        if (c < 32) return;
        if (c == '\\' || c == '/' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|') return;

        _textBox.Insert(c);
        RefreshDialogText();
    }

    public bool HandleMouseDown(Vector2 pos, bool isRightClick = false)
    {
        _mousePos = pos;
        
        if (_settingsPanel.IsOpen)
        {
            _settingsPanel.UpdateMousePos(pos);
            _settingsPanel.HandleMouseDown(pos);
            return true;
        }

        if (IsDialogOpen)
        {
            HandleDialogClick(pos, isRightClick);
            return true;
        }

        _dragPending = false;
        _dragNode = null;

        if (!IsOpen || pos.X > Width) return false;

        if (HitTest(_saveButtonBounds, pos)) { OpenSaveDialog(); return true; }
        if (HitTest(_createFolderButtonBounds, pos)) { OpenCreateFolderDialog(); return true; }
        if (HitTest(_newButtonBounds, pos))
        {
            if (_libraryManager.IsDocumentDirty())
                OpenNewFileDialog();
            else
                _libraryManager.NewFile();
            return true;
        }
        
        if (HitTest(_settingsButtonBounds, pos)) { OpenSettingsDialog(); return true; }

        foreach (var (node, action, x, y, w, h) in _iconHitRegions)
        {
            if (pos.X >= x && pos.X <= x + w && pos.Y >= y && pos.Y <= y + h)
            {
                if (action == IconAction.Edit) OpenRenameDialog(node);
                else if (action == IconAction.Delete) OpenDeleteDialog(node);
                else if (action == IconAction.Move)
                {
                    _dragPending = true;
                    _dragStartPos = pos;
                    _dragNode = node;
                }
                return true;
            }
        }

        float relativeY = pos.Y + _scrollY;
        foreach (var (node, x, y) in _flatList)
        {
            if (MathF.Abs(y - relativeY) < ItemHeight * 0.5f)
            {
                if (node.IsDirectory)
                {
                    node.IsExpanded = !node.IsExpanded;
                    RebuildFlatList();
                    RebuildTextAtlas();
                }
                else
                {
                    _libraryManager.LoadFile(node.FullPath);
                }
                _renderer.SetDirty();
                return true;
            }
        }
        return false;
    }

    private bool HitTest(Vector4 bounds, Vector2 pos)
    {
        return pos.X >= bounds.X && pos.X <= bounds.X + bounds.Z &&
               pos.Y >= bounds.Y && pos.Y <= bounds.Y + bounds.W;
    }

    private void HandleDialogClick(Vector2 pos, bool isRightClick)
    {
        if (IsTreeDialog)
        {
            HandleTreeDialogClick(pos);
            return;
        }
        
        if (_dialogMode == DialogMode.Log)
        {
            float btnY = _screenSize.Y - 48;
            float cancelX = 16;
            if (pos.X >= cancelX && pos.X <= cancelX + IconButtonSize && pos.Y >= btnY && pos.Y <= btnY + IconButtonSize)
            {
                CancelDialog();
                return;
            }
            return;
        }

        float dx = (_screenSize.X - DialogWidth) * 0.5f;
        float dy = (_screenSize.Y - DialogHeight) * 0.5f;
        float btnY1 = dy + DialogHeight - DialogBtnHeight - 20f;

        float okX = dx + DialogWidth - DialogBtnWidth - 16;
        if (pos.X >= okX && pos.X <= okX + DialogBtnWidth && pos.Y >= btnY1 && pos.Y <= btnY1 + DialogBtnHeight)
        {
            ConfirmDialog();
            return;
        }

        float cancelX1 = dx + DialogWidth - DialogBtnWidth * 2 - 28;
        if (pos.X >= cancelX1 && pos.X <= cancelX1 + DialogBtnWidth && pos.Y >= btnY1 && pos.Y <= btnY1 + DialogBtnHeight)
        {
            CancelDialog();
            return;
        }
    }

    private void HandleTreeDialogClick(Vector2 pos)
    {
        float btnY = _screenSize.Y - DialogBtnHeight - 20f;
        float cancelX = 16;
        float okX = Width - DialogBtnWidth - 16;

        if (pos.X >= okX && pos.X <= okX + DialogBtnWidth && pos.Y >= btnY && pos.Y <= btnY + DialogBtnHeight)
        {
            ConfirmDialog();
            return;
        }
        if (pos.X >= cancelX && pos.X <= cancelX + DialogBtnWidth && pos.Y >= btnY && pos.Y <= btnY + DialogBtnHeight)
        {
            CancelDialog();
            return;
        }

        float relativeY = pos.Y + _scrollY;
        for (int i = 0; i < _flatList.Count; i++)
        {
            var (node, x, y) = _flatList[i];
            if (!node.IsDirectory) continue;

            if (MathF.Abs(y - relativeY) < ItemHeight * 0.7f)
            {
                if (pos.X >= x && pos.X <= x + 24f)
                {
                    node.IsExpanded = !node.IsExpanded;
                    RebuildFlatList();
                    RebuildTextAtlas();
                }
                else
                {
                    _selectedDestDir = node.FullPath;
                    _renderer.SetDirty();
                }
                return;
            }
        }
    }

    public void HandleMouseMove(Vector2 pos)
    {
        _mousePos = pos;
        
        if (_settingsPanel.IsOpen)
        {
            _settingsPanel.UpdateMousePos(pos);
            _renderer.SetDirty();
            return;
        }

        if (_dragPending && !_isDragging)
        {
            float dist = Vector2.Distance(_dragStartPos, pos);
            if (dist > DragThreshold)
            {
                _isDragging = true;
                _dragMousePos = pos;
                UpdateDropTarget(pos);
                _renderer.SetDirty();
            }
        }

        if (_isDragging)
        {
            _dragMousePos = pos;
            UpdateDropTarget(pos);
            _renderer.SetDirty();
        }
    }

    public void HandleMouseUp(Vector2 pos)
    {
        if (_settingsPanel.IsOpen)
        {
            _settingsPanel.HandleMouseUp(pos);
            return;
        }

        if (_isDragging && _dragNode != null)
        {
            if (_dropTargetPath != null)
            {
                if (_dragNode.IsDirectory)
                    _libraryManager.MoveDirectory(_dragNode.FullPath, _dropTargetPath);
                else
                    _libraryManager.MoveFile(_dragNode.FullPath, _dropTargetPath);
                RefreshTree();
            }
            _isDragging = false;
            _dragNode = null;
            _dropTargetPath = null;
            _dragPending = false;
            _renderer.SetDirty();
            return;
        }

        _dragPending = false;
    }

    public void CancelDrag()
    {
        _dragPending = false;
        _isDragging = false;
        _dragNode = null;
        _dropTargetPath = null;
        _renderer.SetDirty();
    }

    private void UpdateDropTarget(Vector2 pos)
    {
        _dropTargetPath = null;

        if (pos.X < 0 || pos.X > Width) return;

        float relativeY = pos.Y + _scrollY;
        foreach (var (node, x, y) in _flatList)
        {
            if (!node.IsDirectory) continue;
            if (node == _dragNode) continue;

            if (_dragNode != null && _dragNode.IsDirectory)
            {
                if (node.FullPath.StartsWith(_dragNode.FullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            if (MathF.Abs(y - relativeY) < ItemHeight * 0.5f)
            {
                _dropTargetPath = node.FullPath;
                break;
            }
        }
    }

    public void HandleScroll(float delta)
    {
        if (_settingsPanel.IsOpen)
        {
            _settingsPanel.HandleScroll(delta);
            _renderer.SetDirty();
            return;
        }

        if (_isDragging) return;
        if (_dialogMode != DialogMode.None && !IsTreeDialog && _dialogMode != DialogMode.Log) return;
        _scrollY -= delta * 28f;
        
        float maxScroll;
        if (_dialogMode == DialogMode.Log)
            maxScroll = _logLines.Count * 20f - 500;
        else
            maxScroll = _flatList.Count * ItemHeight - 500;
            
        _scrollY = Math.Clamp(_scrollY, 0, Math.Max(0, maxScroll));
    }
    
    public void OpenNewFileDialog()
    {
        _dialogMode = DialogMode.NewFile;
        _dialogTargetNode = null;
        _textBox.SetText("");
        _dialogTitle = Localization.Get("dialog_new_file_title", "Create New File?");
        _dialogMessage = Localization.Get("dialog_new_file_message", "Unsaved changes will be lost.");
        _dialogConfirmLabel = Localization.Get("dialog_button_create", "Create");
        _dialogCancelLabel = Localization.Get("dialog_button_cancel", "Cancel");
        RebuildTextAtlas();
    }

    public void Dispose() { }
}