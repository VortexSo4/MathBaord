using System.Numerics;
using MathBoard.Core;
using Silk.NET.Windowing;

namespace MathBoard.Rendering;

public enum DrawFrameResult
{
    Success,
    SkipFrame,       // timeout или окно не готово — просто пропустить
    NeedsRecreation  // реально нужно пересоздать swapchain
}

public sealed class VulkanRenderer : IDisposable
{
    private readonly VulkanContext _context;
    private SwapchainManager? _swapchain;
    private RenderPassManager? _renderPassManager;
    private FramebufferManager? _framebufferManager;
    private CommandManager? _commandManager;
    private FrameSync? _frameSync;

    private StrokeRenderer? _strokeRenderer;
    private InputManager? _inputManager;
    private RadialMenu? _radialMenu;
    private LibraryManager _libraryManager;

    private readonly Document _document = new();
    private readonly Camera _camera = new();

    private bool _framebufferResized;
    private bool _isRecreating;

    public VulkanRenderer(IWindow window)
    {
        _context = new VulkanContext(window);
        _libraryManager = new LibraryManager(_document, null!);
    }

    public void Initialize()
    {
        _context.Initialize();

        _swapchain = new SwapchainManager(_context);
        _swapchain.Initialize();

        _camera.Position = new Vector2(_swapchain.Extent.Width / 2f, _swapchain.Extent.Height / 2f);

        _renderPassManager = new RenderPassManager(_context, _swapchain);
        _renderPassManager.Initialize();

        _framebufferManager = new FramebufferManager(_context, _swapchain, _renderPassManager);
        _framebufferManager.Initialize();

        _commandManager = new CommandManager(_context);
        _commandManager.Initialize((uint)_framebufferManager.Framebuffers.Count);

        _strokeRenderer = new StrokeRenderer(_context, _swapchain!, _renderPassManager!, _commandManager!, _document, _camera);
        _strokeRenderer.Initialize();

        _radialMenu = new RadialMenu(_strokeRenderer!);
        _strokeRenderer.SetRadialMenu(_radialMenu);

        _libraryManager = new LibraryManager(_document, _strokeRenderer!);

        _inputManager = new InputManager(_context.Window, _strokeRenderer!, _camera, _document, _radialMenu, _libraryManager);

        _commandManager.RecordCommandBuffers(_framebufferManager.Framebuffers, _renderPassManager.RenderPass, _swapchain.Extent, _strokeRenderer);

        _frameSync = new FrameSync(_context);
        _frameSync.Initialize();

        Console.WriteLine("VulkanRenderer initialized successfully");
    }

    private void RecreateSwapchain()
    {
        if (_isRecreating) return;
        _isRecreating = true;

        try
        {
            Console.WriteLine("[Recreate] Starting swapchain recreation...");
        
            // Уничтожаем старый FrameSync, чтобы не ждать на его fence
            _frameSync?.Dispose();
            _frameSync = null;
        
            // Теперь ожидаем завершения всех операций – но если GPU завис, мы уже уничтожили объекты,
            // и DeviceWaitIdle может всё равно зависнуть. Поэтому лучше сначала попробовать сбросить очередь.
            // В идеале – пересоздать весь Vulkan-контекст, но это сложно.
            // Как компромисс – установим таймаут через внешний таймер (например, Task.Run), но это уже усложнение.
            // Пока оставим так, но добавим защиту от двойного входа.
        
            _context.Vk.DeviceWaitIdle(_context.Device); // всё ещё рискованно, но теперь вызывается только при накоплении таймаутов

            _framebufferManager?.Dispose();
            _swapchain?.Recreate();

            _framebufferManager = new FramebufferManager(_context, _swapchain!, _renderPassManager!);
            _framebufferManager.Initialize();

            _commandManager!.Recreate(
                (uint)_framebufferManager.Framebuffers.Count,
                _framebufferManager.Framebuffers,
                _renderPassManager!.RenderPass,
                _swapchain!.Extent,
                _strokeRenderer!);

            _strokeRenderer!.UpdateExtent(_swapchain.Extent);

            // Пересоздаём FrameSync
            _frameSync = new FrameSync(_context);
            _frameSync.Initialize();

            Console.WriteLine("[Recreate] Success");
        }
        finally
        {
            _framebufferResized = false;
            _isRecreating = false;
        }
    }

    public void Render(double delta)
    {
        var size = _context.Window.FramebufferSize;
        if (size.X == 0 || size.Y == 0)
            return;

        if (_framebufferResized)
            RecreateSwapchain();

        _inputManager?.Update();
        _libraryManager.AutoSaveIfNeeded();
        _strokeRenderer!.UpdateGeometry();
        _strokeRenderer.UpdateExtent(_swapchain!.Extent);

        _commandManager!.RecordCommandBuffers(
            _framebufferManager!.Framebuffers,
            _renderPassManager!.RenderPass,
            _swapchain.Extent,
            _strokeRenderer);

        var result = _frameSync!.DrawFrame(_swapchain, _commandManager);

        switch (result)
        {
            case DrawFrameResult.NeedsRecreation:
                _framebufferResized = true;
                break;
            case DrawFrameResult.SkipFrame:
                break; // просто пропускаем
            case DrawFrameResult.Success:
                break;
        }
    }

    public void Dispose()
    {
        _context.Vk.DeviceWaitIdle(_context.Device); // на всякий случай
        _inputManager?.Dispose();
        _context.Vk.QueueWaitIdle(_context.GraphicsQueue);
        _context.Vk.QueueWaitIdle(_context.PresentQueue);
        _frameSync?.Dispose();
        _commandManager?.Dispose();
        _framebufferManager?.Dispose();
        _renderPassManager?.Dispose();
        _swapchain?.Dispose();
        _context.Dispose();
    }
}