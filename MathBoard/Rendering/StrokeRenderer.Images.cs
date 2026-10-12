using System.Numerics;
using System.Runtime.InteropServices;
using MathBoard.Core;
using MathBoard.Graphics;
using SkiaSharp;

namespace MathBoard.Rendering;

public sealed partial class StrokeRenderer
{
    // Pictures of the document, drawn by the backend below all other world primitives.
    private readonly List<ImageDraw> _images = new();
    private ImageDraw[] _imagesKey = [];
    private int _imagesVersion;
    private readonly ImageDraw[] _overlayImage = new ImageDraw[1];

    public ScreenshotOverlay Screenshot { get; }

    /// <summary>
    /// Stores the picture and puts it on the board, centered on <paramref name="screenCenter"/> (the middle of the view by
    /// default) and <paramref name="screenSize"/> large (one picture pixel per screen pixel by default), but at most 80 %
    /// of the view. The picture ends up selected.
    /// </summary>
    public void InsertImage(SKBitmap bitmap, Vector2? screenCenter = null, Vector2? screenSize = null)
    {
        string name = ImageStore.Add(bitmap);
        float left = _libraryPanel?.IsOpen == true ? _libraryPanel.Width : 0f;
        var view = new Vector2(ScreenSize.X - left, ScreenSize.Y);
        var size = screenSize ?? new Vector2(bitmap.Width, bitmap.Height) / UiScale;
        size *= MathF.Min(1f, MathF.Min(view.X * 0.8f / size.X, view.Y * 0.8f / size.Y));
        // Kept inside the visible part of the board.
        var half = size * 0.5f;
        var center = Vector2.Clamp(screenCenter ?? new Vector2(left + view.X * 0.5f, view.Y * 0.5f),
            new Vector2(left, 0) + half, ScreenSize - half);

        var origin = ScreenToWorld(center - size * 0.5f);
        var world = size / _camera.Zoom;
        var image = new BoardObject(ShapeKind.Image, [origin, origin + new Vector2(world.X, 0), origin + new Vector2(0, world.Y)],
            Vector4.One, 0f) { Text = name };

        EndStroke();
        CancelShape();
        _document.SaveState();
        _document.Objects.Add(image);
        SetTool(Tool.Select);
        ClearSelection();
        SelectedObjects.Add(image);
        MarkStrokesDirty();
    }

    /// <summary>Adds the object's picture to the frame; false if its file is missing and a placeholder is drawn instead.</summary>
    private bool AddImage(BoardObject o)
    {
        if (ImageStore.Get(o.Text) is not { } data) return false;
        var p = o.Points;
        _images.Add(new ImageDraw(data, p[0], p[1] - p[0], p[2] - p[0]));
        return true;
    }

    private void UpdateImagesVersion()
    {
        if (_imagesKey.AsSpan().SequenceEqual(CollectionsMarshal.AsSpan(_images))) return;
        _imagesKey = [.._images];
        _imagesVersion++;
        ImageStore.Retain(_document.Objects.Where(o => o.Kind == ShapeKind.Image).Select(o => o.Text));
    }

    private ReadOnlySpan<ImageDraw> OverlayImages()
    {
        if (Screenshot.Backdrop is not { } backdrop) return [];
        _overlayImage[0] = backdrop;
        return _overlayImage;
    }
}
