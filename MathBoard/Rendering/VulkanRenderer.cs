using System.Numerics;
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
            _context.Vk.DeviceWaitIdle(_context.Device);

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

        bool success = _frameSync!.DrawFrame(_swapchain, _commandManager);

        if (!success)
        {
            RecreateSwapchain();

            _commandManager.RecordCommandBuffers(
                _framebufferManager.Framebuffers,
                _renderPassManager.RenderPass,
                _swapchain.Extent,
                _strokeRenderer);

            _frameSync.DrawFrame(_swapchain, _commandManager);
        }
    }

    public void Dispose()
    {
        _context.Vk.DeviceWaitIdle(_context.Device); // на всякий случай
        _inputManager?.Dispose();
        _frameSync?.Dispose();
        _commandManager?.Dispose();
        _framebufferManager?.Dispose();
        _renderPassManager?.Dispose();
        _swapchain?.Dispose();
        _context.Dispose();
    }
}