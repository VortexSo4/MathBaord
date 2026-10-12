using System.Numerics;
using MathBoard.Core;
using MathBoard.Graphics;
using SkiaSharp;

namespace MathBoard.Rendering;

/// <summary>
/// Shows a frozen capture of one monitor over the whole window; the region the user drags out is pasted onto the board
/// where it was selected. With several monitors a button switches between them.
/// </summary>
public sealed class ScreenshotOverlay(StrokeRenderer renderer) : IPopup
{
    private const float MinSelection = 8f;
    private static readonly Vector2 ButtonSize = new(48f);

    private ScreenCapture? _capture;
    private SKBitmap? _shot;
    private ImageData? _image;
    private int _monitor;
    private Vector2 _start, _end, _mouse;
    private bool _dragging;

    public bool IsOpen => _shot != null;

    /// <summary>The capture, fitted into the window, in logical screen space.</summary>
    public ImageDraw? Backdrop
    {
        get
        {
            if (_image == null || _shot == null) return null;
            // The whole capture is drawn; the parts of other monitors fall outside the window or under the bars.
            var (pos, scale) = Fit();
            var origin = pos - new Vector2(Monitor.Left, Monitor.Top) * scale;
            return new ImageDraw(_image, origin, new Vector2(_shot.Width * scale, 0), new Vector2(0, _shot.Height * scale));
        }
    }

    /// <summary>Takes ownership of <paramref name="capture"/>.</summary>
    public void Open(ScreenCapture capture)
    {
        Close();
        _capture = capture;
        _shot = capture.Bitmap;
        _monitor = capture.Current;
        _image = ImageStore.ToImageData(_shot);
        _dragging = false;
        renderer.SetDirty();
    }

    public void Close()
    {
        _capture?.Dispose();
        _capture = null;
        _shot = null;
        _image = null;
        _dragging = false;
        renderer.SetDirty();
    }

    private SKRectI Monitor => _capture!.Monitors[_monitor];

    /// <summary>Where the current monitor is shown and how much it is scaled to fit the window.</summary>
    private (Vector2 Pos, float Scale) Fit()
    {
        var screen = renderer.ScreenSize;
        var size = new Vector2(Monitor.Width, Monitor.Height);
        float scale = MathF.Min(screen.X / size.X, screen.Y / size.Y);
        return ((screen - size * scale) * 0.5f, scale);
    }

    private Vector2 CancelPos => new(renderer.ScreenSize.X - ButtonSize.X - 16f, 16f);
    private Vector2 MonitorPos => CancelPos - new Vector2(ButtonSize.X + 10f, 0);
    private bool HasMonitorButton => _capture?.Monitors.Length > 1;

    public void OnMouseDown(Vector2 pos)
    {
        _mouse = pos;
        if (UiBatch.Contains(CancelPos, ButtonSize, pos))
        {
            Close();
            return;
        }

        if (HasMonitorButton && UiBatch.Contains(MonitorPos, ButtonSize, pos))
        {
            _monitor = (_monitor + 1) % _capture!.Monitors.Length;
            renderer.SetDirty();
            return;
        }

        _start = _end = pos;
        _dragging = true;
        renderer.SetDirty();
    }

    public void OnMouseMove(Vector2 pos)
    {
        _mouse = pos;
        if (_dragging) _end = pos;
        renderer.SetDirty();
    }

    public void OnMouseUp(Vector2 pos)
    {
        if (!_dragging || _shot == null) return;
        _dragging = false;
        _end = pos;
        var min = Vector2.Min(_start, _end);
        var max = Vector2.Max(_start, _end);
        renderer.SetDirty();
        if (max.X - min.X < MinSelection || max.Y - min.Y < MinSelection) return;

        // Selection in capture pixels.
        var (origin, scale) = Fit();
        var monitorMin = new Vector2(Monitor.Left, Monitor.Top);
        var monitorMax = new Vector2(Monitor.Right, Monitor.Bottom);
        var a = Vector2.Clamp(monitorMin + (min - origin) / scale, monitorMin, monitorMax);
        var b = Vector2.Clamp(monitorMin + (max - origin) / scale, monitorMin, monitorMax);
        var rect = SKRectI.Round(new SKRect(a.X, a.Y, b.X, b.Y));
        if (rect.Width < 2 || rect.Height < 2) return;

        using var crop = new SKBitmap();
        if (_shot.ExtractSubset(crop, rect)) renderer.InsertImage(crop, (min + max) * 0.5f, max - min);
        Close();
    }

    public void Render(UiBatch ui, Vector2 screen)
    {
        if (_shot == null) return;
        var (pos, scale) = Fit();
        var size = new Vector2(Monitor.Width, Monitor.Height) * scale;
        var bar = new Vector4(0.06f, 0.06f, 0.07f, 1f);
        // Bars around the capture when its proportions differ from the window's.
        ui.Rect(Vector2.Zero, new Vector2(screen.X, pos.Y), bar);
        ui.Rect(new Vector2(0, pos.Y + size.Y), new Vector2(screen.X, screen.Y - pos.Y - size.Y), bar);
        ui.Rect(new Vector2(0, pos.Y), new Vector2(pos.X, size.Y), bar);
        ui.Rect(new Vector2(pos.X + size.X, pos.Y), new Vector2(screen.X - pos.X - size.X, size.Y), bar);

        var shade = new Vector4(0f, 0f, 0f, 0.45f);
        if (_dragging && Vector2.Distance(_start, _end) > 1f)
        {
            var min = Vector2.Min(_start, _end);
            var max = Vector2.Max(_start, _end);
            ui.Rect(Vector2.Zero, new Vector2(screen.X, min.Y), shade);
            ui.Rect(new Vector2(0, max.Y), new Vector2(screen.X, screen.Y - max.Y), shade);
            ui.Rect(new Vector2(0, min.Y), new Vector2(min.X, max.Y - min.Y), shade);
            ui.Rect(new Vector2(max.X, min.Y), new Vector2(screen.X - max.X, max.Y - min.Y), shade);
            ui.Border(min - new Vector2(1f), max - min + new Vector2(2f), Theme.Accent, 2f);
        }
        else
        {
            ui.Rect(Vector2.Zero, screen, shade);
        }

        string hint = Localization.Get("screenshot_hint", "Drag over the part of the screen to paste");
        float width = ui.Measure(hint, Theme.Label) + 40f;
        var hintPos = new Vector2((screen.X - width) * 0.5f, 16f);
        ui.Rect(hintPos, new Vector2(width, 44f), Theme.WithAlpha(Theme.Surface, 0.95f), 22f);
        ui.TextInRect(hint, hintPos, new Vector2(width, 44f), Theme.Text, Theme.Label, UiBatch.Align.Center);

        bool hover = UiBatch.Contains(CancelPos, ButtonSize, _mouse);
        ui.Rect(CancelPos, ButtonSize, hover ? Theme.Danger : Theme.WithAlpha(Theme.Surface, 0.95f), ButtonSize.X * 0.5f);
        ui.Icon(Icons.Close, CancelPos + ButtonSize * 0.5f, 18f, Theme.Text);

        if (!HasMonitorButton) return;
        hover = UiBatch.Contains(MonitorPos, ButtonSize, _mouse);
        ui.Rect(MonitorPos, ButtonSize, hover ? Theme.SurfaceHover : Theme.WithAlpha(Theme.Surface, 0.95f), ButtonSize.X * 0.5f);
        ui.Icon(Icons.Monitor, MonitorPos + new Vector2(ButtonSize.X * 0.5f, 20f), 18f, Theme.Text);
        ui.TextInRect((_monitor + 1) + "/" + _capture!.Monitors.Length, MonitorPos + new Vector2(0, 28f), new Vector2(ButtonSize.X, 16f),
            Theme.TextSecondary, Theme.Small, UiBatch.Align.Center);
    }
}
