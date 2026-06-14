using System.Numerics;
using MathBoard.Core;
using SkiaSharp;

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

    // SkiaSharp для текста
    private SKPaint _textPaint = new();
    private SKTypeface? _typeface;
    private SKSurface? _textSurface;
    private SKCanvas? _textCanvas;

    public LibraryPanel(StrokeRenderer renderer, LibraryManager libraryManager)
    {
        _renderer = renderer;
        _libraryManager = libraryManager;
        InitSkia();
        RefreshTree();
    }

    private void InitSkia()
    {
        _typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal) 
                   ?? SKTypeface.FromFamilyName("Arial") 
                   ?? SKTypeface.Default;

        _textPaint = new SKPaint
        {
            Typeface = _typeface,
            TextSize = 16f,
            Color = SKColors.WhiteSmoke,
            IsAntialias = true,
            SubpixelText = true,
            TextEncoding = SKTextEncoding.Utf8
        };
    }

    public void Toggle() { IsOpen = !IsOpen; _renderer.SetDirty(); }

    public void RefreshTree()
    {
        _rootNode = BuildTree(Settings.LibraryRootPath.Value);
        RebuildFlatList();
        _renderer.SetDirty();
    }

    private FileNode BuildTree(string path) {
        var root = new FileNode(Path.GetFileName(path) ?? "Lessons", path, true);

        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            return root;
        }

        foreach (var dir in Directory.GetDirectories(path))
            root.Children.Add(BuildTree(dir));

        foreach (var file in Directory.GetFiles(path, "*.mathboard"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            root.Children.Add(new FileNode(name, file, false));
        }

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
        if (!IsOpen) return;

        // Фон и заголовок (через вершины)
        DrawRect(vertices, Vector2.Zero, new Vector2(Width, screenSize.Y), new Vector4(0.06f, 0.06f, 0.085f, 0.985f));
        DrawRect(vertices, new Vector2(0, 0), new Vector2(Width, 68), new Vector4(0.11f, 0.11f, 0.15f, 1f));

        // Кнопка
        if (DrawButton(vertices, "💾 Сохранить как...", new Vector2(16, 78), new Vector2(Width - 32, 42)))
        {
            string name = $"Урок_{DateTime.Now:yyyy-MM-dd_HH-mm}";
            _libraryManager.SaveCanvas(name);
            RefreshTree();
        }

        // Текст через Skia (пока выводим в консоль + заглушка)
        foreach (var (node, x, y) in _flatList)
        {
            float screenY = y - _scrollY;
            if (screenY < 120 || screenY > screenSize.Y + 50) continue;

            string prefix = node.IsDirectory 
                ? (node.IsExpanded ? "▼ " : "▶ ") 
                : "   • ";

            DrawTextWithSkiaStub(vertices, prefix + node.Name, new Vector2(x, screenY));
        }
    }

    private bool DrawButton(List<Vertex> v, string text, Vector2 pos, Vector2 size)
    {
        DrawRect(v, pos, size, new Vector4(0.22f, 0.42f, 0.78f, 1f));
        DrawTextWithSkiaStub(v, text, pos + new Vector2(18, 12));
        return false;
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

    private void DrawTextWithSkiaStub(List<Vertex> vertices, string text, Vector2 pos)
    {
        // Временная заглушка — позже заменим на настоящую текстуру
        float w = text.Length * 9.5f;
        DrawRect(vertices, pos, new Vector2(w, 24), new Vector4(0,0,0,0.4f));
        Console.WriteLine($"[Text] {text} @ {pos}");
    }

    // ====================== КЛИКИ ======================
    public bool HandleClick(Vector2 pos)
    {
        if (!IsOpen || pos.X > Width) return false;

        float relativeY = pos.Y + _scrollY - 130;

        for (int i = 0; i < _flatList.Count; i++)
        {
            var (node, x, y) = _flatList[i];
            if (MathF.Abs(y - relativeY) < ItemHeight * 0.7f)
            {
                if (node.IsDirectory)
                {
                    node.IsExpanded = !node.IsExpanded;
                    RebuildFlatList();
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

    public void HandleScroll(float delta)
    {
        _scrollY -= delta * 28f;
        _scrollY = Math.Clamp(_scrollY, 0, Math.Max(0, _flatList.Count * ItemHeight - 500));
    }

    public void Dispose()
    {
        _textPaint.Dispose();
        _typeface?.Dispose();
        _textSurface?.Dispose();
    }
}