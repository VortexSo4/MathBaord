using MathBoard.Core;
using Silk.NET.Windowing;
using MathBoard.Rendering;
using Silk.NET.Maths;

string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MathBoard");
Directory.CreateDirectory(logDir);
using var logFile = File.CreateText(Path.Combine(logDir, "latest.log"));
Console.SetOut(logFile);
Console.SetError(logFile);

var options = WindowOptions.DefaultVulkan with
{
    Size = new Vector2D<int>(1280, 720),
    Title = "MathBoard"
};

var window = Window.Create(options);
window.FramebufferResize += _ => {};

var renderer = new VulkanRenderer(window);
Localization.Load(Settings.Language.Value);

window.Load += () => renderer.Initialize();
window.Render += delta => renderer.Render(delta);
window.Closing += () => renderer.Dispose();

window.Run();