using System.Diagnostics;
using MathBoard.Core;
using MathBoard.Rendering;
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

Settings.Load();
Localization.Load(Settings.Language.Value);

var options = WindowOptions.DefaultVulkan with
{
    Size = new Vector2D<int>(1280, 720),
    Title = "MathBoard"
};

var window = Window.Create(options);
var renderer = new VulkanRenderer(window);

window.Load += () =>
{
    renderer.Initialize();
    SetWindowIcon(window);
};
window.Closing += () => renderer.Dispose();

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
window.Initialize();
var glfw = GlfwProvider.GLFW.Value;
window.Run(() =>
{
    if (inFrame)
    {
        // Called from the window refresh callback while Windows is resizing the window.
        renderer.Render(NextDelta());
        return;
    }

    inFrame = true;
    window.DoEvents();
    if (!window.IsClosing && !renderer.Render(NextDelta()))
    {
        double timeout = renderer.IdleTimeout;
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
