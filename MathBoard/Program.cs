using Silk.NET.Windowing;
using MathBoard.Rendering;
using Silk.NET.Maths;

var options = WindowOptions.DefaultVulkan with
{
    Size = new Vector2D<int>(1280, 720),
    Title = "MathBoard"
};

var window = Window.Create(options);

var renderer = new VulkanRenderer(window);

window.Load += () => renderer.Initialize();
window.Render += delta => renderer.Render(delta);
window.Closing += () => renderer.Dispose();

window.Run();