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

    public enum DialogMode { None, Delete, Rename, Save }
    private DialogMode _dialogMode = DialogMode.None;
    private FileNode? _dialogTargetNode;
    
    // Продвинутое текстовое поле
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

    private readonly List<(FileNode node, bool isEdit, float x, float y, float w, float h)> _iconHitRegions = new();
    private Vector4 _saveButtonBounds;
    private Vector2 _screenSize;

    private TextAtlas.Entry _saveIconEntry, _editIconEntry, _deleteIconEntry;

    public bool IsDialogOpen => _dialogMode != DialogMode.None;

    public LibraryPanel(StrokeRenderer renderer, LibraryManager libraryManager)
    {
        _renderer = renderer;
        _libraryManager = libraryManager;
        _saveButtonBounds = new Vector4(16, 78, Width - 32, 42);
        RefreshTree();
    }

    public void Toggle() { IsOpen = !IsOpen; _renderer.SetDirty(); }

    public void RefreshTree()
    {
        _rootNode = BuildTree(Settings.LibraryRootPath.Value);
        RebuildFlatList();
        RebuildTextAtlas();
        _renderer.SetDirty();
    }

    private void RebuildTextAtlas()
    {
        var atlas = _renderer.TextAtlas;
        atlas.BeginBuild();
        
        atlas.Request(Localization.Get("panel_save_as"));
        atlas.Request(Localization.Get("dialog_save_title"));
        atlas.Request(Localization.Get("dialog_rename_title"));
        atlas.Request(Localization.Get("dialog_delete_title"));
        atlas.Request(Localization.Get("dialog_button_ok"));
        atlas.Request(Localization.Get("dialog_button_save"));
        atlas.Request(Localization.Get("dialog_button_cancel"));

        _saveIconEntry = atlas.RequestImage("resources/textures/save.png");
        _editIconEntry = atlas.RequestImage("resources/textures/edit.png");
        _deleteIconEntry = atlas.RequestImage("resources/textures/delete.png");

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
        FlattenTree(_rootNode, 24f, 140f);
    }

    private void FlattenTree(FileNode node, float x, float y)
    {
        _flatList.Add((node, x, y));
        if (node.IsDirectory && node.IsExpanded)
        {
            float childY = y + ItemHeight;
            foreach (var child in node.Children)
            {
                FlattenTree(child, x + 34, childY);
                childY += ItemHeight;
            }
        }
    }

    public void RenderToVertices(List<Vertex> vertices, Vector2 screenSize)
    {
        _screenSize = screenSize;
        if (!IsOpen) return;

        DrawRect(vertices, Vector2.Zero, new Vector2(Width, screenSize.Y), new Vector4(0.06f, 0.06f, 0.085f, 0.985f));
        DrawRect(vertices, Vector2.Zero, new Vector2(Width, 68), new Vector4(0.11f, 0.11f, 0.15f, 1f));

        _saveButtonBounds = new Vector4(16, 78, Width - 32, 42);
        
        // Кнопка сохранения с иконкой
        DrawRect(vertices, new Vector2(_saveButtonBounds.X, _saveButtonBounds.Y), new Vector2(_saveButtonBounds.Z, _saveButtonBounds.W), new Vector4(0.22f, 0.42f, 0.78f, 1f));
        var atlas = _renderer.TextAtlas;
        
        float iconSize = 24f;
        float iconY = _saveButtonBounds.Y + (_saveButtonBounds.W - iconSize) * 0.5f;
        atlas.EmitImage(_saveIconEntry, new Vector2(_saveButtonBounds.X + 12f, iconY), new Vector2(iconSize, iconSize), ButtonTextColor);
        
        var textSize = atlas.Measure(Localization.Get("panel_save_as"));
        atlas.Emit(Localization.Get("panel_save_as"), new Vector2(_saveButtonBounds.X + 48f, _saveButtonBounds.Y + (_saveButtonBounds.W - textSize.Y) * 0.5f), ButtonTextColor);

        _iconHitRegions.Clear();
        float iconAreaStart = Width - IconButtonSize * 2 - IconButtonMargin * 3;

        foreach (var (node, x, y) in _flatList)
        {
            float screenY = y - _scrollY;
            if (screenY < 120 || screenY > screenSize.Y + 50) continue;

            if (!node.IsDirectory)
            {
                float iY = screenY + (ItemHeight - IconButtonSize) * 0.5f;

                float delX = Width - IconButtonSize - IconButtonMargin;
                DrawIconButton(vertices, atlas, _deleteIconEntry, delX, iY, new Vector4(0.22f, 0.10f, 0.10f, 0.85f));
                _iconHitRegions.Add((node, false, delX, iY, IconButtonSize, IconButtonSize));

                float editX = delX - IconButtonSize - IconButtonMargin;
                DrawIconButton(vertices, atlas, _editIconEntry, editX, iY, new Vector4(0.10f, 0.12f, 0.20f, 0.85f));
                _iconHitRegions.Add((node, true, editX, iY, IconButtonSize, IconButtonSize));
            }

            string label = LabelFor(node);
            var size = atlas.Measure(label);
            atlas.Emit(label, new Vector2(x, screenY + (ItemHeight - size.Y) * 0.5f), TextColor);
        }

        if (_dialogMode != DialogMode.None)
            RenderDialog(vertices, atlas, screenSize);
    }

    private void DrawIconButton(List<Vertex> v, TextAtlas atlas, TextAtlas.Entry icon, float x, float y, Vector4 bg)
    {
        DrawRect(v, new Vector2(x, y), new Vector2(IconButtonSize, IconButtonSize), bg);
        if (icon.Width > 0)
        {
            float pad = 4f;
            atlas.EmitImage(icon, new Vector2(x + pad, y + pad), new Vector2(IconButtonSize - pad*2, IconButtonSize - pad*2), Vector4.One);
        }
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

            string text = TextBox.ToString();
            
            // Выделение текста
            if (TextBox.HasSelection)
            {
                string beforeSel = text.Substring(0, TextBox.SelStart);
                string selText = text.Substring(TextBox.SelStart, TextBox.SelEnd - TextBox.SelStart);
                
                var beforeSize = atlas.Measure(beforeSel);
                var selSize = atlas.Measure(selText);
                
                DrawRect(vertices, new Vector2(fieldX + 12f + beforeSize.X, yPos + 4f), new Vector2(selSize.X, fieldH - 8f), new Vector4(0.2f, 0.4f, 0.8f, 0.8f));
            }

            var inputSize = atlas.Measure(text);
            float textY = yPos + (fieldH - inputSize.Y) * 0.5f;
            atlas.Emit(text, new Vector2(fieldX + 12f, textY), TextColor);

            // Мигающий курсор
            bool showCursor = ((DateTime.Now - TextBox.LastEditTime).TotalMilliseconds % 1000) < 500;
            if (showCursor)
            {
                string beforeCursor = text.Substring(0, TextBox.CursorPos);
                var beforeSize = atlas.Measure(beforeCursor);
                float cursorX = fieldX + 12f + beforeSize.X + 2f;
                float cursorH = Math.Max(inputSize.Y - 6f, 12f);
                DrawRect(vertices, new Vector2(cursorX, yPos + (fieldH - cursorH) * 0.5f), new Vector2(2f, cursorH), new Vector4(0.9f, 0.9f, 0.95f, 1f));
            }
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
                _libraryManager.DeleteFile(_dialogTargetNode.FullPath);
                break;
            case DialogMode.Rename when _dialogTargetNode != null:
                if (!string.IsNullOrWhiteSpace(TextBox.ToString()))
                    _libraryManager.RenameFile(_dialogTargetNode.FullPath, TextBox.ToString());
                break;
        }

        _dialogMode = DialogMode.None;
        _dialogTargetNode = null;
        RefreshTree();
    }

    public void CancelDialog()
    {
        _dialogMode = DialogMode.None;
        _dialogTargetNode = null;
        _renderer.SetDirty();
    }

    public void HandleCharInput(char c)
    {
        if (_dialogMode != DialogMode.Rename && _dialogMode != DialogMode.Save) return;
        if (c < 32) return;
        if (c == '\\' || c == '/' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|') return;

        TextBox.Insert(c);
        RebuildTextAtlas();
    }

    public bool HandleClick(Vector2 pos)
    {
        if (_dialogMode != DialogMode.None) return HandleDialogClick(pos);
        if (!IsOpen || pos.X > Width) return false;

        if (pos.X >= _saveButtonBounds.X && pos.X <= _saveButtonBounds.X + _saveButtonBounds.Z &&
            pos.Y >= _saveButtonBounds.Y && pos.Y <= _saveButtonBounds.Y + _saveButtonBounds.W)
        {
            OpenSaveDialog();
            return true;
        }

        foreach (var (node, isEdit, x, y, w, h) in _iconHitRegions)
        {
            if (pos.X >= x && pos.X <= x + w && pos.Y >= y && pos.Y <= y + h)
            {
                if (isEdit) OpenRenameDialog(node);
                else OpenDeleteDialog(node);
                return true;
            }
        }

        float relativeY = pos.Y + _scrollY;
        for (int i = 0; i < _flatList.Count; i++)
        {
            var (node, x, y) = _flatList[i];
            if (MathF.Abs(y - relativeY) < ItemHeight * 0.7f)
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

    private bool HandleDialogClick(Vector2 pos)
    {
        float dx = (_screenSize.X - DialogWidth) * 0.5f;
        float dy = (_screenSize.Y - DialogHeight) * 0.5f;
        float btnY = dy + DialogHeight - DialogBtnHeight - 20f;

        float okX = dx + DialogWidth - DialogBtnWidth - 16;
        if (pos.X >= okX && pos.X <= okX + DialogBtnWidth && pos.Y >= btnY && pos.Y <= btnY + DialogBtnHeight)
        {
            ConfirmDialog();
            return true;
        }

        float cancelX = dx + DialogWidth - DialogBtnWidth * 2 - 28;
        if (pos.X >= cancelX && pos.X <= cancelX + DialogBtnWidth && pos.Y >= btnY && pos.Y <= btnY + DialogBtnHeight)
        {
            CancelDialog();
            return true;
        }

        return true;
    }

    public void HandleScroll(float delta)
    {
        if (_dialogMode != DialogMode.None) return;
        _scrollY -= delta * 28f;
        _scrollY = Math.Clamp(_scrollY, 0, Math.Max(0, _flatList.Count * ItemHeight - 500));
    }

    public void Dispose() {}
}