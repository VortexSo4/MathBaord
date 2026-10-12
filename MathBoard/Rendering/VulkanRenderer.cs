using System.Numerics;
using MathBoard.Core;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;

namespace MathBoard.Rendering;

public enum DrawFrameResult
{
    Success,
    SkipFrame,
    NeedsRecreation
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
    private LibraryManager? _libraryManager;
    private LibraryPanel? _libraryPanel;

    private readonly Document _document = new();
    private readonly Camera _camera = new();

    private bool _framebufferResized;
    private bool _disposed;

    public VulkanRenderer(IWindow window)
    {
        _context = new VulkanContext(window);
    }

    /// <summary>How long the main loop may block waiting for input before calling <see cref="Render"/> again.</summary>
    public double IdleTimeout
    {
        get
        {
            var size = _context.Window.FramebufferSize;
            if (size.X == 0 || size.Y == 0) return 0.5;
            if (_strokeRenderer?.NeedsRedraw == true) return 0;
            return _inputManager?.NeedsFrequentUpdates == true ? 1.0 / 60.0 : 0.5;
        }
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

        _strokeRenderer = new StrokeRenderer(_context, _swapchain, _renderPassManager, _commandManager, _document, _camera);
        _strokeRenderer.Initialize();

        _radialMenu = new RadialMenu(_strokeRenderer);
        _strokeRenderer.SetRadialMenu(_radialMenu);

        _libraryManager = new LibraryManager(_document, _strokeRenderer);

        _libraryPanel = new LibraryPanel(_strokeRenderer, _libraryManager);
        _strokeRenderer.SetLibraryPanel(_libraryPanel);

        _inputManager = new InputManager(_context.Window, _strokeRenderer, _camera, _document, _radialMenu, _libraryManager, _libraryPanel);
        _strokeRenderer.SetQuickBar(_inputManager.QuickBar);

        _context.Window.FramebufferResize += _ => _framebufferResized = true;

        _frameSync = new FrameSync(_context);
        _frameSync.Initialize();

        Console.WriteLine("VulkanRenderer initialized successfully");
    }

    private void RecreateSwapchain()
    {
        Console.WriteLine("[Recreate] Starting swapchain recreation...");

        _frameSync?.Dispose();
        _context.Vk.DeviceWaitIdle(_context.Device);

        _framebufferManager?.Dispose();
        _swapchain!.Recreate();

        _framebufferManager = new FramebufferManager(_context, _swapchain, _renderPassManager!);
        _framebufferManager.Initialize();

        _commandManager!.Recreate(
            (uint)_framebufferManager.Framebuffers.Count,
            _framebufferManager.Framebuffers,
            _renderPassManager!.RenderPass,
            _swapchain.Extent,
            _strokeRenderer!);

        _strokeRenderer!.UpdateExtent(_swapchain.Extent);

        _frameSync = new FrameSync(_context);
        _frameSync.Initialize();

        _framebufferResized = false;
        Console.WriteLine("[Recreate] Success");
    }

    /// <summary>Processes timers and draws a frame if anything changed. Returns false when nothing was drawn.</summary>
    public bool Render(float dt)
    {
        var size = _context.Window.FramebufferSize;
        if (size.X == 0 || size.Y == 0 || _strokeRenderer == null || _disposed)
            return false;

        _inputManager!.Update(dt);
        _libraryManager!.AutoSaveIfNeeded();

        if (_framebufferResized)
            RecreateSwapchain();

        if (!_strokeRenderer.NeedsRedraw)
            return false;

        if (!_frameSync!.WaitForPreviousFrame())
            return false;

        _strokeRenderer.PrepareFrame();

        _commandManager!.RecordCommandBuffers(
            _framebufferManager!.Framebuffers,
            _renderPassManager!.RenderPass,
            _swapchain!.Extent,
            _strokeRenderer);

        var result = _frameSync.SubmitFrame(_swapchain, _commandManager);
        if (result == DrawFrameResult.NeedsRecreation)
        {
            _framebufferResized = true;
            _strokeRenderer.SetDirty();
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _context.Vk.DeviceWaitIdle(_context.Device);
        _inputManager?.Dispose();
        _frameSync?.Dispose();
        _strokeRenderer?.Dispose();
        _commandManager?.Dispose();
        _framebufferManager?.Dispose();
        _renderPassManager?.Dispose();
        _swapchain?.Dispose();
        _context.Dispose();
    }
}
