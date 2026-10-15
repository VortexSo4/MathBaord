using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using MathBoard.Graphics;
using MathBoard.Rendering;
using Silk.NET.GLFW;
using Silk.NET.Windowing;

namespace MathBoard.Core;

/// <summary>Owns the document and the UI and hands finished frames to the chosen render backend.</summary>
public sealed partial class BoardApp : IDisposable
{
    private readonly IWindow _window;
    private readonly IRenderBackend _backend;
    private readonly Document _document = new();
    private readonly Camera _camera = new();
    private readonly StrokeRenderer _strokeRenderer;
    private readonly LibraryManager _libraryManager;
    private readonly InputManager _inputManager;
    private readonly Updater _updater = new();
    private bool _renderFailed;

    public BoardApp(IWindow window, IRenderBackend backend)
    {
        _window = window;
        _backend = backend;

        _strokeRenderer = new StrokeRenderer(_document, _camera);
        UpdateViewport();
        _camera.Position = _strokeRenderer.ScreenSize / 2f;

        var radialMenu = new RadialMenu(_strokeRenderer);
        _strokeRenderer.SetRadialMenu(radialMenu);
        _libraryManager = new LibraryManager(_document, _strokeRenderer);
        var libraryPanel = new LibraryPanel(_strokeRenderer, _libraryManager, _updater);
        _strokeRenderer.SetLibraryPanel(libraryPanel);
        _inputManager = new InputManager(window, _strokeRenderer, _camera, _document, radialMenu, _libraryManager, libraryPanel, _updater);
        _strokeRenderer.SetQuickBar(_inputManager.QuickBar);

        window.FramebufferResize += _ => _strokeRenderer.SetDirty();
        _libraryManager.OpenLastFile();
        libraryPanel.RefreshTree();

        _updater.Changed += () =>
        {
            _inputManager.QuickBar.LibraryBadge = _updater.Status == Updater.State.Available;
            _strokeRenderer.SetDirty();
            GlfwProvider.GLFW.Value.PostEmptyEvent();
        };
        if (Settings.CheckUpdates) _updater.Check();
    }

    /// <summary>How long the main loop may block waiting for input before calling <see cref="Render"/> again.</summary>
    public double IdleTimeout
    {
        get
        {
            var size = _window.FramebufferSize;
            if (size.X == 0 || size.Y == 0) return 0.5;
            if (_renderFailed) return 1.0 / 60.0;
            if (_strokeRenderer.NeedsRedraw) return 0;
            return _inputManager.NeedsFrequentUpdates ? 1.0 / 60.0 : 0.5;
        }
    }

    /// <summary>Processes timers and draws a frame if anything changed. Returns false when nothing was drawn.</summary>
    public bool Render(float dt)
    {
        var size = _window.FramebufferSize;
        if (size.X == 0 || size.Y == 0) return false;

        // The installer waits for the files to be released; Closing saves the document.
        if (_updater.Status == Updater.State.Installing)
        {
            _window.Close();
            return false;
        }

        UpdateViewport();
        _inputManager.Update(dt);
        _libraryManager.AutoSaveIfNeeded();
        if (!_strokeRenderer.NeedsRedraw) return false;

        long prepareStart = Stopwatch.GetTimestamp();
        _strokeRenderer.PrepareFrame();
        long backendStart = Stopwatch.GetTimestamp();
        _renderFailed = !_backend.Render(_strokeRenderer.GetFrame());
        if (Perf.Enabled)
            Perf.Frame(Stopwatch.GetElapsedTime(prepareStart, backendStart).TotalMilliseconds, Stopwatch.GetElapsedTime(backendStart).TotalMilliseconds);
        if (_renderFailed) return false;
        _strokeRenderer.OnPresented();
        return true;
    }

    /// <summary>
    /// Shows the window, created hidden, once everything is loaded and a frame is drawn, so it never appears blank.
    /// The frame is drawn again right away: a hidden window may drop what was presented to it (the software backend's GDI).
    /// </summary>
    public void Show()
    {
        Render(0f);
        _window.IsVisible = true;
        _strokeRenderer.SetDirty();
        Render(0f);
    }

    private void UpdateViewport()
    {
        var size = _window.FramebufferSize;
        if (size.X > 0 && size.Y > 0)
            _strokeRenderer.UpdateViewport(new Vector2(size.X, size.Y), DisplayScale());
    }

    /// <summary>The user's interface scale, or the monitor's DPI scale when set to automatic.</summary>
    private float DisplayScale()
    {
        if (Settings.UiScale > 0) return Settings.UiScale;
        if (OperatingSystem.IsWindows() && _window.Native?.Win32 is { } win32)
        {
            uint dpi = GetDpiForWindow(win32.Hwnd);
            if (dpi > 0) return dpi / 96f;
        }

        return 1f;
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    public void Dispose()
    {
        _libraryManager.SaveOnExit();
        _inputManager.Dispose();
        _backend.Dispose();
        _strokeRenderer.Dispose();
    }
}
