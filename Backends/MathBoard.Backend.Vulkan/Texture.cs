using Silk.NET.Vulkan;

namespace MathBoard.Backend.Vulkan;

/// <summary>
/// A sampled image with its own descriptor set: the R8 glyph atlas or a picture. Changed rows are staged on the CPU and
/// copied inside the frame's command buffer.
/// </summary>
public sealed unsafe class Texture : IDisposable
{
    private readonly VulkanContext _context;
    private readonly Format _format;
    private readonly int _bytesPerPixel;

    private Image _image;
    private DeviceMemory _imageMemory;
    private ImageView _imageView;
    private Sampler _sampler;
    private Silk.NET.Vulkan.Buffer _staging;
    private DeviceMemory _stagingMemory;
    private byte* _stagingData;
    private DescriptorPool _descriptorPool;

    private int _width, _height;
    private bool _initialized;
    private int _pendingTop = int.MaxValue, _pendingBottom;

    public DescriptorSet DescriptorSet { get; }

    public static DescriptorSetLayout CreateLayout(VulkanContext context)
    {
        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding
        };
        context.Vk.CreateDescriptorSetLayout(context.Device, &layoutInfo, null, out var layout);
        return layout;
    }

    public Texture(VulkanContext context, DescriptorSetLayout layout, Format format, int bytesPerPixel, Filter filter)
    {
        _context = context;
        _format = format;
        _bytesPerPixel = bytesPerPixel;

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = filter,
            MinFilter = filter,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MipmapMode = SamplerMipmapMode.Nearest
        };
        _context.Vk.CreateSampler(_context.Device, &samplerInfo, null, out _sampler);

        var poolSize = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 1 };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
            MaxSets = 1
        };
        _context.Vk.CreateDescriptorPool(_context.Device, &poolInfo, null, out _descriptorPool);

        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout
        };
        _context.Vk.AllocateDescriptorSets(_context.Device, &allocInfo, out var set);
        DescriptorSet = set;
    }

    /// <summary>Stages changed rows. Call only after the previous frame has finished.</summary>
    public void Stage(ReadOnlySpan<byte> pixels, int width, int height, int dirtyTop, int dirtyBottom)
    {
        if (width != _width || height != _height)
        {
            DestroyImage();
            CreateImage(width, height);
            dirtyTop = 0;
            dirtyBottom = height;
        }

        if (dirtyBottom <= dirtyTop) return;
        if (_stagingData == null) CreateStaging();
        int row = width * _bytesPerPixel;
        int offset = dirtyTop * row, length = (dirtyBottom - dirtyTop) * row;
        pixels.Slice(offset, length).CopyTo(new Span<byte>(_stagingData + offset, length));
        _pendingTop = Math.Min(_pendingTop, dirtyTop);
        _pendingBottom = Math.Max(_pendingBottom, dirtyBottom);
    }

    public void RecordUpload(CommandBuffer cmd)
    {
        if (_pendingBottom <= _pendingTop) return;

        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = _initialized ? ImageLayout.ShaderReadOnlyOptimal : ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcAccessMask = _initialized ? AccessFlags.ShaderReadBit : 0,
            DstAccessMask = AccessFlags.TransferWriteBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1)
        };
        _context.Vk.CmdPipelineBarrier(cmd, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &barrier);

        // A fresh image is copied whole so that unused areas never contain garbage.
        int top = _initialized ? _pendingTop : 0;
        int rows = _initialized ? _pendingBottom - _pendingTop : _height;
        var region = new BufferImageCopy
        {
            BufferOffset = (ulong)(top * _width * _bytesPerPixel),
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D(0, top, 0),
            ImageExtent = new Extent3D((uint)_width, (uint)rows, 1)
        };
        _context.Vk.CmdCopyBufferToImage(cmd, _staging, _image, ImageLayout.TransferDstOptimal, 1, &region);

        barrier.OldLayout = ImageLayout.TransferDstOptimal;
        barrier.NewLayout = ImageLayout.ShaderReadOnlyOptimal;
        barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
        barrier.DstAccessMask = AccessFlags.ShaderReadBit;
        _context.Vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
            0, 0, null, 0, null, 1, &barrier);

        _initialized = true;
        _pendingTop = int.MaxValue;
        _pendingBottom = 0;
    }

    /// <summary>Frees the CPU copy once it has been uploaded; for textures that never change. Call after the frame finished.</summary>
    public void ReleaseStaging()
    {
        if (!_initialized || _pendingBottom > _pendingTop || _stagingData == null) return;
        DestroyStaging();
    }

    private void CreateImage(int width, int height)
    {
        _width = width;
        _height = height;
        _initialized = false;

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D((uint)width, (uint)height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Format = _format,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            Samples = SampleCountFlags.Count1Bit
        };
        VulkanContext.Check(_context.Vk.CreateImage(_context.Device, &imageInfo, null, out _image), "CreateImage");

        MemoryRequirements memReq;
        _context.Vk.GetImageMemoryRequirements(_context.Device, _image, &memReq);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = _context.FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
        };
        VulkanContext.Check(_context.Vk.AllocateMemory(_context.Device, &allocInfo, null, out _imageMemory), "AllocateMemory");
        VulkanContext.Check(_context.Vk.BindImageMemory(_context.Device, _image, _imageMemory, 0), "BindImageMemory");

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _image,
            ViewType = ImageViewType.Type2D,
            Format = _format,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1)
        };
        _context.Vk.CreateImageView(_context.Device, &viewInfo, null, out _imageView);

        var imageDescriptor = new DescriptorImageInfo
        {
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            ImageView = _imageView,
            Sampler = _sampler
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = DescriptorSet,
            DstBinding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &imageDescriptor
        };
        _context.Vk.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
    }

    private void CreateStaging()
    {
        ulong size = (ulong)(_width * _height * _bytesPerPixel);
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = BufferUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive
        };
        VulkanContext.Check(_context.Vk.CreateBuffer(_context.Device, &bufferInfo, null, out _staging), "CreateBuffer");
        MemoryRequirements memReq;
        _context.Vk.GetBufferMemoryRequirements(_context.Device, _staging, &memReq);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = _context.FindMemoryType(memReq.MemoryTypeBits,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit)
        };
        VulkanContext.Check(_context.Vk.AllocateMemory(_context.Device, &allocInfo, null, out _stagingMemory), "AllocateMemory");
        VulkanContext.Check(_context.Vk.BindBufferMemory(_context.Device, _staging, _stagingMemory, 0), "BindBufferMemory");
        void* mapped;
        VulkanContext.Check(_context.Vk.MapMemory(_context.Device, _stagingMemory, 0, size, 0, &mapped), "MapMemory");
        _stagingData = (byte*)mapped;
    }

    private void DestroyStaging()
    {
        if (_stagingData == null) return;
        _context.Vk.DestroyBuffer(_context.Device, _staging, null);
        _context.Vk.FreeMemory(_context.Device, _stagingMemory, null);
        _stagingData = null;
    }

    private void DestroyImage()
    {
        DestroyStaging();
        if (_image.Handle == 0) return;
        var vk = _context.Vk;
        vk.DestroyImageView(_context.Device, _imageView, null);
        vk.DestroyImage(_context.Device, _image, null);
        vk.FreeMemory(_context.Device, _imageMemory, null);
        _image = default;
    }

    public void Dispose()
    {
        DestroyImage();
        _context.Vk.DestroyDescriptorPool(_context.Device, _descriptorPool, null);
        _context.Vk.DestroySampler(_context.Device, _sampler, null);
    }
}
