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

    public VulkanRenderer(IWindow window)
    {
        _context = new VulkanContext(window);
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

        _commandManager.RecordCommandBuffers(
            _framebufferManager.Framebuffers,
            _renderPassManager.RenderPass,
            _swapchain.Extent);

        _frameSync = new FrameSync(_context);
        _frameSync.Initialize();

        Console.WriteLine("VulkanRenderer initialized successfully - First frame ready!");
    }

    public void Render(double delta)
    {
        _frameSync?.DrawFrame(_swapchain!, _commandManager!);
    }

    public void Dispose()
    {
        _frameSync?.Dispose();
        _commandManager?.Dispose();
        _framebufferManager?.Dispose();
        _renderPassManager?.Dispose();
        _swapchain?.Dispose();
        _context.Dispose();
    }
}