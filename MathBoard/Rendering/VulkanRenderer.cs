using MathBoard.Core;
using Silk.NET.Windowing;

namespace MathBoard.Rendering;

public sealed class VulkanRenderer : IDisposable
{
    private readonly VulkanContext _context;
    private SwapchainManager? _swapchain;
    private RenderPassManager? _renderPassManager;
    private FramebufferManager? _framebufferManager;
    private CommandManager? _commandManager;
    private FrameSync? _frameSync;
    private bool _framebufferResized;
    private StrokeRenderer? _strokeRenderer;
    private InputManager? _inputManager;

    public VulkanRenderer(IWindow window)
    {
        _context = new VulkanContext(window);

        window.FramebufferResize += _ =>
        {
            _framebufferResized = true;
        };
    }

    public void Initialize()
    {
        _context.Initialize();

        _swapchain = new SwapchainManager(_context);
        _swapchain.Initialize();

        _renderPassManager = new RenderPassManager(_context, _swapchain);
        _renderPassManager.Initialize();

        _framebufferManager = new FramebufferManager(_context, _swapchain, _renderPassManager);
        _framebufferManager.Initialize();

        _commandManager = new CommandManager(_context);
        _commandManager.Initialize((uint)_framebufferManager.Framebuffers.Count);

        _strokeRenderer = new StrokeRenderer(_context, _swapchain!, _renderPassManager!, _commandManager);
        _strokeRenderer.Initialize();
        
        _inputManager = new InputManager(_context.Window, _strokeRenderer!);

        _commandManager.RecordCommandBuffers(
            _framebufferManager.Framebuffers,
            _renderPassManager.RenderPass,
            _swapchain.Extent,
            _strokeRenderer);

        _frameSync = new FrameSync(_context);
        _frameSync.Initialize();

        Console.WriteLine("VulkanRenderer initialized successfully - First frame ready!");
    }
    
    private void RecreateSwapchain()
    {
        var size = _context.Window.FramebufferSize;

        if (size.X == 0 || size.Y == 0)
            return;

        _context.Vk.DeviceWaitIdle(_context.Device);

        _framebufferManager!.Dispose();

        _swapchain!.Recreate();

        _framebufferManager = new FramebufferManager(
            _context,
            _swapchain,
            _renderPassManager!);

        _framebufferManager.Initialize();

        _commandManager!.Recreate(
            (uint)_framebufferManager.Framebuffers.Count,
            _framebufferManager.Framebuffers,
            _renderPassManager!.RenderPass,
            _swapchain!.Extent,
            _strokeRenderer!);

        _framebufferResized = false;
        _strokeRenderer!.UpdateExtent(_swapchain.Extent);

        Console.WriteLine("Swapchain recreated");
    }

    public void Render(double delta)
    {
        if (_framebufferResized)
        {
            _context.Vk.DeviceWaitIdle(_context.Device);
            RecreateSwapchain();
        }

        _context.Vk.DeviceWaitIdle(_context.Device);

        // Обновляем размер в StrokeRenderer (на случай изменения окна)
        _strokeRenderer!.UpdateExtent(_swapchain!.Extent);
        _strokeRenderer.Flush();          // безопасное обновление буфера

        _commandManager!.RecordCommandBuffers(
            _framebufferManager!.Framebuffers,
            _renderPassManager!.RenderPass,
            _swapchain.Extent,
            _strokeRenderer);

        _frameSync?.DrawFrame(_swapchain!, _commandManager!);
    }

    public void Dispose()
    {
        _inputManager?.Dispose();
        _frameSync?.Dispose();
        _commandManager?.Dispose();
        _framebufferManager?.Dispose();
        _renderPassManager?.Dispose();
        _swapchain?.Dispose();
        _context.Dispose();
    }
}