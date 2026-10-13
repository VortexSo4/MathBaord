using System.Numerics;
using MathBoard.Core;
using MathBoard.Graphics;
using SkiaSharp;

namespace MathBoard.Rendering;

/// <summary>Saves the whole board as a PNG picture or a vector PDF, on the board's background color.</summary>
public static class BoardExporter
{
    private const float Margin = 40f;
    private const float PngScale = 2f;
    private const int MaxPngSide = 8000;

    /// <summary>Writes the board to <paramref name="path"/>, as PDF if it ends with ".pdf"; false if the board is empty.</summary>
    public static bool Export(Document document, string path)
    {
        if (StrokeRenderer.ContentBounds(document) is not var (min, max)) return false;
        var origin = min - new Vector2(Margin);
        var size = max - min + new Vector2(Margin * 2);

        if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.Create(path);
            using var pdf = SKDocument.CreatePdf(stream);
            Draw(pdf.BeginPage(size.X, size.Y), document, origin, 1f);
            pdf.EndPage();
            pdf.Close();
            return true;
        }

        float scale = MathF.Min(PngScale, MaxPngSide / MathF.Max(size.X, size.Y));
        using var surface = SKSurface.Create(new SKImageInfo((int)MathF.Ceiling(size.X * scale), (int)MathF.Ceiling(size.Y * scale)));
        Draw(surface.Canvas, document, origin, scale);
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        png.SaveTo(file);
        return true;
    }

    private static void Draw(SKCanvas canvas, Document document, Vector2 origin, float scale)
    {
        canvas.Clear(ToColor(Settings.Background));
        canvas.Scale(scale);
        canvas.Translate(-origin.X, -origin.Y);

        using var paint = new SKPaint { IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        var labels = new List<ObjectLabel>();
        var prims = new List<Prim>();

        // Same order as on the board: pictures, then shapes, then ink.
        foreach (var o in document.Objects.Where(o => o.Kind == ShapeKind.Image))
            DrawPicture(canvas, o);

        foreach (var o in document.Objects.Where(o => o.Kind != ShapeKind.Image))
        {
            prims.Clear();
            ObjectGeometry.Build(o, prims, labels);
            foreach (var p in prims)
            {
                paint.Color = ToColor(p.Color);
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = p.B.X * 2f;
                canvas.DrawLine(p.A.X, p.A.Y, p.A.Z, p.A.W, paint);
            }
        }

        paint.Style = SKPaintStyle.Stroke;
        foreach (var s in document.Strokes)
        {
            if (s.Points.Count == 0) continue;
            using var path = new SKPath();
            path.MoveTo(s.Points[0].X, s.Points[0].Y);
            foreach (var p in s.Points.Skip(1)) path.LineTo(p.X, p.Y);
            if (s.Points.Count == 1) path.LineTo(s.Points[0].X, s.Points[0].Y);
            paint.Color = ToColor(s.Color);
            paint.StrokeWidth = s.Width;
            canvas.DrawPath(path, paint);
        }

        // Labels are as large as on the board at 100 % zoom.
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), Theme.Small.Size);
        paint.Style = SKPaintStyle.Fill;
        foreach (var label in labels)
        {
            paint.Color = ToColor(label.Color);
            canvas.DrawText(label.Text, label.World.X, label.World.Y + font.Size * 0.35f, SKTextAlign.Center, font, paint);
        }
    }

    private static void DrawPicture(SKCanvas canvas, BoardObject o)
    {
        if (ImageStore.Get(o.Text) is not { } data) return;
        var info = new SKImageInfo(data.Width, data.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var image = SKImage.FromPixelCopy(info, data.Pixels);
        var p = o.Points;
        var u = p[1] - p[0];
        var v = p[2] - p[0];
        var local = new SKMatrix(u.X / data.Width, v.X / data.Height, p[0].X, u.Y / data.Width, v.Y / data.Height, p[0].Y, 0, 0, 1);
        canvas.Save();
        canvas.Concat(in local);
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        canvas.Restore();
    }

    private static SKColor ToColor(Vector4 c) => new(
        (byte)Math.Clamp(c.X * 255f + 0.5f, 0, 255), (byte)Math.Clamp(c.Y * 255f + 0.5f, 0, 255),
        (byte)Math.Clamp(c.Z * 255f + 0.5f, 0, 255), (byte)Math.Clamp(c.W * 255f + 0.5f, 0, 255));
}
