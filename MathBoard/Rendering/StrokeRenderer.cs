using Silk.NET.Vulkan;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;

namespace MathBoard.Rendering;

[StructLayout(LayoutKind.Sequential)]
public struct Vertex
{
    public Vector2 Position;
    public float Thickness;
    public Vector4 Color;
}

public sealed unsafe class StrokeRenderer : IDisposable
{
    private readonly VulkanContext _context;
    private readonly SwapchainManager _swapchain;
    private readonly RenderPassManager _renderPass;
    private readonly CommandManager _commandManager;

    private Pipeline _pipeline;
    private PipelineLayout _pipelineLayout;

    private Silk.NET.Vulkan.Buffer _vertexBuffer;
    private DeviceMemory _vertexBufferMemory;
    private uint _vertexCount;

    private readonly List<Vertex> _vertices = [];
    private readonly List<Vector2> _currentPoints = [];

    private bool _isDrawing = false;
    private bool _dirty = false;
    private Extent2D _extent;

    public StrokeRenderer(
        VulkanContext context,
        SwapchainManager swapchain,
        RenderPassManager renderPass,
        CommandManager commandManager)
    {
        _context = context;
        _swapchain = swapchain;
        _renderPass = renderPass;
        _commandManager = commandManager;
        _extent = _swapchain.Extent;
    }

    public void Initialize()
    {
        CreatePipeline();
        Console.WriteLine("StrokeRenderer: Pipeline created successfully");
    }

    public void UpdateExtent(Extent2D extent)
    {
        _extent = extent;
    }

    private void CreatePipeline()
    {
        var vertShader = LoadShader("Shaders/stroke.vert.spv");
        var fragShader = LoadShader("Shaders/stroke.frag.spv");

        // === Shader Stages ===
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

        // === Vertex Input ===
        var binding = new VertexInputBindingDescription
        {
            Binding = 0,
            Stride = (uint)sizeof(Vertex),
            InputRate = VertexInputRate.Vertex
        };

        var attributes = stackalloc VertexInputAttributeDescription[3];
        attributes[0] = new VertexInputAttributeDescription
        {
            Location = 0,
            Binding = 0,
            Format = Format.R32G32Sfloat,
            Offset = (uint)Marshal.OffsetOf<Vertex>("Position")
        };
        attributes[1] = new VertexInputAttributeDescription
        {
            Location = 1,
            Binding = 0,
            Format = Format.R32Sfloat,
            Offset = (uint)Marshal.OffsetOf<Vertex>("Thickness")
        };
        attributes[2] = new VertexInputAttributeDescription
        {
            Location = 2,
            Binding = 0,
            Format = Format.R32G32B32A32Sfloat,
            Offset = (uint)Marshal.OffsetOf<Vertex>("Color")
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

        // === Dynamic Viewport & Scissor ===
        var dynamicStates = stackalloc DynamicState[2]
        {
            DynamicState.Viewport,
            DynamicState.Scissor
        };
        var dynamicStateInfo = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2,
            PDynamicStates = dynamicStates
        };

        // Всё равно нужно передать валидные указатели, но они будут проигнорированы
        var viewport = new Viewport { X = 0, Y = 0, Width = 1, Height = 1, MinDepth = 0, MaxDepth = 1 };
        var scissor = new Rect2D { Offset = new Offset2D { X = 0, Y = 0 }, Extent = new Extent2D { Width = 1, Height = 1 } };

        var viewportState = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            PViewports = &viewport,
            ScissorCount = 1,
            PScissors = &scissor
        };

        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            DepthClampEnable = false,
            RasterizerDiscardEnable = false,
            PolygonMode = PolygonMode.Fill,
            CullMode = CullModeFlags.None,
            FrontFace = FrontFace.CounterClockwise,
            DepthBiasEnable = false,
            LineWidth = 1.0f
        };

        var multisampling = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = SampleCountFlags.Count1Bit,
            SampleShadingEnable = false
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
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                             ColorComponentFlags.BBit | ColorComponentFlags.ABit
        };

        var colorBlending = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            LogicOpEnable = false,
            AttachmentCount = 1,
            PAttachments = &colorBlendAttachment
        };

        // === Pipeline Layout with Push Constants ===
        var pushConstant = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit,
            Offset = 0,
            Size = (uint)sizeof(Matrix4x4)
        };

        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstant
        };

        _context.Vk.CreatePipelineLayout(_context.Device, &pipelineLayoutInfo, null, out _pipelineLayout);

        // === Graphics Pipeline ===
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
            Subpass = 0,
            BasePipelineHandle = default
        };

        var result = _context.Vk.CreateGraphicsPipelines(
            _context.Device, default, 1, &pipelineInfo, null, out _pipeline);

        if (result != Result.Success)
            throw new Exception($"CreateGraphicsPipelines failed: {result}");

        // Cleanup
        SilkMarshal.Free((nint)vertStage.PName);
        SilkMarshal.Free((nint)fragStage.PName);
        _context.Vk.DestroyShaderModule(_context.Device, vertShader, null);
        _context.Vk.DestroyShaderModule(_context.Device, fragShader, null);

        Console.WriteLine("Graphics pipeline created successfully");
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

    // === Stroke Input ===
    public void BeginStroke(Vector2 position, float pressure = 1.0f)
    {
        _currentPoints.Clear();
        _vertices.Clear();
        _currentPoints.Add(position);
        _isDrawing = true;
        _dirty = true;
    }

    public void AddPoint(Vector2 position, float pressure = 1.0f)
    {
        if (!_isDrawing) return;
        _currentPoints.Add(position);
        UpdateCurrentStroke();
    }

    public void EndStroke()
    {
        _isDrawing = false;
    }

    private void UpdateCurrentStroke()
    {
        _vertices.Clear();
        if (_currentPoints.Count < 2) return;

        const float thickness = 22.0f;
        var color = new Vector4(0.0f, 0.0f, 0.0f, 1.0f);

        for (int i = 0; i < _currentPoints.Count - 1; i++)
        {
            var p1 = _currentPoints[i];
            var p2 = _currentPoints[i + 1];

            var dir = Vector2.Normalize(p2 - p1);
            var perp = new Vector2(-dir.Y, dir.X);

            var p1l = p1 + perp * thickness * 0.5f;
            var p1r = p1 - perp * thickness * 0.5f;
            var p2l = p2 + perp * thickness * 0.5f;
            var p2r = p2 - perp * thickness * 0.5f;

            _vertices.Add(new Vertex { Position = p1l, Thickness = thickness, Color = color });
            _vertices.Add(new Vertex { Position = p1r, Thickness = thickness, Color = color });
            _vertices.Add(new Vertex { Position = p2l, Thickness = thickness, Color = color });

            _vertices.Add(new Vertex { Position = p2l, Thickness = thickness, Color = color });
            _vertices.Add(new Vertex { Position = p1r, Thickness = thickness, Color = color });
            _vertices.Add(new Vertex { Position = p2r, Thickness = thickness, Color = color });
        }

        _dirty = true;
    }

    public void Flush()
    {
        if (!_dirty) return;

        if (_vertices.Count == 0)
        {
            if (_vertexBuffer.Handle != 0)
            {
                _context.Vk.DeviceWaitIdle(_context.Device);
                _context.Vk.DestroyBuffer(_context.Device, _vertexBuffer, null);
                _context.Vk.FreeMemory(_context.Device, _vertexBufferMemory, null);
                _vertexBuffer = default;
                _vertexCount = 0;
            }
        }
        else
        {
            UpdateVertexBuffer();
        }

        _dirty = false;
    }

    private void UpdateVertexBuffer()
    {
        if (_vertexBuffer.Handle != 0)
        {
            _context.Vk.DeviceWaitIdle(_context.Device);
            _context.Vk.DestroyBuffer(_context.Device, _vertexBuffer, null);
            _context.Vk.FreeMemory(_context.Device, _vertexBufferMemory, null);
        }

        if (_vertices.Count == 0) return;

        var bufferSize = (ulong)(sizeof(Vertex) * _vertices.Count);

        // Staging buffer
        CreateBuffer(bufferSize, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var stagingBuffer, out var stagingMemory);

        void* mapped;
        _context.Vk.MapMemory(_context.Device, stagingMemory, 0, bufferSize, 0, &mapped);
        fixed (Vertex* pSrc = _vertices.ToArray())
        {
            System.Buffer.MemoryCopy(pSrc, mapped, bufferSize, bufferSize);
        }
        _context.Vk.UnmapMemory(_context.Device, stagingMemory);

        // GPU buffer
        CreateBuffer(bufferSize,
            BufferUsageFlags.TransferDstBit | BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.DeviceLocalBit,
            out _vertexBuffer, out _vertexBufferMemory);

        CopyBuffer(stagingBuffer, _vertexBuffer, bufferSize);

        _context.Vk.DestroyBuffer(_context.Device, stagingBuffer, null);
        _context.Vk.FreeMemory(_context.Device, stagingMemory, null);

        _vertexCount = (uint)_vertices.Count;
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

    private uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        PhysicalDeviceMemoryProperties memProps;
        _context.Vk.GetPhysicalDeviceMemoryProperties(_context.PhysicalDevice, &memProps);

        for (int i = 0; i < memProps.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1u << i)) != 0 && (memProps.MemoryTypes[i].PropertyFlags & properties) == properties)
            {
                return (uint)i;
            }
        }
        throw new Exception("Failed to find suitable memory type");
    }

    private void CopyBuffer(Silk.NET.Vulkan.Buffer src, Silk.NET.Vulkan.Buffer dst, ulong size)
    {
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandManager.CommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };

        CommandBuffer cmd;
        _context.Vk.AllocateCommandBuffers(_context.Device, &allocInfo, &cmd);

        var beginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };
        _context.Vk.BeginCommandBuffer(cmd, &beginInfo);

        var copyRegion = new BufferCopy { Size = size };
        _context.Vk.CmdCopyBuffer(cmd, src, dst, 1, &copyRegion);

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

    public void Render(CommandBuffer cmd)
    {
        if (_vertexCount < 6) return;

        // Set dynamic viewport and scissor
        var viewport = new Viewport
        {
            X = 0,
            Y = 0,
            Width = _extent.Width,
            Height = _extent.Height,
            MinDepth = 0.0f,
            MaxDepth = 1.0f
        };
        var scissor = new Rect2D
        {
            Offset = new Offset2D { X = 0, Y = 0 },
            Extent = _extent
        };
        _context.Vk.CmdSetViewport(cmd, 0, 1, &viewport);
        _context.Vk.CmdSetScissor(cmd, 0, 1, &scissor);

        // Ortho projection (screen space)
        var projection = Matrix4x4.CreateOrthographicOffCenter(
            0, _extent.Width,
            _extent.Height, 0,
            -1f, 1f);

        _context.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);

        var vb = _vertexBuffer;
        var offset = 0ul;
        _context.Vk.CmdBindVertexBuffers(cmd, 0, 1, &vb, &offset);

        Matrix4x4* pProj = &projection;
        _context.Vk.CmdPushConstants(cmd, _pipelineLayout,
            ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), pProj);

        _context.Vk.CmdDraw(cmd, _vertexCount, 1, 0, 0);
    }

    public void Dispose()
    {
        if (_vertexBuffer.Handle != 0)
        {
            _context.Vk.DeviceWaitIdle(_context.Device);
            _context.Vk.DestroyBuffer(_context.Device, _vertexBuffer, null);
            _context.Vk.FreeMemory(_context.Device, _vertexBufferMemory, null);
        }

        if (_pipeline.Handle != 0)
            _context.Vk.DestroyPipeline(_context.Device, _pipeline, null);

        if (_pipelineLayout.Handle != 0)
            _context.Vk.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
    }
}