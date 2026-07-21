using System.Numerics;
using MathBoard.Core;
using Silk.NET.Maths;
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
    private LibraryManager _libraryManager;
    private LibraryPanel? _libraryPanel;

    private readonly Document _document = new();
    private readonly Camera _camera = new();

    private bool _framebufferResized;
    private bool _isRecreating;

    // --- Idle / dirty tracking ---
    private bool _sceneDirty = true;          // первый кадр всегда рисуем
    private Vector2D<int> _lastExtent;
    private DateTime _lastActivityTime = DateTime.UtcNow;

    private static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(0.5);

    public VulkanRenderer(IWindow window)
    {
        _context = new VulkanContext(window);
        _libraryManager = new LibraryManager(_document, null!);
    }

    public void MarkSceneDirty()
    {
        _sceneDirty = true;
        _lastActivityTime = DateTime.UtcNow;
    }

    public void MarkInputActivity()
    {
        _lastActivityTime = DateTime.UtcNow;
    }

    public void Initialize()
    {
        _context.Initialize();

        _swapchain = new SwapchainManager(_context);
        _swapchain.Initialize();

        _lastExtent = new Vector2D<int>(
            (int)_swapchain.Extent.Width,
            (int)_swapchain.Extent.Height);

        _camera.Position = new Vector2(_swapchain.Extent.Width / 2f, _swapchain.Extent.Height / 2f);

        _renderPassManager = new RenderPassManager(_context, _swapchain);
        _renderPassManager.Initialize();

        _framebufferManager = new FramebufferManager(_context, _swapchain, _renderPassManager);
        _framebufferManager.Initialize();

        _commandManager = new CommandManager(_context);
        _commandManager.Initialize((uint)_framebufferManager.Framebuffers.Count);

        _strokeRenderer = new StrokeRenderer(_context, _swapchain!, _renderPassManager!, _commandManager!, _document, _camera);
        _strokeRenderer.Initialize();
        _strokeRenderer.OnSceneChanged += MarkSceneDirty;

        _radialMenu = new RadialMenu(_strokeRenderer!);
        _strokeRenderer.SetRadialMenu(_radialMenu);

        _libraryManager = new LibraryManager(_document, _strokeRenderer!);

        _libraryPanel = new LibraryPanel(_strokeRenderer!, _libraryManager);
        _strokeRenderer.SetLibraryPanel(_libraryPanel);

        _inputManager = new InputManager(_context.Window, _strokeRenderer!, _camera, _document, _radialMenu, _libraryManager, _libraryPanel);
        _inputManager.OnActivity += MarkInputActivity;
        _inputManager.OnSceneChanged += MarkSceneDirty;

        // Подписываемся на ресайз окна — это гарантирует что рендер
        // запустится после изменения размера (bug 2)
        _context.Window.FramebufferResize += _ =>
        {
            _framebufferResized = true;
            _lastActivityTime = DateTime.UtcNow;
        };

        _commandManager.RecordCommandBuffers(
            _framebufferManager.Framebuffers,
            _renderPassManager.RenderPass,
            _swapchain.Extent,
            _strokeRenderer);

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

            _frameSync?.Dispose();
            _frameSync = null;

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
            _strokeRenderer!.SetDirty(); // UI вершины зависят от extent

            _lastExtent = new Vector2D<int>(
                (int)_swapchain.Extent.Width,
                (int)_swapchain.Extent.Height);

            _frameSync = new FrameSync(_context);
            _frameSync.Initialize();

            _sceneDirty = true;

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

        // ВСЕГДА обрабатываем ввод — нужно для обнаружения долгого нажатия (bug 1).
        // Update() очень дешёвый (проверка таймера).
        _inputManager?.Update();
        _libraryManager.AutoSaveIfNeeded();

        if (_framebufferResized)
            RecreateSwapchain();

        // Рендерим если: есть активность, геометрия грязная, или сцена грязная
        bool isActive = (DateTime.UtcNow - _lastActivityTime) < ActiveWindow;
        bool needsRender = isActive || _strokeRenderer!.IsDirty || _sceneDirty;

        if (!needsRender)
            return;

        // Ждём GPU перед обновлением буферов
        if (!_frameSync!.WaitForPreviousFrame())
            return;

        var currentExtent = new Vector2D<int>(
            (int)_swapchain!.Extent.Width,
            (int)_swapchain.Extent.Height);

        if (currentExtent != _lastExtent)
        {
            _strokeRenderer!.UpdateExtent(_swapchain.Extent);
            _strokeRenderer!.SetDirty();
            _lastExtent = currentExtent;
        }

        // Обновляем геометрию если грязная (только при изменении штрихов/UI,
        // НЕ при движении камеры — трансформация на GPU)
        if (_strokeRenderer!.IsDirty)
            _strokeRenderer.UpdateGeometry();

        // ВСЕГДА перезаписываем command buffers при рендере.
        // Это дёшево (~10-15 вызовов API на буфер) и автоматически
        // подхватывает изменение camera transform без отдельных флагов.
        _commandManager!.RecordCommandBuffers(
            _framebufferManager!.Framebuffers,
            _renderPassManager!.RenderPass,
            _swapchain.Extent,
            _strokeRenderer!);

        var result = _frameSync.SubmitFrame(_swapchain, _commandManager!);

        if (result == DrawFrameResult.NeedsRecreation)
            _framebufferResized = true;

        _sceneDirty = false;
    }

    public void Dispose()
    {
        _context.Vk.DeviceWaitIdle(_context.Device);
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