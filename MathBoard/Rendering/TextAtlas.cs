using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Core.Native;
using SkiaSharp;

namespace MathBoard.Rendering;

public sealed unsafe class TextAtlas : IDisposable
{
    private const int AtlasWidth = 1024;
    private const int AtlasHeight = 1024;
    private const int Padding = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct TextCommand
    {
        public Vector4 PosSize;
        public Vector4 Color;
        public Vector4 UV;
        public Vector4 Params; // X = offset
    }

    public readonly struct Entry
    {
        public readonly float U0, V0, U1, V1;
        public readonly float Width, Height;
        public Entry(float u0, float v0, float u1, float v1, float w, float h)
        {
            U0 = u0; V0 = v0; U1 = u1; V1 = v1; Width = w; Height = h;
        }
    }

    private readonly VulkanContext _context;
    private readonly RenderPassManager _renderPass;
    private readonly CommandManager _commandManager;

    private readonly Dictionary<string, Entry> _cache = new();
    private readonly List<(string text, int x, int y)> _pending = new();
    private readonly Dictionary<string, SKBitmap> _imageCache = new();
    private readonly List<(SKBitmap bmp, int x, int y)> _pendingImages = new();

    private SKTypeface? _typeface;
    private SKPaint _paint = null!;

    private int _penX, _penY, _rowHeight;
    private bool _building;
    private bool _atlasReady;

    private Image _image;
    private DeviceMemory _imageMemory;
    private ImageView _imageView;
    private Sampler _sampler;
    private DescriptorSetLayout _descriptorSetLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _descriptorSet;

    private Pipeline _pipeline;
    private PipelineLayout _pipelineLayout;

    private Silk.NET.Vulkan.Buffer _vertexBuffer;
    private DeviceMemory _vertexBufferMemory;
    private ulong _vertexBufferAllocatedSize;
    private uint _vertexCount;
    
    private readonly List<TextCommand> _textCommands = new();

    // Text Compute Pipeline
    private Pipeline _textComputePipeline;
    private PipelineLayout _textComputeLayout;
    private DescriptorSetLayout _textComputeDescSetLayout;
    private DescriptorPool _textComputeDescPool;
    private DescriptorSet _textComputeDescSet;

    private Silk.NET.Vulkan.Buffer _commandBuffer;
    private DeviceMemory _commandMemory;
    private ulong _commandAllocatedSize;

    public TextAtlas(VulkanContext context, RenderPassManager renderPass, CommandManager commandManager)
    {
        _context = context;
        _renderPass = renderPass;
        _commandManager = commandManager;
    }

    public void Initialize()
    {
        _typeface = SKFontManager.Default.MatchCharacter('А') 
                    ?? SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal)
                    ?? SKTypeface.FromFamilyName("Arial")
                    ?? SKTypeface.Default;

        _paint = new SKPaint
        {
            Typeface = _typeface,
            TextSize = 16f,
            Color = SKColors.White,
            IsAntialias = true,
            SubpixelText = true,
            TextEncoding = SKTextEncoding.Utf8
        };

        CreateEmptyImage();
        CreateSampler();
        CreateDescriptorSetLayout();
        CreateDescriptorPool();
        CreateDescriptorSet();
        CreatePipeline();
        CreateTextComputeResources();
    }

    public void BeginBuild()
    {
        _building = true;
        _cache.Clear();
        _pending.Clear();
        _pendingImages.Clear();
        _penX = Padding;
        _penY = Padding;
        _rowHeight = 0;
    }

    public Entry Request(string text)
    {
        if (!_building) throw new InvalidOperationException("Call between BeginBuild/EndBuild");
        if (string.IsNullOrEmpty(text)) return _cache[text] = new Entry(0, 0, 0, 0, 0, 0);
        if (_cache.TryGetValue(text, out var existing)) return existing;

        float textWidth = _paint.MeasureText(text);
        var metrics = _paint.FontMetrics;
        float textHeight = metrics.Descent - metrics.Ascent;

        PackAndAddEntry(text, (int)MathF.Ceiling(textWidth), (int)MathF.Ceiling(textHeight), out var entry);
        _cache[text] = entry;
        return entry;
    }

    public Entry RequestImage(string path)
    {
        if (!_building) throw new InvalidOperationException("Call between BeginBuild/EndBuild");
        if (_cache.TryGetValue(path, out var existing)) return existing;

        if (!_imageCache.TryGetValue(path, out var bmp))
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"TextAtlas: Image not found: {path}");
                return _cache[path] = new Entry(0, 0, 0, 0, 0, 0);
            }
            bmp = SKBitmap.Decode(path);
            _imageCache[path] = bmp;
        }

        PackAndAddEntry(path, bmp.Width, bmp.Height, out var entry);
        return entry;
    }

    private void PackAndAddEntry(string key, int width, int height, out Entry entry)
    {
        int w = width + Padding * 2;
        int h = height + Padding * 2;

        if (_penX + w > AtlasWidth)
        {
            _penX = Padding;
            _penY += _rowHeight + Padding;
            _rowHeight = 0;
        }

        if (_penY + h > AtlasHeight)
        {
            entry = new Entry(0, 0, 0, 0, width, height);
            return;
        }

        int x = _penX, y = _penY;
        
        if (key.EndsWith(".png"))
            _pendingImages.Add((_imageCache[key], x, y));
        else
            _pending.Add((key, x, y));

        _penX += w + Padding;
        _rowHeight = Math.Max(_rowHeight, h);

        float u0 = (x + Padding) / (float)AtlasWidth;
        float v0 = (y + Padding) / (float)AtlasHeight;
        float u1 = (x + Padding + width) / (float)AtlasWidth;
        float v1 = (y + Padding + height) / (float)AtlasHeight;

        entry = new Entry(u0, v0, u1, v1, width, height);
    }

    public void EndBuild()
    {
        _building = false;

        using var bitmap = new SKBitmap(AtlasWidth, AtlasHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            var metrics = _paint.FontMetrics;
            
            foreach (var (text, x, y) in _pending)
            {
                float baseline = y + Padding - metrics.Ascent;
                canvas.DrawText(text, x + Padding, baseline, _paint);
            }

            foreach (var (bmp, x, y) in _pendingImages)
            {
                canvas.DrawBitmap(bmp, x + Padding, y + Padding);
            }
        }

        UploadAtlas(bitmap);
        _atlasReady = true;
    }

    private void UploadAtlas(SKBitmap bitmap)
    {
        int pixelCount = AtlasWidth * AtlasHeight;
        var rgbaData = new byte[pixelCount * 4];
        var pixels = bitmap.GetPixelSpan();
        pixels.CopyTo(rgbaData);

        ulong size = (ulong)rgbaData.Length;

        CreateBuffer(size, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var staging, out var stagingMemory);

        void* mapped;
        _context.Vk.MapMemory(_context.Device, stagingMemory, 0, size, 0, &mapped);
        fixed (byte* src = rgbaData)
            System.Buffer.MemoryCopy(src, mapped, size, size);
        _context.Vk.UnmapMemory(_context.Device, stagingMemory);

        TransitionImageLayout(_image, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferDstOptimal);
        CopyBufferToImage(staging, _image, AtlasWidth, AtlasHeight);
        TransitionImageLayout(_image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);

        _context.Vk.DestroyBuffer(_context.Device, staging, null);
        _context.Vk.FreeMemory(_context.Device, stagingMemory, null);
    }

    public void BeginFrame() => _textCommands.Clear();

    public Vector2 Emit(string text, Vector2 pos, Vector4 color)
    {
        if (string.IsNullOrEmpty(text) || !_cache.TryGetValue(text, out var e) || e.Width <= 0) return Vector2.Zero;
        return EmitEntry(e, pos, new Vector2(e.Width, e.Height), color);
    }

    public Vector2 EmitImage(Entry e, Vector2 pos, Vector2 size, Vector4 color)
    {
        if (e.Width <= 0) return Vector2.Zero;
        return EmitEntry(e, pos, size, color);
    }

    private Vector2 EmitEntry(Entry e, Vector2 pos, Vector2 size, Vector4 color)
    {
        _textCommands.Add(new TextCommand
        {
            PosSize = new Vector4(pos.X, pos.Y, size.X, size.Y),
            Color = color,
            UV = new Vector4(e.U0, e.V0, e.U1, e.V1)
        });
        return size;
    }

    public Vector2 Measure(string text)
    {
        if (string.IsNullOrEmpty(text)) return Vector2.Zero;
        if (_cache.TryGetValue(text, out var e) && e.Width > 0) return new Vector2(e.Width, e.Height);

        float w = _paint.MeasureText(text);
        var m = _paint.FontMetrics;
        return new Vector2(w, m.Descent - m.Ascent);
    }

        public void UpdateTextCompute()
    {
        if (_textCommands.Count == 0) { _vertexCount = 0; return; }

        uint offset = 0;
        for (int i = 0; i < _textCommands.Count; i++)
        {
            var cmd = _textCommands[i];
            cmd.Params.X = offset;
            _textCommands[i] = cmd;
            offset += 6;
        }
        _vertexCount = offset;

        ulong cmdSize = (ulong)(_textCommands.Count * sizeof(TextCommand));
        if (_commandAllocatedSize < cmdSize)
        {
            if (_commandBuffer.Handle != 0)
            {
                _context.Vk.DeviceWaitIdle(_context.Device);
                _context.Vk.DestroyBuffer(_context.Device, _commandBuffer, null);
                _context.Vk.FreeMemory(_context.Device, _commandMemory, null);
            }
            CreateStorageBuffer(Math.Max(cmdSize * 2, 1UL << 16), out _commandBuffer, out _commandMemory);
            _commandAllocatedSize = Math.Max(cmdSize * 2, 1UL << 16);
            UpdateTextComputeDescriptorSet();
        }

        // Мапим только размер валидных команд
        void* mappedCmd;
        _context.Vk.MapMemory(_context.Device, _commandMemory, 0, cmdSize, 0, &mappedCmd);
        fixed (TextCommand* src = _textCommands.ToArray())
            System.Buffer.MemoryCopy(src, mappedCmd, cmdSize, cmdSize);
        _context.Vk.UnmapMemory(_context.Device, _commandMemory);

        ulong requiredVertexSize = _vertexCount * (ulong)sizeof(TextVertex);
        if (_vertexBuffer.Handle == 0 || _vertexBufferAllocatedSize < requiredVertexSize)
        {
            if (_vertexBuffer.Handle != 0)
            {
                _context.Vk.DeviceWaitIdle(_context.Device);
                _context.Vk.DestroyBuffer(_context.Device, _vertexBuffer, null);
                _context.Vk.FreeMemory(_context.Device, _vertexBufferMemory, null);
            }

            ulong allocSize = Math.Max(requiredVertexSize * 2, 1UL << 16);
            CreateBuffer(allocSize, BufferUsageFlags.VertexBufferBit | BufferUsageFlags.StorageBufferBit,
                MemoryPropertyFlags.DeviceLocalBit, out _vertexBuffer, out _vertexBufferMemory);
            _vertexBufferAllocatedSize = allocSize;
            UpdateTextComputeDescriptorSet();
        }
    }

    public void DispatchCompute(CommandBuffer cmd)
    {
        if (_vertexCount == 0 || _textCommands.Count == 0) return;

        _context.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _textComputePipeline);
        var descSet = _textComputeDescSet;
        _context.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _textComputeLayout, 0, 1, &descSet, 0, null);

        uint cmdCount = (uint)_textCommands.Count;
        _context.Vk.CmdPushConstants(cmd, _textComputeLayout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(uint), &cmdCount);

        uint groupSize = 64;
        uint groups = (cmdCount + groupSize - 1) / groupSize;
        _context.Vk.CmdDispatch(cmd, groups, 1, 1);

        var barriers = stackalloc BufferMemoryBarrier[1];
        barriers[0] = new BufferMemoryBarrier
        {
            SType = StructureType.BufferMemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.VertexAttributeReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = _vertexBuffer,
            Offset = 0,
            Size = Vk.WholeSize
        };

        _context.Vk.CmdPipelineBarrier(cmd, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.VertexInputBit, 0, 0, null, 1, barriers, 0, null);
    }

    public void Render(CommandBuffer cmd, Extent2D extent)
    {
        if (!_atlasReady || _vertexCount == 0 || _vertexBuffer.Handle == 0) return;

        _context.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);
        var set = _descriptorSet;
        _context.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &set, 0, null);

        var vb = _vertexBuffer;
        var offset = 0ul;
        _context.Vk.CmdBindVertexBuffers(cmd, 0, 1, &vb, &offset);

        var transform = stackalloc Matrix4x4[1];
        transform[0] = Matrix4x4.CreateOrthographicOffCenter(0, extent.Width, 0, extent.Height, -1f, 1f);
        _context.Vk.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), transform);

        _context.Vk.CmdDraw(cmd, _vertexCount, 1, 0, 0);
    }

        private void CreateTextComputeResources()
    {
        var bindings = stackalloc DescriptorSetLayoutBinding[2];
        bindings[0] = new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit };
        bindings[1] = new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit };

        var layoutInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 2, PBindings = bindings };
        _context.Vk.CreateDescriptorSetLayout(_context.Device, &layoutInfo, null, out _textComputeDescSetLayout);

        var poolSize = new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 2 };
        var poolInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, PoolSizeCount = 1, PPoolSizes = &poolSize, MaxSets = 1 };
        _context.Vk.CreateDescriptorPool(_context.Device, &poolInfo, null, out _textComputeDescPool);

        fixed (DescriptorSetLayout* pSetLayout = &_textComputeDescSetLayout)
        {
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _textComputeDescPool,
                DescriptorSetCount = 1,
                PSetLayouts = pSetLayout
            };
            _context.Vk.AllocateDescriptorSets(_context.Device, &allocInfo, out _textComputeDescSet);
        }

        var computeShader = LoadShader("Shaders/text.comp.spv");
        var stage = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.ComputeBit,
            Module = computeShader,
            PName = (byte*)SilkMarshal.StringToPtr("main")
        };

        var pushConstant = new PushConstantRange { StageFlags = ShaderStageFlags.ComputeBit, Size = (uint)sizeof(uint) };
        fixed (DescriptorSetLayout* pSetLayout = &_textComputeDescSetLayout)
        {
            var pipelineLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = pSetLayout,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstant
            };
            _context.Vk.CreatePipelineLayout(_context.Device, &pipelineLayoutInfo, null, out _textComputeLayout);
        }

        var pipelineInfo = new ComputePipelineCreateInfo
        {
            SType = StructureType.ComputePipelineCreateInfo,
            Stage = stage,
            Layout = _textComputeLayout
        };
        _context.Vk.CreateComputePipelines(_context.Device, default, 1, &pipelineInfo, null, out _textComputePipeline);

        SilkMarshal.Free((nint)stage.PName);
        _context.Vk.DestroyShaderModule(_context.Device, computeShader, null);
    }

    private void UpdateTextComputeDescriptorSet()
    {
        if (_commandBuffer.Handle == 0 || _vertexBuffer.Handle == 0) return;

        var bufferInfos = stackalloc DescriptorBufferInfo[2];
        bufferInfos[0] = new DescriptorBufferInfo { Buffer = _commandBuffer, Offset = 0, Range = Vk.WholeSize };
        bufferInfos[1] = new DescriptorBufferInfo { Buffer = _vertexBuffer, Offset = 0, Range = Vk.WholeSize };

        var writes = stackalloc WriteDescriptorSet[2];
        for (int i = 0; i < 2; i++)
        {
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _textComputeDescSet,
                DstBinding = (uint)i,
                DstArrayElement = 0,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                PBufferInfo = bufferInfos + i
            };
        }
        _context.Vk.UpdateDescriptorSets(_context.Device, 2, writes, 0, null);
    }

    private void CreateEmptyImage()
    {
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(AtlasWidth, AtlasHeight, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Format = Format.R8G8B8A8Unorm,
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
            MemoryTypeIndex = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
        };
        _context.Vk.AllocateMemory(_context.Device, &allocInfo, null, out _imageMemory);
        _context.Vk.BindImageMemory(_context.Device, _image, _imageMemory, 0);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _image,
            ViewType = ImageViewType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = 1,
                LayerCount = 1
            }
        };
        _context.Vk.CreateImageView(_context.Device, &viewInfo, null, out _imageView);

        TransitionImageLayout(_image, ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal);
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
            AnisotropyEnable = false,
            BorderColor = BorderColor.IntOpaqueBlack,
            UnnormalizedCoordinates = false,
            MipmapMode = SamplerMipmapMode.Nearest
        };
        _context.Vk.CreateSampler(_context.Device, &samplerInfo, null, out _sampler);
    }

    private void CreateDescriptorSetLayout()
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
        _context.Vk.CreateDescriptorSetLayout(_context.Device, &layoutInfo, null, out _descriptorSetLayout);
    }

    private void CreateDescriptorPool()
    {
        var poolSize = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
            MaxSets = 1
        };
        _context.Vk.CreateDescriptorPool(_context.Device, &poolInfo, null, out _descriptorPool);
    }

    private void CreateDescriptorSet()
    {
        var layout = _descriptorSetLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout
        };
        _context.Vk.AllocateDescriptorSets(_context.Device, &allocInfo, out _descriptorSet);

        var imageInfo = new DescriptorImageInfo
        {
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            ImageView = _imageView,
            Sampler = _sampler
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _descriptorSet,
            DstBinding = 0,
            DstArrayElement = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &imageInfo
        };
        _context.Vk.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
    }

    private void TransitionImageLayout(Image image, ImageLayout oldLayout, ImageLayout newLayout)
    {
        var cmd = BeginSingleTimeCommands();

        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = 1,
                LayerCount = 1
            }
        };

        PipelineStageFlags srcStage, dstStage;

        if (oldLayout == ImageLayout.Undefined && newLayout == ImageLayout.TransferDstOptimal)
        {
            barrier.SrcAccessMask = 0;
            barrier.DstAccessMask = AccessFlags.TransferWriteBit;
            srcStage = PipelineStageFlags.TopOfPipeBit;
            dstStage = PipelineStageFlags.TransferBit;
        }
        else if (oldLayout == ImageLayout.TransferDstOptimal && newLayout == ImageLayout.ShaderReadOnlyOptimal)
        {
            barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit;
            srcStage = PipelineStageFlags.TransferBit;
            dstStage = PipelineStageFlags.FragmentShaderBit;
        }
        else if (oldLayout == ImageLayout.ShaderReadOnlyOptimal && newLayout == ImageLayout.TransferDstOptimal)
        {
            barrier.SrcAccessMask = AccessFlags.ShaderReadBit;
            barrier.DstAccessMask = AccessFlags.TransferWriteBit;
            srcStage = PipelineStageFlags.FragmentShaderBit;
            dstStage = PipelineStageFlags.TransferBit;
        }
        else 
        {
            barrier.SrcAccessMask = 0;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit;
            srcStage = PipelineStageFlags.TopOfPipeBit;
            dstStage = PipelineStageFlags.FragmentShaderBit;
        }

        _context.Vk.CmdPipelineBarrier(cmd, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
        EndSingleTimeCommands(cmd);
    }

    private void CopyBufferToImage(Silk.NET.Vulkan.Buffer buffer, Image image, uint width, uint height)
    {
        var cmd = BeginSingleTimeCommands();

        var region = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(width, height, 1)
        };

        _context.Vk.CmdCopyBufferToImage(cmd, buffer, image, ImageLayout.TransferDstOptimal, 1, &region);
        EndSingleTimeCommands(cmd);
    }

    private CommandBuffer BeginSingleTimeCommands()
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
        return cmd;
    }

    private void EndSingleTimeCommands(CommandBuffer cmd)
    {
        _context.Vk.EndCommandBuffer(cmd);

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd
        };
        _context.Vk.QueueSubmit(_context.GraphicsQueue, 1, &submitInfo, default);
        _context.Vk.QueueWaitIdle(_context.GraphicsQueue); 

        var pool = _commandManager.CommandPool;
        _context.Vk.FreeCommandBuffers(_context.Device, pool, 1, &cmd);
    }

    private void CreatePipeline()
    {
        var vertShader = LoadShader("Shaders/text.vert.spv");
        var fragShader = LoadShader("Shaders/text.frag.spv");

        var vertStage = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = vertShader,
            PName = (byte*)SilkMarshal.StringToPtr("main")
        };
        var fragStage = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = fragShader,
            PName = (byte*)SilkMarshal.StringToPtr("main")
        };
        var shaderStages = stackalloc PipelineShaderStageCreateInfo[2] { vertStage, fragStage };

        var binding = new VertexInputBindingDescription
        {
            Binding = 0,
            Stride = (uint)sizeof(TextVertex),
            InputRate = VertexInputRate.Vertex
        };

        var attributes = stackalloc VertexInputAttributeDescription[3];
        attributes[0] = new VertexInputAttributeDescription
        {
            Location = 0, Binding = 0, Format = Format.R32G32Sfloat,
            Offset = (uint)Marshal.OffsetOf<TextVertex>("Position")
        };
        attributes[1] = new VertexInputAttributeDescription
        {
            Location = 1, Binding = 0, Format = Format.R32G32Sfloat,
            Offset = (uint)Marshal.OffsetOf<TextVertex>("UV")
        };
        attributes[2] = new VertexInputAttributeDescription
        {
            Location = 2, Binding = 0, Format = Format.R32G32B32A32Sfloat,
            Offset = (uint)Marshal.OffsetOf<TextVertex>("Color")
        };

        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = 1,
            PVertexBindingDescriptions = &binding,
            VertexAttributeDescriptionCount = 3,
            PVertexAttributeDescriptions = attributes
        };

        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.TriangleList
        };

        var dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamicStateInfo = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2,
            PDynamicStates = dynamicStates
        };

        var vp = stackalloc Viewport[1];
        vp[0] = new Viewport { Width = 1, Height = 1, MinDepth = 0, MaxDepth = 1 };
        var sc = stackalloc Rect2D[1];
        sc[0] = new Rect2D { Extent = new Extent2D { Width = 1, Height = 1 } };
        var viewportState = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1, PViewports = vp,
            ScissorCount = 1, PScissors = sc
        };

        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = PolygonMode.Fill,
            CullMode = CullModeFlags.None,
            LineWidth = 1.0f
        };

        var multisampling = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = _context.GetSampleCount()
        };

        var colorBlendAttachment = new PipelineColorBlendAttachmentState
        {
            BlendEnable = true,
            SrcColorBlendFactor = BlendFactor.SrcAlpha,
            DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
            ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = BlendFactor.One,
            DstAlphaBlendFactor = BlendFactor.Zero,
            AlphaBlendOp = BlendOp.Add,
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit |
                             ColorComponentFlags.ABit
        };
        var colorBlending = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = 1,
            PAttachments = &colorBlendAttachment
        };

        var pushConstant = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit,
            Size = (uint)sizeof(Matrix4x4)
        };

        var setLayout = _descriptorSetLayout;
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstant
        };
        _context.Vk.CreatePipelineLayout(_context.Device, &pipelineLayoutInfo, null, out _pipelineLayout);

        var pipelineInfo = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            StageCount = 2,
            PStages = shaderStages,
            PVertexInputState = &vertexInput,
            PInputAssemblyState = &inputAssembly,
            PViewportState = &viewportState,
            PRasterizationState = &rasterizer,
            PMultisampleState = &multisampling,
            PColorBlendState = &colorBlending,
            PDynamicState = &dynamicStateInfo,
            Layout = _pipelineLayout,
            RenderPass = _renderPass.RenderPass,
            Subpass = 0
        };
        _context.Vk.CreateGraphicsPipelines(_context.Device, default, 1, &pipelineInfo, null, out _pipeline);

        SilkMarshal.Free((nint)vertStage.PName);
        SilkMarshal.Free((nint)fragStage.PName);
        _context.Vk.DestroyShaderModule(_context.Device, vertShader, null);
        _context.Vk.DestroyShaderModule(_context.Device, fragShader, null);
    }

    private ShaderModule LoadShader(string path)
    {
        var code = File.ReadAllBytes(path);
        fixed (byte* pCode = code)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)pCode
            };
            _context.Vk.CreateShaderModule(_context.Device, &createInfo, null, out var module);
            return module;
        }
    }

    private void CreateBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties,
        out Silk.NET.Vulkan.Buffer buffer, out DeviceMemory memory)
    {
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive
        };
        _context.Vk.CreateBuffer(_context.Device, &bufferInfo, null, out buffer);

        MemoryRequirements memReq;
        _context.Vk.GetBufferMemoryRequirements(_context.Device, buffer, &memReq);

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = FindMemoryType(memReq.MemoryTypeBits, properties)
        };
        _context.Vk.AllocateMemory(_context.Device, &allocInfo, null, out memory);
        _context.Vk.BindBufferMemory(_context.Device, buffer, memory, 0);
    }

    private void CreateStorageBuffer(ulong size, out Silk.NET.Vulkan.Buffer buffer, out DeviceMemory memory)
    {
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive
        };
        _context.Vk.CreateBuffer(_context.Device, &bufferInfo, null, out buffer);

        MemoryRequirements memReq;
        _context.Vk.GetBufferMemoryRequirements(_context.Device, buffer, &memReq);

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit)
        };
        _context.Vk.AllocateMemory(_context.Device, &allocInfo, null, out memory);
        _context.Vk.BindBufferMemory(_context.Device, buffer, memory, 0);
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
        throw new Exception("Failed to find suitable memory type (TextAtlas)");
    }

    public void Dispose()
    {
        if (_vertexBuffer.Handle != 0)
        {
            _context.Vk.DeviceWaitIdle(_context.Device);
            _context.Vk.DestroyBuffer(_context.Device, _vertexBuffer, null);
            _context.Vk.FreeMemory(_context.Device, _vertexBufferMemory, null);
        }
        
        if (_commandBuffer.Handle != 0)
        {
            _context.Vk.DestroyBuffer(_context.Device, _commandBuffer, null);
            _context.Vk.FreeMemory(_context.Device, _commandMemory, null);
        }

        if (_pipeline.Handle != 0) _context.Vk.DestroyPipeline(_context.Device, _pipeline, null);
        if (_pipelineLayout.Handle != 0) _context.Vk.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);

        if (_descriptorPool.Handle != 0) _context.Vk.DestroyDescriptorPool(_context.Device, _descriptorPool, null);
        if (_descriptorSetLayout.Handle != 0) _context.Vk.DestroyDescriptorSetLayout(_context.Device, _descriptorSetLayout, null);

        if (_sampler.Handle != 0) _context.Vk.DestroySampler(_context.Device, _sampler, null);
        if (_imageView.Handle != 0) _context.Vk.DestroyImageView(_context.Device, _imageView, null);
        if (_image.Handle != 0) _context.Vk.DestroyImage(_context.Device, _image, null);
        if (_imageMemory.Handle != 0) _context.Vk.FreeMemory(_context.Device, _imageMemory, null);
        
        if (_textComputePipeline.Handle != 0) _context.Vk.DestroyPipeline(_context.Device, _textComputePipeline, null);
        if (_textComputeLayout.Handle != 0) _context.Vk.DestroyPipelineLayout(_context.Device, _textComputeLayout, null);
        if (_textComputeDescPool.Handle != 0) _context.Vk.DestroyDescriptorPool(_context.Device, _textComputeDescPool, null);
        if (_textComputeDescSetLayout.Handle != 0) _context.Vk.DestroyDescriptorSetLayout(_context.Device, _textComputeDescSetLayout, null);

        _paint?.Dispose();
        _typeface?.Dispose();
    }
}