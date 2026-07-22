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

    private FileNode? _rootNode;
    private readonly List<(FileNode node, float x, float y)> _flatList = new();
    private float _scrollY = 0f;
    private const float ItemHeight = 36f;

    private static readonly Vector4 TextColor = new(0.93f, 0.93f, 0.95f, 1f);
    private static readonly Vector4 ButtonTextColor = Vector4.One;
    private static readonly Vector4 FileNameColor = new(0.95f, 0.78f, 0.55f, 1f);
    private static readonly Vector4 DropHighlightColor = new(0.4f, 0.78f, 1.0f, 0.95f);

    public enum DialogMode { None, Delete, Rename, Save, CreateFolder }
    private enum IconAction { Edit, Delete, Move }

    private DialogMode _dialogMode = DialogMode.None;
    private FileNode? _dialogTargetNode;

    public bool IsDialogOpen => _dialogMode != DialogMode.None;
    public bool IsTreeDialog => _dialogMode == DialogMode.CreateFolder;

    // Drag & Drop State
    private bool _dragPending = false;
    private Vector2 _dragStartPos;
    private FileNode? _dragNode;
    private bool _isDragging = false;
    private Vector2 _dragMousePos;
    private string? _dropTargetPath;
    private const float DragThreshold = 6f;

    public bool IsDragging => _isDragging;
    public bool HasPendingDrag => _dragPending;

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
                int removeCount = wholeWord ? GetWordLength(-1) : 1;
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
                int removeCount = wholeWord ? GetWordLength(1) : 1;
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

    public TextBoxState TextBox { get; } = new TextBoxState();

    private string _dialogTitle = "";
    private string _dialogConfirmLabel = "OK";
    private string _dialogCancelLabel = "Cancel";

    private const float DialogWidth = 380f;
    private const float DialogHeight = 210f;
    private const float DialogBtnWidth = 100f;
    private const float DialogBtnHeight = 36f;
    private const float IconButtonSize = 24f;
    private const float IconButtonMargin = 4f;

    private readonly List<(FileNode node, IconAction action, float x, float y, float w, float h)> _iconHitRegions = new();
    private Vector4 _saveButtonBounds;
    private Vector4 _createFolderButtonBounds;
    private string? _selectedDestDir;

    private Vector2 _screenSize;
    private TextAtlas.Entry _saveIconEntry, _editIconEntry, _deleteIconEntry, _moveIconEntry, _folderIconEntry;

    public LibraryPanel(StrokeRenderer renderer, LibraryManager libraryManager)
    {
        _renderer = renderer;
        _libraryManager = libraryManager;
        _saveButtonBounds = new Vector4(16, 78, Width - 32, 42);
        _createFolderButtonBounds = new Vector4(16, 126, Width - 32, 42);
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
        atlas.Request(Localization.Get("dialog_save_title"));
        atlas.Request(Localization.Get("dialog_rename_title"));
        atlas.Request(Localization.Get("dialog_delete_title"));
        atlas.Request(Localization.Get("dialog_create_folder_title"));
        atlas.Request(Localization.Get("dialog_button_ok"));
        atlas.Request(Localization.Get("dialog_button_save"));
        atlas.Request(Localization.Get("dialog_button_cancel"));

        _saveIconEntry = atlas.RequestImage("resources/textures/save.png");
        _editIconEntry = atlas.RequestImage("resources/textures/edit.png");
        _deleteIconEntry = atlas.RequestImage("resources/textures/delete.png");
        _moveIconEntry = atlas.RequestImage("resources/textures/move.png");
        _folderIconEntry = atlas.RequestImage("resources/textures/folder.png");

        _renderer.RequestRadialMenuIcons();

        foreach (var (node, _, _) in _flatList)
            atlas.Request(LabelFor(node));

        if (_dialogMode != DialogMode.None && !string.IsNullOrEmpty(TextBox.ToString()))
            atlas.Request(TextBox.ToString());

        if (_dialogMode == DialogMode.Delete && _dialogTargetNode != null)
            atlas.Request(_dialogTargetNode.Name);

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
        FlattenTree(_rootNode, 24f, 190f);
    }

    private float FlattenTree(FileNode node, float x, float y)
    {
        _flatList.Add((node, x, y));
        float nextY = y + ItemHeight;
        if (node.IsDirectory && node.IsExpanded)
        {
            foreach (var child in node.Children)
            {
                nextY = FlattenTree(child, x + 24, nextY); // Корректный отступ для вложенных
            }
        }
        return nextY;
    }

    public void RenderToVertices(List<Vertex> vertices, Vector2 screenSize)
    {
        _screenSize = screenSize;
        if (!IsOpen) return;

        DrawRect(vertices, Vector2.Zero, new Vector2(Width, screenSize.Y), new Vector4(0.06f, 0.06f, 0.085f, 0.985f));
        DrawRect(vertices, Vector2.Zero, new Vector2(Width, 68), new Vector4(0.11f, 0.11f, 0.15f, 1f));

        var atlas = _renderer.TextAtlas;

        if (_dialogMode != DialogMode.None && !IsTreeDialog)
        {
            RenderDialog(vertices, atlas, screenSize);
            return;
        }

        if (!IsTreeDialog)
            RenderSaveAndFolderButtons(vertices, atlas);

        RenderFileList(vertices, atlas, IsTreeDialog);

        if (IsTreeDialog)
            RenderTreeDialogUI(vertices, atlas, screenSize);

        if (_isDragging && _dragNode != null)
            RenderDragPreview(vertices, atlas);
    }

    private void RenderSaveAndFolderButtons(List<Vertex> vertices, TextAtlas atlas)
    {
        _saveButtonBounds = new Vector4(16, 78, Width - 32, 42);
        DrawRect(vertices, new Vector2(_saveButtonBounds.X, _saveButtonBounds.Y), new Vector2(_saveButtonBounds.Z, _saveButtonBounds.W), new Vector4(0.22f, 0.42f, 0.78f, 1f));

        float iconSize = 24f;
        float iconY = _saveButtonBounds.Y + (_saveButtonBounds.W - iconSize) * 0.5f;
        atlas.EmitImage(_saveIconEntry, new Vector2(_saveButtonBounds.X + 12f, iconY), new Vector2(iconSize, iconSize), ButtonTextColor);

        var textSize = atlas.Measure(Localization.Get("panel_save_as"));
        atlas.Emit(Localization.Get("panel_save_as"), new Vector2(_saveButtonBounds.X + 48f, _saveButtonBounds.Y + (_saveButtonBounds.W - textSize.Y) * 0.5f), ButtonTextColor);

        _createFolderButtonBounds = new Vector4(16, 126, Width - 32, 42);
        DrawRect(vertices, new Vector2(_createFolderButtonBounds.X, _createFolderButtonBounds.Y), new Vector2(_createFolderButtonBounds.Z, _createFolderButtonBounds.W), new Vector4(0.18f, 0.38f, 0.28f, 1f));

        iconY = _createFolderButtonBounds.Y + (_createFolderButtonBounds.W - iconSize) * 0.5f;
        atlas.EmitImage(_folderIconEntry, new Vector2(_createFolderButtonBounds.X + 12f, iconY), new Vector2(iconSize, iconSize), ButtonTextColor);

        textSize = atlas.Measure(Localization.Get("panel_create_folder"));
        atlas.Emit(Localization.Get("panel_create_folder"), new Vector2(_createFolderButtonBounds.X + 48f, _createFolderButtonBounds.Y + (_createFolderButtonBounds.W - textSize.Y) * 0.5f), ButtonTextColor);
    }

    private void RenderFileList(List<Vertex> vertices, TextAtlas atlas, bool isTreeDialog)
    {
        _iconHitRegions.Clear();

        foreach (var (node, x, y) in _flatList)
        {
            float screenY = y - _scrollY;
            if (screenY < 120 || screenY > _screenSize.Y + 50) continue;

            if (isTreeDialog)
            {
                if (!node.IsDirectory) continue;

                if (_selectedDestDir == node.FullPath)
                    DrawRect(vertices, new Vector2(0, screenY), new Vector2(Width, ItemHeight), new Vector4(0.3f, 0.5f, 0.8f, 0.35f));

                string label = LabelFor(node);
                var size = atlas.Measure(label);
                atlas.Emit(label, new Vector2(x, screenY + (ItemHeight - size.Y) * 0.5f), TextColor);
            }
            else
            {
                if (_isDragging && _dropTargetPath != null && _dropTargetPath == node.FullPath)
                    DrawRectOutline(vertices, new Vector2(0, screenY), new Vector2(Width, ItemHeight), DropHighlightColor, 2f);

                if (node != _rootNode)
                {
                    float iY = screenY + (ItemHeight - IconButtonSize) * 0.5f;

                    float delX = Width - IconButtonSize - IconButtonMargin;
                    DrawIconButton(vertices, atlas, _deleteIconEntry, delX, iY, new Vector4(0.22f, 0.10f, 0.10f, 0.85f));
                    _iconHitRegions.Add((node, IconAction.Delete, delX, iY, IconButtonSize, IconButtonSize));

                    float editX = delX - IconButtonSize - IconButtonMargin;
                    DrawIconButton(vertices, atlas, _editIconEntry, editX, iY, new Vector4(0.10f, 0.12f, 0.20f, 0.85f));
                    _iconHitRegions.Add((node, IconAction.Edit, editX, iY, IconButtonSize, IconButtonSize));

                    float moveX = editX - IconButtonSize - IconButtonMargin;
                    DrawIconButton(vertices, atlas, _moveIconEntry, moveX, iY, new Vector4(0.10f, 0.18f, 0.12f, 0.85f));
                    _iconHitRegions.Add((node, IconAction.Move, moveX, iY, IconButtonSize, IconButtonSize));
                }

                string label = LabelFor(node);
                var size = atlas.Measure(label);
                atlas.Emit(label, new Vector2(x, screenY + (ItemHeight - size.Y) * 0.5f), TextColor);
            }
        }
    }

    private void RenderDragPreview(List<Vertex> vertices, TextAtlas atlas)
    {
        string label = LabelFor(_dragNode!);
        var labelSize = atlas.Measure(label);

        float padding = 12f;
        float previewW = Math.Max(labelSize.X + padding * 2, 120f);
        float previewH = ItemHeight;
        float previewX = _dragMousePos.X - previewW * 0.5f;
        float previewY = _dragMousePos.Y - previewH * 0.5f;

        DrawRect(vertices, new Vector2(previewX, previewY), new Vector2(previewW, previewH), new Vector4(0.15f, 0.15f, 0.20f, 0.90f));
        DrawRectOutline(vertices, new Vector2(previewX, previewY), new Vector2(previewW, previewH), new Vector4(0.4f, 0.78f, 1.0f, 0.8f), 1.5f);

        if (labelSize.X > 0)
            atlas.Emit(label, new Vector2(previewX + padding, previewY + (previewH - labelSize.Y) * 0.5f), TextColor);
    }

    private void RenderTreeDialogUI(List<Vertex> vertices, TextAtlas atlas, Vector2 screenSize)
    {
        var titleSize = atlas.Measure(_dialogTitle);
        atlas.Emit(_dialogTitle, new Vector2((Width - titleSize.X) * 0.5f, 24), TextColor);

        if (_dialogMode == DialogMode.CreateFolder)
        {
            float fieldX = 16f;
            float fieldW = Width - 32f;
            float fieldH = 38f;
            float fieldY = 78f;

            DrawRect(vertices, new Vector2(fieldX, fieldY), new Vector2(fieldW, fieldH), new Vector4(0.05f, 0.05f, 0.07f, 1f));
            DrawRectOutline(vertices, new Vector2(fieldX, fieldY), new Vector2(fieldW, fieldH), new Vector4(0.30f, 0.32f, 0.40f, 1f), 1.5f);

            RenderTextInput(vertices, atlas, new Vector2(fieldX, fieldY), new Vector2(fieldW, fieldH));
        }

        float btnY = screenSize.Y - DialogBtnHeight - 20f;
        float cancelX = 16;
        DrawDialogButton(vertices, atlas, _dialogCancelLabel, cancelX, btnY, new Vector4(0.20f, 0.20f, 0.25f, 1f));

        float okX = Width - DialogBtnWidth - 16;
        DrawDialogButton(vertices, atlas, _dialogConfirmLabel, okX, btnY, new Vector4(0.22f, 0.42f, 0.78f, 1f));
    }

    private void RenderDialog(List<Vertex> vertices, TextAtlas atlas, Vector2 screenSize)
    {
        DrawRect(vertices, Vector2.Zero, screenSize, new Vector4(0, 0, 0, 0.55f));

        float dx = (screenSize.X - DialogWidth) * 0.5f;
        float dy = (screenSize.Y - DialogHeight) * 0.5f;

        DrawRect(vertices, new Vector2(dx, dy), new Vector2(DialogWidth, DialogHeight), new Vector4(0.10f, 0.10f, 0.14f, 0.98f));
        DrawRectOutline(vertices, new Vector2(dx, dy), new Vector2(DialogWidth, DialogHeight), new Vector4(0.35f, 0.37f, 0.45f, 1f), 2f);

        float yPos = dy + 28f;
        var titleSize = atlas.Measure(_dialogTitle);
        atlas.Emit(_dialogTitle, new Vector2(dx + (DialogWidth - titleSize.X) * 0.5f, yPos), TextColor);
        yPos += 38f;

        if (_dialogMode == DialogMode.Rename || _dialogMode == DialogMode.Save)
        {
            float fieldX = dx + 28f;
            float fieldW = DialogWidth - 56f;
            float fieldH = 38f;

            DrawRect(vertices, new Vector2(fieldX, yPos), new Vector2(fieldW, fieldH), new Vector4(0.05f, 0.05f, 0.07f, 1f));
            DrawRectOutline(vertices, new Vector2(fieldX, yPos), new Vector2(fieldW, fieldH), new Vector4(0.30f, 0.32f, 0.40f, 1f), 1.5f);

            RenderTextInput(vertices, atlas, new Vector2(fieldX, yPos), new Vector2(fieldW, fieldH));
        }
        else if (_dialogMode == DialogMode.Delete && _dialogTargetNode != null)
        {
            var nameSize = atlas.Measure(_dialogTargetNode.Name);
            atlas.Emit(_dialogTargetNode.Name, new Vector2(dx + (DialogWidth - nameSize.X) * 0.5f, yPos), FileNameColor);
        }

        float btnY = dy + DialogHeight - DialogBtnHeight - 20f;
        float cancelX = dx + DialogWidth - DialogBtnWidth * 2 - 28;
        DrawDialogButton(vertices, atlas, _dialogCancelLabel, cancelX, btnY, new Vector4(0.20f, 0.20f, 0.25f, 1f));

        float okX = dx + DialogWidth - DialogBtnWidth - 16;
        DrawDialogButton(vertices, atlas, _dialogConfirmLabel, okX, btnY, new Vector4(0.22f, 0.42f, 0.78f, 1f));
    }

    private void RenderTextInput(List<Vertex> vertices, TextAtlas atlas, Vector2 fieldPos, Vector2 fieldSize)
    {
        string text = TextBox.ToString();
        float fieldX = fieldPos.X;
        float fieldY = fieldPos.Y;
        float fieldW = fieldSize.X;
        float fieldH = fieldSize.Y;

        if (TextBox.HasSelection)
        {
            string beforeSel = text.Substring(0, TextBox.SelStart);
            string selText = text.Substring(TextBox.SelStart, TextBox.SelEnd - TextBox.SelStart);

            var beforeSize = atlas.Measure(beforeSel);
            var selSize = atlas.Measure(selText);

            DrawRect(vertices, new Vector2(fieldX + 12f + beforeSize.X, fieldY + 4f), new Vector2(selSize.X, fieldH - 8f), new Vector4(0.2f, 0.4f, 0.8f, 0.8f));
        }

        var inputSize = atlas.Measure(text);
        float textY = fieldY + (fieldH - inputSize.Y) * 0.5f;
        atlas.Emit(text, new Vector2(fieldX + 12f, textY), TextColor);

        bool showCursor = ((DateTime.Now - TextBox.LastEditTime).TotalMilliseconds % 1000) < 500;
        if (showCursor)
        {
            string beforeCursor = text.Substring(0, TextBox.CursorPos);
            var beforeSize = atlas.Measure(beforeCursor);
            float cursorX = fieldX + 12f + beforeSize.X + 2f;
            float cursorH = Math.Max(inputSize.Y - 6f, 12f);
            DrawRect(vertices, new Vector2(cursorX, fieldY + (fieldH - cursorH) * 0.5f), new Vector2(2f, cursorH), new Vector4(0.9f, 0.9f, 0.95f, 1f));
        }
    }

    private void DrawIconButton(List<Vertex> v, TextAtlas atlas, TextAtlas.Entry icon, float x, float y, Vector4 bg)
    {
        DrawRect(v, new Vector2(x, y), new Vector2(IconButtonSize, IconButtonSize), bg);
        if (icon.Width > 0)
        {
            float pad = 4f;
            atlas.EmitImage(icon, new Vector2(x + pad, y + pad), new Vector2(IconButtonSize - pad * 2, IconButtonSize - pad * 2), Vector4.One);
        }
    }

    private void DrawDialogButton(List<Vertex> v, TextAtlas atlas, string text, float x, float y, Vector4 bg)
    {
        DrawRect(v, new Vector2(x, y), new Vector2(DialogBtnWidth, DialogBtnHeight), bg);
        var size = atlas.Measure(text);
        atlas.Emit(text, new Vector2(x + (DialogBtnWidth - size.X) * 0.5f, y + (DialogBtnHeight - size.Y) * 0.5f), ButtonTextColor);
    }

    private static void DrawRectOutline(List<Vertex> v, Vector2 pos, Vector2 size, Vector4 color, float thickness)
    {
        DrawRect(v, pos, new Vector2(size.X, thickness), color);
        DrawRect(v, new Vector2(pos.X, pos.Y + size.Y - thickness), new Vector2(size.X, thickness), color);
        DrawRect(v, pos, new Vector2(thickness, size.Y), color);
        DrawRect(v, new Vector2(pos.X + size.X - thickness, pos.Y), new Vector2(thickness, size.Y), color);
    }

    private static void DrawRect(List<Vertex> v, Vector2 pos, Vector2 size, Vector4 color)
    {
        var p1 = pos; var p2 = pos + new Vector2(size.X, 0);
        var p3 = pos + size; var p4 = pos + new Vector2(0, size.Y);

        v.Add(new Vertex { Position = p1, Color = color });
        v.Add(new Vertex { Position = p2, Color = color });
        v.Add(new Vertex { Position = p3, Color = color });
        v.Add(new Vertex { Position = p1, Color = color });
        v.Add(new Vertex { Position = p3, Color = color });
        v.Add(new Vertex { Position = p4, Color = color });
    }

    public void OpenSaveDialog()
    {
        _dialogMode = DialogMode.Save;
        _dialogTargetNode = null;
        TextBox.SetText($"Lesson_{DateTime.Now:yyyy-MM-dd_HH-mm}");
        _dialogTitle = Localization.Get("dialog_save_title");
        _dialogConfirmLabel = Localization.Get("dialog_button_save");
        _dialogCancelLabel = Localization.Get("dialog_button_cancel");
        RebuildTextAtlas();
    }

    public void OpenRenameDialog(FileNode node)
    {
        _dialogMode = DialogMode.Rename;
        _dialogTargetNode = node;
        TextBox.SetText(node.Name);
        _dialogTitle = Localization.Get("dialog_rename_title");
        _dialogConfirmLabel = Localization.Get("dialog_button_ok");
        _dialogCancelLabel = Localization.Get("dialog_button_cancel");
        RebuildTextAtlas();
    }

    public void OpenDeleteDialog(FileNode node)
    {
        _dialogMode = DialogMode.Delete;
        _dialogTargetNode = node;
        TextBox.SetText("");
        _dialogTitle = Localization.Get("dialog_delete_title");
        _dialogConfirmLabel = Localization.Get("dialog_button_ok");
        _dialogCancelLabel = Localization.Get("dialog_button_cancel");
        RebuildTextAtlas();
    }

    public void OpenCreateFolderDialog()
    {
        _dialogMode = DialogMode.CreateFolder;
        _dialogTargetNode = null;
        _selectedDestDir = Settings.LibraryRootPath.Value;
        TextBox.SetText($"NewFolder_{DateTime.Now:HHmm}");
        _dialogTitle = Localization.Get("dialog_create_folder_title");
        _dialogConfirmLabel = Localization.Get("dialog_button_ok");
        _dialogCancelLabel = Localization.Get("dialog_button_cancel");
        RebuildTextAtlas();
    }

    public void ConfirmDialog()
    {
        if (_dialogMode == DialogMode.None) return;

        switch (_dialogMode)
        {
            case DialogMode.Save:
                if (!string.IsNullOrWhiteSpace(TextBox.ToString()))
                    _libraryManager.SaveCanvas(TextBox.ToString());
                break;
            case DialogMode.Delete when _dialogTargetNode != null:
                if (_dialogTargetNode.IsDirectory)
                    _libraryManager.DeleteDirectory(_dialogTargetNode.FullPath);
                else
                    _libraryManager.DeleteFile(_dialogTargetNode.FullPath);
                break;
            case DialogMode.Rename when _dialogTargetNode != null:
                if (!string.IsNullOrWhiteSpace(TextBox.ToString()))
                {
                    if (_dialogTargetNode.IsDirectory)
                        _libraryManager.RenameDirectory(_dialogTargetNode.FullPath, TextBox.ToString());
                    else
                        _libraryManager.RenameFile(_dialogTargetNode.FullPath, TextBox.ToString());
                }
                break;
            case DialogMode.CreateFolder when _selectedDestDir != null:
                if (!string.IsNullOrWhiteSpace(TextBox.ToString()))
                    _libraryManager.CreateFolder(_selectedDestDir, TextBox.ToString());
                break;
        }

        _dialogMode = DialogMode.None;
        _dialogTargetNode = null;
        _selectedDestDir = null;
        RefreshTree();
    }

    public void CancelDialog()
    {
        _dialogMode = DialogMode.None;
        _dialogTargetNode = null;
        _selectedDestDir = null;
        _renderer.SetDirty();
    }

    public void HandleCharInput(char c)
    {
        if (_dialogMode != DialogMode.Rename && _dialogMode != DialogMode.Save && _dialogMode != DialogMode.CreateFolder) return;
        if (c < 32) return;
        if (c == '\\' || c == '/' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|') return;

        TextBox.Insert(c);
        RefreshDialogText();
    }

    public bool HandleMouseDown(Vector2 pos)
    {
        // Диалог приоритетнее всего
        if (IsDialogOpen)
        {
            HandleDialogClick(pos);
            return true;
        }

        _dragPending = false;
        _dragNode = null;

        if (!IsOpen || pos.X > Width) return false;

        // 1. Кнопки (Сохранить / Создать папку)
        if (HitTest(_saveButtonBounds, pos)) { OpenSaveDialog(); return true; }
        if (HitTest(_createFolderButtonBounds, pos)) { OpenCreateFolderDialog(); return true; }

        // 2. Иконки (Переименовать / Удалить / Переместить)
        foreach (var (node, action, x, y, w, h) in _iconHitRegions)
        {
            if (pos.X >= x && pos.X <= x + w && pos.Y >= y && pos.Y <= y + h)
            {
                if (action == IconAction.Edit) OpenRenameDialog(node);
                else if (action == IconAction.Delete) OpenDeleteDialog(node);
                else if (action == IconAction.Move)
                {
                    // Начинаем Drag&Drop
                    _dragPending = true;
                    _dragStartPos = pos;
                    _dragNode = node;
                }
                return true;
            }
        }

        // 3. Клики по самим строкам (раскрыть папку или загрузить файл)
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

    private void HandleDialogClick(Vector2 pos)
    {
        if (IsTreeDialog)
        {
            HandleTreeDialogClick(pos);
            return;
        }

        float dx = (_screenSize.X - DialogWidth) * 0.5f;
        float dy = (_screenSize.Y - DialogHeight) * 0.5f;
        float btnY = dy + DialogHeight - DialogBtnHeight - 20f;

        float okX = dx + DialogWidth - DialogBtnWidth - 16;
        if (pos.X >= okX && pos.X <= okX + DialogBtnWidth && pos.Y >= btnY && pos.Y <= btnY + DialogBtnHeight)
        {
            ConfirmDialog();
            return;
        }

        float cancelX = dx + DialogWidth - DialogBtnWidth * 2 - 28;
        if (pos.X >= cancelX && pos.X <= cancelX + DialogBtnWidth && pos.Y >= btnY && pos.Y <= btnY + DialogBtnHeight)
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
        // Завершение Drag&Drop
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

            // Нельзя бросить папку в её собственного потомка
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
        if (_isDragging) return;
        if (_dialogMode != DialogMode.None && !IsTreeDialog) return;
        _scrollY -= delta * 28f;
        _scrollY = Math.Clamp(_scrollY, 0, Math.Max(0, _flatList.Count * ItemHeight - 500));
    }

    public void Dispose() { }
}