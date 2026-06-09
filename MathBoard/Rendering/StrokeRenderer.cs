using Silk.NET.Vulkan;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using MathBoard.Core;

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
    private readonly Document _document;
    private readonly Camera _camera;
    
    private bool _isEraser = false;
    private float _eraserSize = 8f;
    
    private Vector4 _currentColor = new Vector4(0.0f, 0.0f, 0.0f, 1.0f);
    private float _currentBrushWidth = 22f;
    
    private Pipeline _pipeline;
    private PipelineLayout _pipelineLayout;

    private Silk.NET.Vulkan.Buffer _vertexBuffer;
    private DeviceMemory _vertexBufferMemory;
    private uint _vertexCount;

    private readonly List<Vertex> _vertices = [];
    private bool _dirty = true;

    private Extent2D _extent;
    
    private RadialMenu? _radialMenu;

    public StrokeRenderer(
        VulkanContext context,
        SwapchainManager swapchain,
        RenderPassManager renderPass,
        CommandManager commandManager,
        Document document,
        Camera camera)
    {
        _context = context;
        _swapchain = swapchain;
        _renderPass = renderPass;
        _commandManager = commandManager;
        _document = document;
        _camera = camera;
        _extent = _swapchain.Extent;
    }
    
    public void SetRadialMenu(RadialMenu menu) => _radialMenu = menu;
    public Camera Camera => _camera;
    public float CurrentBrushWidth { get => _currentBrushWidth; set => _currentBrushWidth = value; }

    public void Initialize()
    {
        CreatePipeline();
        Console.WriteLine("StrokeRenderer: Smooth rounded strokes pipeline created");
    }

    public void UpdateExtent(Extent2D extent) => _extent = extent;

    public Vector2 ScreenToWorld(Vector2 screen)
    {
        return (screen - _camera.Position) / _camera.Zoom;
    }

    public Vector2 WorldToScreen(Vector2 world)
    {
        return world * _camera.Zoom + _camera.Position;
    }

    public void SetDirty() => _dirty = true;

    // ==================== DRAWING ====================
    public void BeginStroke(Vector2 screenPos)
    {
        if (_isEraser)
        {
            EraseAt(screenPos);
            return;
        }

        _document.SaveState();
        var worldPos = ScreenToWorld(screenPos);
        var stroke = new Stroke
        {
            Width = _currentBrushWidth,
            Color = _currentColor
        };
        stroke.Points.Add(worldPos);
        _document.Strokes.Add(stroke);
        _dirty = true;
    }

    public void AddPoint(Vector2 screenPos)
    {
        if (_isEraser)
        {
            EraseAt(screenPos);
            return;
        }

        if (_document.Strokes.Count == 0) return;
        var worldPos = ScreenToWorld(screenPos);
        _document.Strokes[^1].Points.Add(worldPos);
        _dirty = true;
    }
    
    private void EraseAt(Vector2 screenPos)
    {
        var worldPos = ScreenToWorld(screenPos);
        var radius = _eraserSize / _camera.Zoom;

        for (int i = _document.Strokes.Count - 1; i >= 0; i--)
        {
            var stroke = _document.Strokes[i];
            foreach (var p in stroke.Points)
            {
                if (Vector2.Distance(p, worldPos) < radius + stroke.Width * 0.5f)
                {
                    _document.SaveState();
                    _document.Strokes.RemoveAt(i);
                    _dirty = true;
                    return; // удаляем один Stroke за раз
                }
            }
        }
    }

    public void EndStroke()
    {
        _dirty = true;
    }

    // ==================== SMOOTH VERTEX GENERATION ====================
        private void RebuildAllVertices()
    {
        _vertices.Clear();

        const int circleSegments = 14;
        const float twoPi = MathF.PI * 2f;

        foreach (var stroke in _document.Strokes)
        {
            if (stroke.Points.Count == 0) continue;

            var color = stroke.Color;
            var radius = stroke.Width * 0.5f;

            // Круги в каждой точке (плавные стыки)
            foreach (var p in stroke.Points)
            {
                var center = WorldToScreen(p);
                for (int i = 0; i < circleSegments; i++)
                {
                    float a1 = i * twoPi / circleSegments;
                    float a2 = (i + 1) * twoPi / circleSegments;

                    _vertices.Add(new Vertex { Position = center, Color = color });
                    _vertices.Add(new Vertex { Position = center + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * radius, Color = color });
                    _vertices.Add(new Vertex { Position = center + new Vector2(MathF.Cos(a2), MathF.Sin(a2)) * radius, Color = color });
                }
            }

            // Прямоугольники между точками
            for (int i = 0; i < stroke.Points.Count - 1; i++)
            {
                var p1 = WorldToScreen(stroke.Points[i]);
                var p2 = WorldToScreen(stroke.Points[i + 1]);

                var dir = Vector2.Normalize(p2 - p1);
                var perp = new Vector2(-dir.Y, dir.X);

                var p1l = p1 + perp * radius;
                var p1r = p1 - perp * radius;
                var p2l = p2 + perp * radius;
                var p2r = p2 - perp * radius;

                _vertices.Add(new Vertex { Position = p1l, Color = color });
                _vertices.Add(new Vertex { Position = p1r, Color = color });
                _vertices.Add(new Vertex { Position = p2l, Color = color });

                _vertices.Add(new Vertex { Position = p2l, Color = color });
                _vertices.Add(new Vertex { Position = p1r, Color = color });
                _vertices.Add(new Vertex { Position = p2r, Color = color });
            }
        }

        // Добавляем UI-вершины, если меню открыто
        if (_radialMenu?.IsOpen == true)
        {
            _radialMenu.RenderUI(_vertices);
        }
    }

    public void UpdateGeometry()
    {
        if (_dirty)
        {
            RebuildAllVertices();
            UpdateVertexBuffer();
            _dirty = false;
        }
    }

    public void Render(CommandBuffer cmd)
    {
        if (_vertexCount < 3)
            return;

        // === Vulkan отрисовка ===
        var viewport = new Viewport
        {
            X = 0, Y = 0,
            Width = _extent.Width,
            Height = _extent.Height,
            MinDepth = 0, MaxDepth = 1
        };
        var scissor = new Rect2D { Offset = new Offset2D(0, 0), Extent = _extent };

        _context.Vk.CmdSetViewport(cmd, 0, 1, &viewport);
        _context.Vk.CmdSetScissor(cmd, 0, 1, &scissor);

        var projection = Matrix4x4.CreateOrthographicOffCenter(
            0, _extent.Width, 0, _extent.Height, -1f, 1f);

        _context.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);

        var vb = _vertexBuffer;
        var offset = 0ul;
        _context.Vk.CmdBindVertexBuffers(cmd, 0, 1, &vb, &offset);

        Matrix4x4* pProj = &projection;
        _context.Vk.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), pProj);

        _context.Vk.CmdDraw(cmd, _vertexCount, 1, 0, 0);
    }

    // ==================== Vulkan Pipeline & Buffers (оставляем как было) ====================
    private void CreatePipeline()
    {
        var vertShader = LoadShader("Shaders/stroke.vert.spv");
        var fragShader = LoadShader("Shaders/stroke.frag.spv");

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
            Stride = (uint)sizeof(Vertex),
            InputRate = VertexInputRate.Vertex
        };

        var attributes = stackalloc VertexInputAttributeDescription[3];
        attributes[0] = new VertexInputAttributeDescription { Location = 0, Binding = 0, Format = Format.R32G32Sfloat, Offset = (uint)Marshal.OffsetOf<Vertex>("Position") };
        attributes[1] = new VertexInputAttributeDescription { Location = 1, Binding = 0, Format = Format.R32Sfloat, Offset = (uint)Marshal.OffsetOf<Vertex>("Thickness") };
        attributes[2] = new VertexInputAttributeDescription { Location = 2, Binding = 0, Format = Format.R32G32B32A32Sfloat, Offset = (uint)Marshal.OffsetOf<Vertex>("Color") };

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

        var viewport = new Viewport { Width = 1, Height = 1, MinDepth = 0, MaxDepth = 1 };
        var scissor = new Rect2D { Extent = new Extent2D { Width = 1, Height = 1 } };

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
            PolygonMode = PolygonMode.Fill,
            CullMode = CullModeFlags.None,
            LineWidth = 1.0f
        };

        var multisampling = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = SampleCountFlags.Count1Bit
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
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit
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

        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
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

    private void UpdateVertexBuffer()
    {
        // Сначала безопасно уничтожаем старый буфер, если он есть
        if (_vertexBuffer.Handle != 0)
        {
            _context.Vk.DeviceWaitIdle(_context.Device); // важно!
            _context.Vk.DestroyBuffer(_context.Device, _vertexBuffer, null);
            _context.Vk.FreeMemory(_context.Device, _vertexBufferMemory, null);
        
            _vertexBuffer = default;
            _vertexBufferMemory = default;
        }

        _vertexCount = 0;

        if (_vertices.Count == 0)
            return;

        var bufferSize = (ulong)(sizeof(Vertex) * _vertices.Count);

        // Staging buffer
        CreateBuffer(bufferSize, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var stagingBuffer, out var stagingMemory);

        void* mapped;
        _context.Vk.MapMemory(_context.Device, stagingMemory, 0, bufferSize, 0, &mapped);
        fixed (Vertex* src = CollectionsMarshal.AsSpan(_vertices))
        {
            System.Buffer.MemoryCopy(src, mapped, bufferSize, bufferSize);
        }
        _context.Vk.UnmapMemory(_context.Device, stagingMemory);

        // GPU buffer
        CreateBuffer(bufferSize, BufferUsageFlags.TransferDstBit | BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.DeviceLocalBit, out _vertexBuffer, out _vertexBufferMemory);

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
                return (uint)i;
        }
        throw new Exception("Failed to find suitable memory type");
    }

    private void CopyBuffer(Silk.NET.Vulkan.Buffer src, Silk.NET.Vulkan.Buffer dst, ulong size)
    {
        // (оставь как было в предыдущей версии — используй temporary command buffer)
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
    
    public void ToggleEraser()
    {
        _isEraser = !_isEraser;
        Console.WriteLine($"Eraser mode: {_isEraser}");
    }

    public void ToggleEraser(bool enable)
    {
        _isEraser = enable;
        Console.WriteLine($"Eraser mode: {_isEraser}");
    }

    public void SetColor(Vector4 color)
    {
        _currentColor = color;
        Console.WriteLine($"Color changed to: {color}");
    }

    public void ClearAll()
    {
        _document.SaveState();
        _document.Strokes.Clear();
        _dirty = true;
        Console.WriteLine("Canvas cleared");
    }

    public void Undo()
    {
        _document.Undo();
        _dirty = true;
    }

    public void Redo()
    {
        _document.Redo();
        _dirty = true;
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