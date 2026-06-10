using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace MathBoard.Rendering;

public sealed unsafe class SwapchainManager : IDisposable
{
    private readonly VulkanContext _context;

    private SwapchainKHR _swapchain;
    private Image[] _images = [];
    private ImageView[] _imageViews = [];

    private Format _imageFormat;
    private Extent2D _extent;

    public SwapchainManager(VulkanContext context)
    {
        _context = context;
    }

    public void Initialize()
    {
        CreateSwapchainResources();

        Console.WriteLine("SwapchainManager initialized successfully");
    }

    private void CreateSwapchain(
        SwapchainSupportDetails support,
        SurfaceFormatKHR surfaceFormat,
        PresentModeKHR presentMode,
        Extent2D extent)
    {
        uint imageCount = support.Capabilities.MinImageCount + 1;

        if (support.Capabilities.MaxImageCount > 0 && 
            imageCount > support.Capabilities.MaxImageCount)
        {
            imageCount = support.Capabilities.MaxImageCount;
        }

        SwapchainCreateInfoKHR createInfo = new()
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _context.Surface,

            MinImageCount = imageCount,
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = extent,

            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit,

            PreTransform = support.Capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,

            PresentMode = presentMode,
            Clipped = true
        };

        // У нас Graphics == Present, поэтому Exclusive
        createInfo.ImageSharingMode = SharingMode.Exclusive;

        var result = _context.KhrSwapchain.CreateSwapchain(
            _context.Device, 
            &createInfo, 
            null, 
            out _swapchain);

        if (result != Result.Success)
            throw new Exception($"CreateSwapchain failed: {result}");

        // Получаем изображения swapchain
        uint imageCountActual = 0;
        _context.KhrSwapchain.GetSwapchainImages(
            _context.Device, _swapchain, &imageCountActual, null);

        _images = new Image[imageCountActual];

        fixed (Image* imagesPtr = _images)
        {
            _context.KhrSwapchain.GetSwapchainImages(
                _context.Device, _swapchain, &imageCountActual, imagesPtr);
        }

        Console.WriteLine($"Swapchain images created: {_images.Length}");
    }

    private void CreateImageViews()
    {
        _imageViews = new ImageView[_images.Length];

        for (int i = 0; i < _images.Length; i++)
        {
            ImageViewCreateInfo createInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _images[i],
                ViewType = ImageViewType.Type2D,
                Format = _imageFormat,

                Components = new ComponentMapping(
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity),

                SubresourceRange = new ImageSubresourceRange(
                    ImageAspectFlags.ColorBit,
                    0, 1,
                    0, 1)
            };

            var result = _context.Vk.CreateImageView(
                _context.Device, 
                &createInfo, 
                null, 
                out _imageViews[i]);

            if (result != Result.Success)
                throw new Exception($"CreateImageView failed: {result}");
        }

        Console.WriteLine($"ImageViews created: {_imageViews.Length}");
    }

    private static SurfaceFormatKHR ChooseSurfaceFormat(SurfaceFormatKHR[] formats)
    {
        foreach (var format in formats)
        {
            if (format is { Format: Format.B8G8R8A8Unorm, ColorSpace: ColorSpaceKHR.SpaceSrgbNonlinearKhr }) return format;
        }
        return formats[0];
    }

    private static PresentModeKHR ChoosePresentMode(PresentModeKHR[] presentModes)
    {
        foreach (var mode in presentModes)
        {
            if (mode == PresentModeKHR.MailboxKhr)
                return mode;
        }
        return PresentModeKHR.FifoKhr;
    }

    private Extent2D ChooseExtent(SurfaceCapabilitiesKHR capabilities)
    {
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
            return capabilities.CurrentExtent;

        var windowSize = _context.Window.Size;

        return new Extent2D(
            (uint)windowSize.X,
            (uint)windowSize.Y);
    }

    public Format ImageFormat => _imageFormat;
    public Extent2D Extent => _extent;
    public IReadOnlyList<ImageView> ImageViews => _imageViews;
    public SwapchainKHR Swapchain => _swapchain;
    public KhrSwapchain KhrSwapchain => _context.KhrSwapchain;

    public void Dispose()
    {
        if (_imageViews.Length > 0)
        {
            foreach (var view in _imageViews)
            {
                if (view.Handle != 0)
                    _context.Vk.DestroyImageView(_context.Device, view, null);
            }
        }

        if (_swapchain.Handle != 0)
        {
            _context.KhrSwapchain.DestroySwapchain(_context.Device, _swapchain, null);
        }
    }
    
    public void Recreate()
    {
        DestroySwapchainResources();
        CreateSwapchainResources();
    }
    
    private void CreateSwapchainResources()
    {
        var support = _context.QuerySwapchainSupport();

        var surfaceFormat = ChooseSurfaceFormat(support.Formats);
        var presentMode = ChoosePresentMode(support.PresentModes);
        var extent = ChooseExtent(support.Capabilities);

        _imageFormat = surfaceFormat.Format;
        _extent = extent;

        CreateSwapchain(
            support,
            surfaceFormat,
            presentMode,
            extent);

        CreateImageViews();
    }
    
    private void DestroySwapchainResources()
    {
        foreach (var view in _imageViews)
        {
            if (view.Handle != 0)
                _context.Vk.DestroyImageView(
                    _context.Device,
                    view,
                    null);
        }

        _imageViews = [];

        if (_swapchain.Handle != 0)
        {
            _context.KhrSwapchain.DestroySwapchain(
                _context.Device,
                _swapchain,
                null);
        }

        _swapchain = default;
    }
}