using System.Numerics;
using MathBoard.Graphics;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace MathBoard.Backend.Vulkan;

public sealed unsafe class VulkanBackend : IRenderBackend
{
    private VulkanContext? _context;
    private SwapchainManager? _swapchain;
    private RenderPassManager? _renderPass;
    private FramebufferManager? _framebuffers;
    private DescriptorSetLayout _textureLayout;
    private Texture? _atlas;
    private readonly Dictionary<ImageData, Texture> _textures = new();
    private PrimitiveRenderer? _primitives;
    private PrimBuffer? _strokes;
    private PrimBuffer? _moving;
    private int _movingVersion = -1;
    private PrimBuffer? _ui;
    private PrimBuffer? _background;
    private PrimBuffer? _images;
    private PrimBuffer? _overlayImages;
    private Prim[] _imageScratch = [];

    private CommandPool _commandPool;
    private CommandBuffer _cmd;
    private Semaphore _imageAvailable;
    private Semaphore[] _renderFinished = [];
    private Fence _inFlight;
    private bool _resized;

    public string Name => "Vulkan";
    public int Priority => 300;

    public WindowOptions ConfigureWindow(WindowOptions options) =>
        options with { API = GraphicsAPI.DefaultVulkan, VSync = false };

    public void Initialize(IWindow window)
    {
        _context = new VulkanContext(window);
        _context.Initialize();

        _swapchain = new SwapchainManager(_context);
        _swapchain.Initialize();
        _renderPass = new RenderPassManager(_context, _swapchain);
        _renderPass.Initialize();
        _framebuffers = new FramebufferManager(_context, _swapchain, _renderPass);
        _framebuffers.Initialize();

        var vk = _context.Vk;
        var device = _context.Device;
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = _context.QueueFamilies.GraphicsFamily
        };
        vk.CreateCommandPool(device, &poolInfo, null, out _commandPool);
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };
        vk.AllocateCommandBuffers(device, &allocInfo, out _cmd);

        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        vk.CreateSemaphore(device, &semaphoreInfo, null, out _imageAvailable);
        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
        vk.CreateFence(device, &fenceInfo, null, out _inFlight);
        CreateRenderFinishedSemaphores();

        _textureLayout = Texture.CreateLayout(_context);
        _atlas = new Texture(_context, _textureLayout, Format.R8Unorm, 1, Filter.Nearest);
        _primitives = new PrimitiveRenderer(_context, _renderPass.RenderPass, _textureLayout);
        _strokes = new PrimBuffer(_context);
        _moving = new PrimBuffer(_context);
        _ui = new PrimBuffer(_context);
        _background = new PrimBuffer(_context);
        _images = new PrimBuffer(_context);
        _overlayImages = new PrimBuffer(_context);

        window.FramebufferResize += _ => _resized = true;
    }

    // One per swapchain image: the presentation engine may still hold the previous image's semaphore.
    private void CreateRenderFinishedSemaphores()
    {
        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        _renderFinished = new Semaphore[_swapchain!.ImageViews.Count];
        for (int i = 0; i < _renderFinished.Length; i++)
            _context!.Vk.CreateSemaphore(_context.Device, &semaphoreInfo, null, out _renderFinished[i]);
    }

    private void DestroyRenderFinishedSemaphores()
    {
        foreach (var s in _renderFinished) _context!.Vk.DestroySemaphore(_context.Device, s, null);
        _renderFinished = [];
    }

    private void RecreateSwapchain()
    {
        _context!.Vk.DeviceWaitIdle(_context.Device);
        DestroyRenderFinishedSemaphores();
        _framebuffers!.Dispose();
        _swapchain!.Recreate();
        _framebuffers = new FramebufferManager(_context, _swapchain, _renderPass!);
        _framebuffers.Initialize();
        CreateRenderFinishedSemaphores();
        _resized = false;
    }

    public bool Render(in FrameData frame)
    {
        var context = _context!;
        var size = context.Window.FramebufferSize;
        if (size.X == 0 || size.Y == 0) return false;
        if (_resized) RecreateSwapchain();

        var vk = context.Vk;
        var device = context.Device;
        var fence = _inFlight;
        if (vk.WaitForFences(device, 1, &fence, true, 1_000_000_000) != Result.Success) return false;

        _atlas!.Stage(frame.AtlasPixels, frame.AtlasSize, frame.AtlasSize, frame.AtlasDirtyTop, frame.AtlasDirtyBottom);
        _strokes!.Update(frame.Strokes, frame.StrokesDirtyFrom);
        if (frame.MovingVersion != _movingVersion)
        {
            _moving!.Update(frame.Moving, 0);
            _movingVersion = frame.MovingVersion;
        }

        _ui!.Update(frame.Ui, 0);
        _background!.Update(frame.Background, 0);
        UpdateTextures(frame);
        UpdateImages(_images!, frame.Images);
        UpdateImages(_overlayImages!, frame.OverlayImages);

        uint imageIndex = 0;
        var acquire = _swapchain!.KhrSwapchain.AcquireNextImage(device, _swapchain.Swapchain, 1_000_000_000,
            _imageAvailable, default, &imageIndex);
        if (acquire == Result.ErrorOutOfDateKhr)
        {
            _resized = true;
            return false;
        }

        if (acquire != Result.Success && acquire != Result.SuboptimalKhr) return false;
        if (acquire == Result.SuboptimalKhr) _resized = true;

        vk.ResetFences(device, 1, &fence);
        Record(frame, imageIndex);

        var cmd = _cmd;
        var waitSemaphore = _imageAvailable;
        var signalSemaphore = _renderFinished[imageIndex];
        var waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSemaphore,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &signalSemaphore
        };
        var submit = vk.QueueSubmit(context.GraphicsQueue, 1, &submitInfo, _inFlight);
        if (submit != Result.Success) throw new Exception($"QueueSubmit failed: {submit}");

        var swapchain = _swapchain.Swapchain;
        var presentInfo = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &signalSemaphore,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &imageIndex
        };
        var present = _swapchain.KhrSwapchain.QueuePresent(context.PresentQueue, &presentInfo);
        if (present is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr) _resized = true;
        return true;
    }

    /// <summary>Uploads new pictures and frees those no longer drawn; the previous frame has finished, so nothing uses them.</summary>
    private void UpdateTextures(in FrameData frame)
    {
        var used = new HashSet<ImageData>();
        foreach (var draw in frame.Images) used.Add(draw.Image);
        foreach (var draw in frame.OverlayImages) used.Add(draw.Image);

        foreach (var image in _textures.Keys.Where(i => !used.Contains(i)).ToList())
        {
            _textures[image].Dispose();
            _textures.Remove(image);
        }

        foreach (var image in used)
        {
            if (_textures.TryGetValue(image, out var texture))
            {
                texture.ReleaseStaging();
                continue;
            }

            texture = new Texture(_context!, _textureLayout, Format.R8G8B8A8Unorm, 4, Filter.Linear);
            texture.Stage(image.Pixels, image.Width, image.Height, 0, image.Height);
            _textures[image] = texture;
        }
    }

    private void UpdateImages(PrimBuffer buffer, ReadOnlySpan<ImageDraw> images)
    {
        if (_imageScratch.Length < images.Length) _imageScratch = new Prim[images.Length];
        for (int i = 0; i < images.Length; i++) _imageScratch[i] = images[i].ToPrim();
        buffer.Update(_imageScratch.AsSpan(0, images.Length), 0);
    }

    private void DrawImages(ReadOnlySpan<ImageDraw> images, PrimBuffer buffer, Matrix4x4 transform, float pixel)
    {
        if (images.Length == 0) return;
        for (int i = 0; i < images.Length; i++)
        {
            _primitives!.BindTexture(_cmd, _textures[images[i].Image].DescriptorSet);
            _primitives.Draw(_cmd, buffer, transform, pixel, i, 1);
        }

        _primitives!.BindTexture(_cmd, _atlas!.DescriptorSet);
    }

    private void Record(in FrameData frame, uint imageIndex)
    {
        var vk = _context!.Vk;
        vk.ResetCommandBuffer(_cmd, 0);
        var beginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };
        vk.BeginCommandBuffer(_cmd, &beginInfo);

        _atlas!.RecordUpload(_cmd);
        foreach (var texture in _textures.Values) texture.RecordUpload(_cmd);

        var extent = _swapchain!.Extent;
        var clear = new ClearValue
        {
            Color = new ClearColorValue(frame.ClearColor.X, frame.ClearColor.Y, frame.ClearColor.Z, frame.ClearColor.W)
        };
        var passInfo = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass!.RenderPass,
            Framebuffer = _framebuffers!.Framebuffers[(int)imageIndex],
            RenderArea = new Rect2D { Extent = extent },
            ClearValueCount = 1,
            PClearValues = &clear
        };
        vk.CmdBeginRenderPass(_cmd, &passInfo, SubpassContents.Inline);

        float scale = frame.UiScale;
        float zoom = frame.CameraZoom * scale;
        var cam = frame.CameraPosition * scale;
        // Vulkan clip space has y pointing down, so y = 0 maps to the top of the framebuffer.
        var proj = Matrix4x4.CreateOrthographicOffCenter(0, extent.Width, 0, extent.Height, -1f, 1f);
        var strokeTransform = Matrix4x4.CreateScale(zoom, zoom, 1f) * Matrix4x4.CreateTranslation(cam.X, cam.Y, 0f) * proj;
        var uiTransform = Matrix4x4.CreateScale(scale, scale, 1f) * proj;

        _primitives!.Bind(_cmd, extent, _atlas.DescriptorSet);
        _primitives.Draw(_cmd, _background!, uiTransform, 1f / scale);
        DrawImages(frame.Images, _images!, strokeTransform, 1f / zoom);
        _primitives.Draw(_cmd, _strokes!, strokeTransform, 1f / zoom);
        if (frame.Moving.Length > 0) _primitives.Draw(_cmd, _moving!, strokeTransform, 1f / zoom, move: frame.MovingTransform);
        DrawImages(frame.OverlayImages, _overlayImages!, uiTransform, 1f / scale);
        _primitives.Draw(_cmd, _ui!, uiTransform, 1f / scale);

        vk.CmdEndRenderPass(_cmd);
        vk.EndCommandBuffer(_cmd);
    }

    public void Dispose()
    {
        if (_context == null) return;
        var vk = _context.Vk;
        var device = _context.Device;
        if (device.Handle != 0)
        {
            vk.DeviceWaitIdle(device);
            _strokes?.Dispose();
            _moving?.Dispose();
            _ui?.Dispose();
            _background?.Dispose();
            _images?.Dispose();
            _overlayImages?.Dispose();
            _primitives?.Dispose();
            _atlas?.Dispose();
            foreach (var texture in _textures.Values) texture.Dispose();
            if (_textureLayout.Handle != 0) vk.DestroyDescriptorSetLayout(device, _textureLayout, null);
            if (_renderFinished.Length > 0) DestroyRenderFinishedSemaphores();
            if (_imageAvailable.Handle != 0) vk.DestroySemaphore(device, _imageAvailable, null);
            if (_inFlight.Handle != 0) vk.DestroyFence(device, _inFlight, null);
            if (_commandPool.Handle != 0) vk.DestroyCommandPool(device, _commandPool, null);
            _framebuffers?.Dispose();
            _renderPass?.Dispose();
            _swapchain?.Dispose();
        }

        _context.Dispose();
        _context = null;
    }
}
