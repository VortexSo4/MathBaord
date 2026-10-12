using Silk.NET.Vulkan;

namespace MathBoard.Backend.Vulkan;

/// <summary>R8 glyph atlas image. Changed rows are staged on the CPU and copied inside the frame's command buffer.</summary>
public sealed unsafe class AtlasTexture : IDisposable
{
    private readonly VulkanContext _context;

    private Image _image;
    private DeviceMemory _imageMemory;
    private ImageView _imageView;
    private Sampler _sampler;
    private Silk.NET.Vulkan.Buffer _staging;
    private DeviceMemory _stagingMemory;
    private byte* _stagingData;
    private DescriptorPool _descriptorPool;

    private int _size;
    private bool _initialized;
    private int _pendingTop = int.MaxValue, _pendingBottom;

    public DescriptorSetLayout DescriptorSetLayout { get; }
    public DescriptorSet DescriptorSet { get; }

    public AtlasTexture(VulkanContext context)
    {
        _context = context;

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MipmapMode = SamplerMipmapMode.Nearest
        };
        _context.Vk.CreateSampler(_context.Device, &samplerInfo, null, out _sampler);

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
        _context.Vk.CreateDescriptorSetLayout(_context.Device, &layoutInfo, null, out var layout);
        DescriptorSetLayout = layout;

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
    public void Stage(ReadOnlySpan<byte> pixels, int size, int dirtyTop, int dirtyBottom)
    {
        if (size != _size)
        {
            DestroyImage();
            CreateImage(size);
            dirtyTop = 0;
            dirtyBottom = size;
        }

        if (dirtyBottom <= dirtyTop) return;
        int offset = dirtyTop * size, length = (dirtyBottom - dirtyTop) * size;
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
        int rows = _initialized ? _pendingBottom - _pendingTop : _size;
        var region = new BufferImageCopy
        {
            BufferOffset = (ulong)(top * _size),
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D(0, top, 0),
            ImageExtent = new Extent3D((uint)_size, (uint)rows, 1)
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

    private void CreateImage(int size)
    {
        _size = size;
        _initialized = false;

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D((uint)size, (uint)size, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Format = Format.R8Unorm,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            Samples = SampleCountFlags.Count1Bit
        };
        _context.Vk.CreateImage(_context.Device, &imageInfo, null, out _image);

        MemoryRequirements memReq;
        _context.Vk.GetImageMemoryRequirements(_context.Device, _image, &memReq);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = _context.FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
        };
        _context.Vk.AllocateMemory(_context.Device, &allocInfo, null, out _imageMemory);
        _context.Vk.BindImageMemory(_context.Device, _image, _imageMemory, 0);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _image,
            ViewType = ImageViewType.Type2D,
            Format = Format.R8Unorm,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1)
        };
        _context.Vk.CreateImageView(_context.Device, &viewInfo, null, out _imageView);

        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = (ulong)(size * size),
            Usage = BufferUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive
        };
        _context.Vk.CreateBuffer(_context.Device, &bufferInfo, null, out _staging);
        _context.Vk.GetBufferMemoryRequirements(_context.Device, _staging, &memReq);
        allocInfo.AllocationSize = memReq.Size;
        allocInfo.MemoryTypeIndex = _context.FindMemoryType(memReq.MemoryTypeBits,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        _context.Vk.AllocateMemory(_context.Device, &allocInfo, null, out _stagingMemory);
        _context.Vk.BindBufferMemory(_context.Device, _staging, _stagingMemory, 0);
        void* mapped;
        _context.Vk.MapMemory(_context.Device, _stagingMemory, 0, (ulong)(size * size), 0, &mapped);
        _stagingData = (byte*)mapped;

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

    private void DestroyImage()
    {
        if (_image.Handle == 0) return;
        var vk = _context.Vk;
        vk.DestroyImageView(_context.Device, _imageView, null);
        vk.DestroyImage(_context.Device, _image, null);
        vk.FreeMemory(_context.Device, _imageMemory, null);
        vk.DestroyBuffer(_context.Device, _staging, null);
        vk.FreeMemory(_context.Device, _stagingMemory, null);
        _image = default;
    }

    public void Dispose()
    {
        DestroyImage();
        _context.Vk.DestroyDescriptorPool(_context.Device, _descriptorPool, null);
        _context.Vk.DestroyDescriptorSetLayout(_context.Device, DescriptorSetLayout, null);
        _context.Vk.DestroySampler(_context.Device, _sampler, null);
    }
}
