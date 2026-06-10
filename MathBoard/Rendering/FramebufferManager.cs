using Silk.NET.Vulkan;

namespace MathBoard.Rendering;

public sealed unsafe class FramebufferManager : IDisposable
{
    private readonly VulkanContext _context;
    private readonly SwapchainManager _swapchain;
    private readonly RenderPassManager _renderPass;

    private Framebuffer[] _framebuffers = [];
    private Image[] _msaaImages = [];
    private DeviceMemory[] _msaaMemories = [];
    private ImageView[] _msaaImageViews = [];

    public FramebufferManager(
        VulkanContext context,
        SwapchainManager swapchain,
        RenderPassManager renderPass)
    {
        _context = context;
        _swapchain = swapchain;
        _renderPass = renderPass;
    }

    public void Initialize()
    {
        CreateFramebuffers();

        Console.WriteLine(
            $"Framebuffers created: {_framebuffers.Length}");
    }

    private void CreateFramebuffers()
    {
        var sampleCount = _context.GetSampleCount();
        _framebuffers = new Framebuffer[_swapchain.ImageViews.Count];
        _msaaImages = new Image[_swapchain.ImageViews.Count];
        _msaaMemories = new DeviceMemory[_swapchain.ImageViews.Count];
        _msaaImageViews = new ImageView[_swapchain.ImageViews.Count];

        var attachments = stackalloc ImageView[2];
        for (int i = 0; i < _swapchain.ImageViews.Count; i++)
        {
            // Создать мультисэмпловое изображение
            ImageCreateInfo imageInfo = new()
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = _swapchain.ImageFormat,
                Extent = new Extent3D(_swapchain.Extent.Width, _swapchain.Extent.Height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = sampleCount,
                Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransientAttachmentBit,
                Tiling = ImageTiling.Optimal,
                InitialLayout = ImageLayout.Undefined
            };
            _context.Vk.CreateImage(_context.Device, &imageInfo, null, out _msaaImages[i]);

            MemoryRequirements memReq;
            _context.Vk.GetImageMemoryRequirements(_context.Device, _msaaImages[i], &memReq);
            var allocInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = memReq.Size,
                MemoryTypeIndex = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
            };
            _context.Vk.AllocateMemory(_context.Device, &allocInfo, null, out _msaaMemories[i]);
            _context.Vk.BindImageMemory(_context.Device, _msaaImages[i], _msaaMemories[i], 0);

            // Создать image view для мультисэмплового изображения
            ImageViewCreateInfo viewInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _msaaImages[i],
                ViewType = ImageViewType.Type2D,
                Format = _swapchain.ImageFormat,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1)
            };
            _context.Vk.CreateImageView(_context.Device, &viewInfo, null, out _msaaImageViews[i]);

            // Создать фреймбуфер: два аттачмента (msaa, resolve)
            attachments[0] = _msaaImageViews[i];
            attachments[1] = _swapchain.ImageViews[i];

            FramebufferCreateInfo fbInfo = new()
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _renderPass.RenderPass,
                AttachmentCount = 2,
                PAttachments = attachments,
                Width = _swapchain.Extent.Width,
                Height = _swapchain.Extent.Height,
                Layers = 1
            };
            _context.Vk.CreateFramebuffer(_context.Device, &fbInfo, null, out _framebuffers[i]);
        }
    }

    private void DestroyFramebuffers()
    {
        foreach (var fb in _framebuffers)
            if (fb.Handle != 0) _context.Vk.DestroyFramebuffer(_context.Device, fb, null);
        _framebuffers = [];

        for (int i = 0; i < _msaaImageViews.Length; i++)
        {
            if (_msaaImageViews[i].Handle != 0)
                _context.Vk.DestroyImageView(_context.Device, _msaaImageViews[i], null);
            if (_msaaImages[i].Handle != 0)
                _context.Vk.DestroyImage(_context.Device, _msaaImages[i], null);
            if (_msaaMemories[i].Handle != 0)
                _context.Vk.FreeMemory(_context.Device, _msaaMemories[i], null);
        }
        _msaaImageViews = [];
        _msaaImages = [];
        _msaaMemories = [];
    }
    
    private uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        PhysicalDeviceMemoryProperties memProps;
        _context.Vk.GetPhysicalDeviceMemoryProperties(_context.PhysicalDevice, &memProps);
        for (int i = 0; i < memProps.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1u << i)) != 0 && (memProps.MemoryTypes[i].PropertyFlags & properties) == properties)
                return (uint)i;
        }
        throw new Exception("Failed to find suitable memory type");
    }

    public void Recreate()
    {
        DestroyFramebuffers();
        CreateFramebuffers();

        Console.WriteLine(
            $"Framebuffers recreated: {_framebuffers.Length}");
    }

    public IReadOnlyList<Framebuffer> Framebuffers => _framebuffers;

    public void Dispose()
    {
        DestroyFramebuffers();
    }
}