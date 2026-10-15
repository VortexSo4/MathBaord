using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using MathBoard.Graphics;
using Silk.NET.Vulkan;

namespace MathBoard.Backend.Vulkan;

/// <summary>Host-visible, persistently mapped instance buffer of <see cref="Prim"/>.</summary>
public sealed unsafe class PrimBuffer(VulkanContext context) : IDisposable
{
    private Silk.NET.Vulkan.Buffer _buffer;
    private DeviceMemory _memory;
    private int _capacity;

    public Prim* Data { get; private set; }
    public int Count { get; set; }
    public Silk.NET.Vulkan.Buffer Handle => _buffer;

    /// <summary>Grows the buffer. Only call between frames: the old buffer is destroyed immediately.</summary>
    public void Reserve(int count, bool keepContents)
    {
        if (count <= _capacity) return;

        int newCapacity = (int)Math.Min(Math.Max(count + (long)count / 2, 4096), int.MaxValue / sizeof(Prim));
        if (newCapacity < count) throw new InvalidOperationException($"{count} primitives do not fit in one buffer");
        ulong size = (ulong)newCapacity * (ulong)sizeof(Prim);

        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = BufferUsageFlags.VertexBufferBit,
            SharingMode = SharingMode.Exclusive
        };
        VulkanContext.Check(context.Vk.CreateBuffer(context.Device, &bufferInfo, null, out var buffer), "CreateBuffer");

        MemoryRequirements memReq;
        context.Vk.GetBufferMemoryRequirements(context.Device, buffer, &memReq);
        const MemoryPropertyFlags hostFlags = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
        // Video memory the CPU can write is fastest to draw from, but its heap is often only 256 MB, so a big board
        // falls back to system memory instead of failing (on integrated graphics all of it is both).
        DeviceMemory memory = default;
        const MemoryPropertyFlags local = MemoryPropertyFlags.DeviceLocalBit;
        foreach (var (flags, excluded) in new (MemoryPropertyFlags, MemoryPropertyFlags)[] { (hostFlags | local, 0), (hostFlags, local), (hostFlags, 0) })
        {
            if (context.TryFindMemoryType(memReq.MemoryTypeBits, flags, excluded) is not { } type) continue;
            var allocInfo = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = memReq.Size, MemoryTypeIndex = type };
            if (context.Vk.AllocateMemory(context.Device, &allocInfo, null, out memory) == Result.Success) break;
            memory = default;
        }

        if (memory.Handle == 0)
        {
            context.Vk.DestroyBuffer(context.Device, buffer, null);
            throw new OutOfMemoryException($"No memory for {newCapacity} primitives ({size >> 20} MB)");
        }

        VulkanContext.Check(context.Vk.BindBufferMemory(context.Device, buffer, memory, 0), "BindBufferMemory");
        void* mapped;
        VulkanContext.Check(context.Vk.MapMemory(context.Device, memory, 0, size, 0, &mapped), "MapMemory");

        if (keepContents && Count > 0)
            System.Buffer.MemoryCopy(Data, mapped, (long)size, (long)Count * sizeof(Prim));

        Destroy();
        _buffer = buffer;
        _memory = memory;
        _capacity = newCapacity;
        Data = (Prim*)mapped;
    }

    /// <summary>Makes the buffer hold <paramref name="prims"/>, copying only the part from <paramref name="from"/> on.</summary>
    public void Update(ReadOnlySpan<Prim> prims, int from)
    {
        Reserve(prims.Length, keepContents: from > 0);
        from = Math.Clamp(from, 0, prims.Length);
        prims[from..].CopyTo(new Span<Prim>(Data + from, prims.Length - from));
        Count = prims.Length;
    }

    private void Destroy()
    {
        if (_buffer.Handle == 0) return;
        context.Vk.UnmapMemory(context.Device, _memory);
        context.Vk.DestroyBuffer(context.Device, _buffer, null);
        context.Vk.FreeMemory(context.Device, _memory, null);
        _buffer = default;
    }

    public void Dispose() => Destroy();
}

/// <summary>Draws <see cref="Prim"/> instances: shapes are evaluated as signed distance fields, so no MSAA is needed.</summary>
public sealed unsafe class PrimitiveRenderer : IDisposable
{
    // Laid out like the push constant block in prim.vert: the vec4 is aligned to 16 bytes, the vec2 to 8.
    [StructLayout(LayoutKind.Explicit, Size = 104)]
    private struct PushConstants
    {
        [FieldOffset(0)] public Matrix4x4 Transform;
        [FieldOffset(64)] public float Pixel;
        [FieldOffset(80)] public Vector4 Move;
        [FieldOffset(96)] public Vector2 MoveOffset;
    }

    private readonly VulkanContext _context;
    private readonly DescriptorSetLayout _atlasLayout;
    private Pipeline _pipeline;
    private PipelineLayout _layout;

    public PrimitiveRenderer(VulkanContext context, RenderPass renderPass, DescriptorSetLayout atlasLayout)
    {
        _context = context;
        _atlasLayout = atlasLayout;
        CreatePipeline(renderPass);
    }

    public void Bind(CommandBuffer cmd, Extent2D extent, DescriptorSet atlas)
    {
        var viewport = new Viewport(0, 0, extent.Width, extent.Height, 0, 1);
        var scissor = new Rect2D(new Offset2D(0, 0), extent);
        _context.Vk.CmdSetViewport(cmd, 0, 1, &viewport);
        _context.Vk.CmdSetScissor(cmd, 0, 1, &scissor);
        _context.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);
        BindTexture(cmd, atlas);
    }

    /// <summary>Switches the texture sampled by glyph and image primitives.</summary>
    public void BindTexture(CommandBuffer cmd, DescriptorSet texture) =>
        _context.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 1, &texture, 0, null);

    /// <param name="move">Applied to capsule ends before <paramref name="transform"/>: a selection being dragged.</param>
    public void Draw(CommandBuffer cmd, PrimBuffer buffer, Matrix4x4 transform, float pixel, int first = 0, int count = -1,
        Matrix3x2? move = null)
    {
        if (count < 0) count = buffer.Count - first;
        if (count <= 0) return;

        var vb = buffer.Handle;
        ulong offset = 0;
        _context.Vk.CmdBindVertexBuffers(cmd, 0, 1, &vb, &offset);

        var m = move ?? Matrix3x2.Identity;
        var pc = new PushConstants
        {
            Transform = transform, Pixel = pixel, Move = new Vector4(m.M11, m.M12, m.M21, m.M22), MoveOffset = new Vector2(m.M31, m.M32)
        };
        _context.Vk.CmdPushConstants(cmd, _layout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(PushConstants), &pc);
        _context.Vk.CmdDraw(cmd, 6, (uint)count, 0, (uint)first);
    }

    private void CreatePipeline(RenderPass renderPass)
    {
        var vertShader = LoadShader("prim.vert.spv");
        var fragShader = LoadShader("prim.frag.spv");
        var entry = (byte*)SilkMarshal.StringToPtr("main");

        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit,
            Module = vertShader, PName = entry
        };
        stages[1] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit,
            Module = fragShader, PName = entry
        };

        var binding = new VertexInputBindingDescription
        {
            Binding = 0, Stride = (uint)sizeof(Prim), InputRate = VertexInputRate.Instance
        };
        var attributes = stackalloc VertexInputAttributeDescription[5];
        for (int i = 0; i < 5; i++)
        {
            attributes[i] = new VertexInputAttributeDescription
            {
                Location = (uint)i, Binding = 0, Format = Format.R32G32B32A32Sfloat, Offset = (uint)(i * sizeof(Vector4))
            };
        }

        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = 1, PVertexBindingDescriptions = &binding,
            VertexAttributeDescriptionCount = 5, PVertexAttributeDescriptions = attributes
        };
        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList
        };
        var dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamicState = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamicStates
        };
        var viewportState = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1
        };
        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill,
            CullMode = CullModeFlags.None, LineWidth = 1f
        };
        var multisampling = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit
        };
        var blend = new PipelineColorBlendAttachmentState
        {
            BlendEnable = true,
            SrcColorBlendFactor = BlendFactor.SrcAlpha, DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
            ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = BlendFactor.One, DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
            AlphaBlendOp = BlendOp.Add,
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit |
                             ColorComponentFlags.ABit
        };
        var colorBlending = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &blend
        };

        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit, Size = (uint)sizeof(PushConstants)
        };
        var setLayout = _atlasLayout;
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1, PSetLayouts = &setLayout,
            PushConstantRangeCount = 1, PPushConstantRanges = &pushRange
        };
        _context.Vk.CreatePipelineLayout(_context.Device, &layoutInfo, null, out _layout);

        var pipelineInfo = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            StageCount = 2, PStages = stages,
            PVertexInputState = &vertexInput, PInputAssemblyState = &inputAssembly,
            PViewportState = &viewportState, PRasterizationState = &rasterizer,
            PMultisampleState = &multisampling, PColorBlendState = &colorBlending,
            PDynamicState = &dynamicState, Layout = _layout, RenderPass = renderPass, Subpass = 0
        };
        var result = _context.Vk.CreateGraphicsPipelines(_context.Device, default, 1, &pipelineInfo, null, out _pipeline);
        if (result != Result.Success)
            throw new Exception($"CreateGraphicsPipelines failed: {result}");

        SilkMarshal.Free((nint)entry);
        _context.Vk.DestroyShaderModule(_context.Device, vertShader, null);
        _context.Vk.DestroyShaderModule(_context.Device, fragShader, null);
    }

    private ShaderModule LoadShader(string name)
    {
        using var stream = typeof(PrimitiveRenderer).Assembly.GetManifestResourceStream(name)
                           ?? throw new FileNotFoundException($"Embedded shader {name} not found");
        var code = new byte[stream.Length];
        stream.ReadExactly(code);
        fixed (byte* pCode = code)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)code.Length, PCode = (uint*)pCode
            };
            _context.Vk.CreateShaderModule(_context.Device, &createInfo, null, out var module);
            return module;
        }
    }

    public void Dispose()
    {
        _context.Vk.DestroyPipeline(_context.Device, _pipeline, null);
        _context.Vk.DestroyPipelineLayout(_context.Device, _layout, null);
    }
}
