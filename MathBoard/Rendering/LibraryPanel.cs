using System.Numerics;
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

    private const string SaveButtonLabel = "\uD83D\uDCBE Сохранить как..."; // 💾 Сохранить как...
    private static readonly Vector4 TextColor = new(0.93f, 0.93f, 0.95f, 1f);
    private static readonly Vector4 ButtonTextColor = Vector4.One;

    public LibraryPanel(StrokeRenderer renderer, LibraryManager libraryManager)
    {
        _renderer = renderer;
        _libraryManager = libraryManager;
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

    // Атлас перестраивается ТОЛЬКО здесь — при изменении дерева файлов (появился/
    // пропал автосейв, развернули папку и т.д.), а НЕ каждый кадр. RenderToVertices
    // ниже просто штампует уже готовые textured quads по закэшированным UV — это
    // и даёт "чтобы летало": сам рендер текста в кадре ничего не растеризует.
    private void RebuildTextAtlas()
    {
        var atlas = _renderer.TextAtlas;
        atlas.BeginBuild();

        atlas.Request(SaveButtonLabel);

        foreach (var (node, _, _) in _flatList)
            atlas.Request(LabelFor(node));

        atlas.EndBuild();
    }

    private static string LabelFor(FileNode node)
    {
        string prefix = node.IsDirectory
            ? (node.IsExpanded ? "▼ " : "▶ ")
            : "   • ";
        return prefix + node.Name;
    }

    private FileNode BuildTree(string path)
    {
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

        // Фон и заголовок (через вершины — как раньше)
        DrawRect(vertices, Vector2.Zero, new Vector2(Width, screenSize.Y), new Vector4(0.06f, 0.06f, 0.085f, 0.985f));
        DrawRect(vertices, new Vector2(0, 0), new Vector2(Width, 68), new Vector4(0.11f, 0.11f, 0.15f, 1f));

        // Кнопка
        if (DrawButton(vertices, SaveButtonLabel, new Vector2(16, 78), new Vector2(Width - 32, 42)))
        {
            string name = $"Урок_{DateTime.Now:yyyy-MM-dd_HH-mm}";
            _libraryManager.SaveCanvas(name);
            RefreshTree();
        }

        // Реальный текст через text atlas (не стаб)
        var atlas = _renderer.TextAtlas;
        foreach (var (node, x, y) in _flatList)
        {
            float screenY = y - _scrollY;
            if (screenY < 120 || screenY > screenSize.Y + 50) continue;

            string label = LabelFor(node);
            var size = atlas.Measure(label);
            float textY = screenY + (ItemHeight - size.Y) * 0.5f;
            atlas.Emit(label, new Vector2(x, textY), TextColor);
        }
    }

    private bool DrawButton(List<Vertex> v, string text, Vector2 pos, Vector2 size)
    {
        DrawRect(v, pos, size, new Vector4(0.22f, 0.42f, 0.78f, 1f));

        var atlas = _renderer.TextAtlas;
        var textSize = atlas.Measure(text);
        var textPos = pos + new Vector2(18, (size.Y - textSize.Y) * 0.5f);
        atlas.Emit(text, textPos, ButtonTextColor);

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

    // ====================== КЛИКИ ======================
    public bool HandleClick(Vector2 pos)
    {
        if (!IsOpen || pos.X > Width) return false;

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

    public void HandleScroll(float delta)
    {
        _scrollY -= delta * 28f;
        _scrollY = Math.Clamp(_scrollY, 0, Math.Max(0, _flatList.Count * ItemHeight - 500));
    }

    public void Dispose()
    {
        // Ресурсы текста (шрифт, GPU-текстура и т.д.) владеет TextAtlas внутри StrokeRenderer,
        // он и освобождается там же.
    }
}