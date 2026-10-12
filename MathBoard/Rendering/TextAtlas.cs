using System.Numerics;
using Silk.NET.Vulkan;
using SkiaSharp;

namespace MathBoard.Rendering;

/// <summary>
/// Lazily rasterizes whole strings into a single-channel coverage atlas.
/// When the atlas fills up it is cleared on the next frame and refilled with whatever is drawn then.
/// </summary>
public sealed unsafe class TextAtlas : IDisposable
{
    private const int AtlasSize = 1024;
    private const int Spacing = 1;

    public readonly struct Entry(Vector2 offset, Vector2 size, Vector4 uv)
    {
        /// <summary>Bitmap top-left relative to the pen position on the baseline.</summary>
        public readonly Vector2 Offset = offset;
        public readonly Vector2 Size = size;
        public readonly Vector4 UV = uv;
    }

    public readonly record struct Metrics(float Ascent, float Descent, float CapHeight)
    {
        public float LineHeight => Ascent + Descent;
    }

    private readonly VulkanContext _context;
    private readonly CommandManager _commandManager;

    private readonly Dictionary<(string, TextStyle), Entry> _entries = new();
    private readonly Dictionary<(string, TextStyle), float> _widths = new();
    private readonly Dictionary<TextStyle, SKFont> _fonts = new();
    private readonly SKTypeface[] _typefaces = new SKTypeface[3];

    private readonly SKBitmap _bitmap = new(AtlasSize, AtlasSize, SKColorType.Alpha8, SKAlphaType.Premul);
    private readonly SKCanvas _canvas;
    private readonly SKPaint _paint = new() { Color = SKColors.White, IsAntialias = true };

    private int _penX, _penY, _rowHeight;
    private int _dirtyTop = int.MaxValue, _dirtyBottom;
    private bool _resetPending;

    private Image _image;
    private DeviceMemory _imageMemory;
    private ImageView _imageView;
    private Sampler _sampler;
    private Silk.NET.Vulkan.Buffer _staging;
    private DeviceMemory _stagingMemory;
    private byte* _stagingData;

    public DescriptorSetLayout DescriptorSetLayout { get; private set; }
    public DescriptorSet DescriptorSet { get; private set; }
    private DescriptorPool _descriptorPool;

    /// <summary>Set when a string did not fit this frame; the caller should draw the UI again next frame.</summary>
    public bool NeedsRedraw { get; private set; }

    public TextAtlas(VulkanContext context, CommandManager commandManager)
    {
        _context = context;
        _commandManager = commandManager;
        _canvas = new SKCanvas(_bitmap);
    }

    public void Initialize()
    {
        var semibold = new SKFontStyle(SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        _typefaces[(int)FontFace.Regular] = LoadTypeface(SKFontStyle.Normal, "Segoe UI", "Arial");
        _typefaces[(int)FontFace.Semibold] = LoadTypeface(semibold, "Segoe UI", "Arial");
        _typefaces[(int)FontFace.Icons] = LoadTypeface(SKFontStyle.Normal, "Segoe Fluent Icons", "Segoe MDL2 Assets");

        _canvas.Clear(SKColors.Transparent);
        CreateImage();
        CreateStaging();
        CreateSampler();
        CreateDescriptors();
        SubmitUpload(0, AtlasSize, initial: true);
    }

    private static SKTypeface LoadTypeface(SKFontStyle style, params string[] families)
    {
        foreach (var family in families)
        {
            var typeface = SKFontManager.Default.MatchFamily(family, style);
            if (typeface != null && typeface.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase))
                return typeface;
            typeface?.Dispose();
        }

        Console.WriteLine($"TextAtlas: none of [{string.Join(", ", families)}] found, using default font");
        return SKTypeface.Default;
    }

    private SKFont Font(TextStyle style)
    {
        if (!_fonts.TryGetValue(style, out var font))
        {
            font = new SKFont(_typefaces[(int)style.Face], style.Size)
            {
                Edging = SKFontEdging.Antialias,
                Subpixel = true,
                Hinting = SKFontHinting.Slight
            };
            _fonts[style] = font;
        }

        return font;
    }

    public Metrics GetMetrics(TextStyle style)
    {
        var m = Font(style).Metrics;
        return new Metrics(-m.Ascent, m.Descent, m.CapHeight > 0 ? m.CapHeight : -m.Ascent * 0.7f);
    }

    public float MeasureWidth(string text, TextStyle style)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        if (_widths.TryGetValue((text, style), out float w)) return w;
        if (_widths.Count > 4096) _widths.Clear();
        w = Font(style).MeasureText(text);
        _widths[(text, style)] = w;
        return w;
    }

    /// <summary>Call once before building a frame's UI.</summary>
    public void BeginFrame()
    {
        NeedsRedraw = false;
        if (!_resetPending) return;

        _resetPending = false;
        _entries.Clear();
        _canvas.Clear(SKColors.Transparent);
        _penX = _penY = _rowHeight = 0;
    }

    public bool TryGet(string text, TextStyle style, out Entry entry)
    {
        if (_entries.TryGetValue((text, style), out entry)) return entry.Size.X > 0;

        var font = Font(style);
        font.MeasureText(text, out var bounds, _paint);
        if (bounds.IsEmpty)
        {
            _entries[(text, style)] = entry = default;
            return false;
        }

        int left = (int)MathF.Floor(bounds.Left) - 1;
        int top = (int)MathF.Floor(bounds.Top) - 1;
        int width = (int)MathF.Ceiling(bounds.Right) + 1 - left;
        int height = (int)MathF.Ceiling(bounds.Bottom) + 1 - top;

        // Wider than the whole atlas: clearing would not help, so it is simply not drawn.
        if (width > AtlasSize || height > AtlasSize)
        {
            _entries[(text, style)] = entry = default;
            return false;
        }

        if (_penX + width > AtlasSize)
        {
            _penX = 0;
            _penY += _rowHeight + Spacing;
            _rowHeight = 0;
        }

        if (_penY + height > AtlasSize)
        {
            _resetPending = true;
            NeedsRedraw = true;
            entry = default;
            return false;
        }

        int x = _penX, y = _penY;
        _canvas.DrawText(text, x - left, y - top, font, _paint);
        _penX += width + Spacing;
        _rowHeight = Math.Max(_rowHeight, height);
        _dirtyTop = Math.Min(_dirtyTop, y);
        _dirtyBottom = Math.Max(_dirtyBottom, y + height);

        entry = new Entry(new Vector2(left, top), new Vector2(width, height),
            new Vector4(x, y, x + width, y + height) / AtlasSize);
        _entries[(text, style)] = entry;
        return true;
    }

    /// <summary>Uploads the rows rasterized since the last call. Must run outside of command buffer recording.</summary>
    public void Flush()
    {
        if (_dirtyBottom <= _dirtyTop) return;
        int top = _dirtyTop, rows = _dirtyBottom - _dirtyTop;
        _dirtyTop = int.MaxValue;
        _dirtyBottom = 0;

        System.Buffer.MemoryCopy((byte*)_bitmap.GetPixels() + top * AtlasSize, _stagingData + top * AtlasSize,
            rows * AtlasSize, rows * AtlasSize);
        SubmitUpload(top, rows, initial: false);
    }

    private void SubmitUpload(int top, int rows, bool initial)
    {
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = _commandManager.CommandPool,
            CommandBufferCount = 1
        };
        _context.Vk.AllocateCommandBuffers(_context.Device, &allocInfo, out var cmd);
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };
        _context.Vk.BeginCommandBuffer(cmd, &beginInfo);

        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = initial ? ImageLayout.Undefined : ImageLayout.ShaderReadOnlyOptimal,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcAccessMask = initial ? 0 : AccessFlags.ShaderReadBit,
            DstAccessMask = AccessFlags.TransferWriteBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1)
        };
        _context.Vk.CmdPipelineBarrier(cmd, PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &barrier);

        var region = new BufferImageCopy
        {
            BufferOffset = (ulong)(top * AtlasSize),
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D(0, top, 0),
            ImageExtent = new Extent3D(AtlasSize, (uint)rows, 1)
        };
        _context.Vk.CmdCopyBufferToImage(cmd, _staging, _image, ImageLayout.TransferDstOptimal, 1, &region);

        barrier.OldLayout = ImageLayout.TransferDstOptimal;
        barrier.NewLayout = ImageLayout.ShaderReadOnlyOptimal;
        barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
        barrier.DstAccessMask = AccessFlags.ShaderReadBit;
        _context.Vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
            0, 0, null, 0, null, 1, &barrier);

        _context.Vk.EndCommandBuffer(cmd);
        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd
        };
        _context.Vk.QueueSubmit(_context.GraphicsQueue, 1, &submitInfo, default);
        _context.Vk.QueueWaitIdle(_context.GraphicsQueue);
        _context.Vk.FreeCommandBuffers(_context.Device, _commandManager.CommandPool, 1, &cmd);
    }

    private void CreateImage()
    {
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(AtlasSize, AtlasSize, 1),
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
    }

    private void CreateStaging()
    {
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = AtlasSize * AtlasSize,
            Usage = BufferUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive
        };
        _context.Vk.CreateBuffer(_context.Device, &bufferInfo, null, out _staging);

        MemoryRequirements memReq;
        _context.Vk.GetBufferMemoryRequirements(_context.Device, _staging, &memReq);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = _context.FindMemoryType(memReq.MemoryTypeBits,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit)
        };
        _context.Vk.AllocateMemory(_context.Device, &allocInfo, null, out _stagingMemory);
        _context.Vk.BindBufferMemory(_context.Device, _staging, _stagingMemory, 0);

        void* mapped;
        _context.Vk.MapMemory(_context.Device, _stagingMemory, 0, AtlasSize * AtlasSize, 0, &mapped);
        _stagingData = (byte*)mapped;
        // The initial upload copies this zeroed buffer so unused atlas areas never contain garbage.
        new Span<byte>(_stagingData, AtlasSize * AtlasSize).Clear();
    }

    private void CreateSampler()
    {
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
    }

    private void CreateDescriptors()
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

        var imageInfo = new DescriptorImageInfo
        {
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            ImageView = _imageView,
            Sampler = _sampler
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &imageInfo
        };
        _context.Vk.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
    }

    public void Dispose()
    {
        var vk = _context.Vk;
        var device = _context.Device;
        vk.DestroyDescriptorPool(device, _descriptorPool, null);
        vk.DestroyDescriptorSetLayout(device, DescriptorSetLayout, null);
        vk.DestroySampler(device, _sampler, null);
        vk.DestroyImageView(device, _imageView, null);
        vk.DestroyImage(device, _image, null);
        vk.FreeMemory(device, _imageMemory, null);
        vk.DestroyBuffer(device, _staging, null);
        vk.FreeMemory(device, _stagingMemory, null);

        foreach (var font in _fonts.Values) font.Dispose();
        _paint.Dispose();
        _canvas.Dispose();
        _bitmap.Dispose();
    }
}
