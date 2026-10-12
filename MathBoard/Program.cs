using System.Diagnostics;
using System.Runtime.InteropServices;
using MathBoard.Core;
using MathBoard.Graphics;
using Silk.NET.Core;
using Silk.NET.GLFW;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SkiaSharp;

string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MathBoard");
Directory.CreateDirectory(logDir);
var logFile = new StreamWriter(new FileStream(Path.Combine(logDir, "latest.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
{
    AutoFlush = true
};
Console.SetOut(logFile);
Console.SetError(logFile);
AppDomain.CurrentDomain.UnhandledException += (_, e) => Console.WriteLine($"Unhandled exception: {e.ExceptionObject}");

SystemInfo.Log();
Settings.Load();
Localization.Load(Settings.Language.Value);

var baseOptions = WindowOptions.Default with
{
    Size = new Vector2D<int>(1280, 720),
    Title = "MathBoard"
};

// Try backends in order and fall back to the next one if the window or the backend cannot start.
var backends = BackendLoader.Discover();
Settings.AvailableBackends = backends.Select(b => b.Name).ToList();
IWindow? window = null;
IRenderBackend? backend = null;
foreach (var candidate in BackendLoader.InTryOrder(backends, Settings.RenderBackend.Value))
{
    var attempt = Window.Create(candidate.ConfigureWindow(baseOptions));
    try
    {
        attempt.Initialize();
        candidate.Initialize(attempt);
        window = attempt;
        backend = candidate;
        Console.WriteLine($"Render backend: {candidate.Name}");
        break;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Render backend {candidate.Name} failed: {ex.Message}");
        candidate.Dispose();
        try { attempt.Reset(); } catch { /* the window may not have been created at all */ }
    }
}

foreach (var unused in backends.Where(b => b != backend)) unused.Dispose();

if (window == null || backend == null)
{
    Console.WriteLine("No render backend could start.");
    if (OperatingSystem.IsWindows())
        NativeMethods.MessageBox(0, "MathBoard could not start: no render backend works on this computer.\n" +
                                    "Details are in %AppData%\\MathBoard\\latest.log.", "MathBoard", 0x10);
    logFile.Dispose();
    return;
}

SetWindowIcon(window);
var app = new BoardApp(window, backend);
window.Closing += app.Dispose;

var clock = Stopwatch.StartNew();
double lastFrame = 0;
bool inFrame = false;

float NextDelta()
{
    double now = clock.Elapsed.TotalSeconds;
    float dt = (float)Math.Min(now - lastFrame, 0.1);
    lastFrame = now;
    return dt;
}

// Unlike the default loop, this one sleeps until input arrives or a timer is due,
// so an idle board costs no CPU instead of spinning a core.
var glfw = GlfwProvider.GLFW.Value;
window.Run(() =>
{
    if (inFrame)
    {
        // Called from the window refresh callback while Windows is resizing the window.
        app.Render(NextDelta());
        return;
    }

    inFrame = true;
    window.DoEvents();
    if (!window.IsClosing && !app.Render(NextDelta()))
    {
        double timeout = app.IdleTimeout;
        if (timeout > 0) glfw.WaitEventsTimeout(timeout);
    }

    inFrame = false;
});

window.DoEvents();
window.Reset();
logFile.Dispose();

static void SetWindowIcon(IWindow window)
{
    try
    {
        using var bitmap = SKBitmap.Decode(Path.Combine(AppContext.BaseDirectory, "resources", "icon.png"));
        using var rgba = bitmap.Copy(SKColorType.Rgba8888);
        var icon = new RawImage(rgba.Width, rgba.Height, rgba.Bytes);
        window.SetWindowIcon(ref icon);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Window icon not set: {ex.Message}");
    }
}

internal static partial class NativeMethods
{
    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MessageBox(nint hwnd, string text, string caption, uint type);
}
