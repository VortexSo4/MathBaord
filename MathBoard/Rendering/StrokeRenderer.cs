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

    private LibraryPanel? _libraryPanel;
    public event Action? OnSceneChanged;
    public bool IsDirty => _dirty;
    public void SetLibraryPanel(LibraryPanel panel) => _libraryPanel = panel;

    private bool _isEraser;
    private float _eraserSize = 8f;

    private Vector4 _currentColor = Settings.Colors[0];
    private float _currentBrushWidth = 22f;

    private Pipeline _pipeline;
    private PipelineLayout _pipelineLayout;

    private Silk.NET.Vulkan.Buffer _vertexBuffer;
    private DeviceMemory _vertexBufferMemory;
    private ulong _vertexBufferAllocatedSize;
    private uint _vertexCount;
    private uint _strokeVertexCount;
    private uint _uiVertexCount;

    private readonly List<Vertex> _vertices = [];
    private bool _dirty = true;

    private Extent2D _extent;

    private RadialMenu? _radialMenu;

    // Полноценный текст (используется LibraryPanel'ом для колонки автосохранений).
    // Атлас перестраивается редко (см. LibraryPanel.RefreshTree), кадровая генерация
    // quad'ов — дёшево, так что рендер не проседает.
    private TextAtlas? _textAtlas;
    public TextAtlas TextAtlas => _textAtlas!;

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

        _currentBrushWidth = Settings.DefaultBrushWidth;
        _eraserSize = Settings.DefaultEraserSize;
    }

    public void SetRadialMenu(RadialMenu menu) => _radialMenu = menu;
    public Camera Camera => _camera;

    public float CurrentBrushWidth
    {
        get => _currentBrushWidth;
        set => _currentBrushWidth = value;
    }

    public void Initialize()
    {
        CreatePipeline();

        _textAtlas = new TextAtlas(_context, _renderPass, _commandManager);
        _textAtlas.Initialize();

        Console.WriteLine("StrokeRenderer: Pipeline created (world-space vertices, GPU camera transform)");
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
            _document.SaveState();
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

    public void EraseAt(Vector2 screenPos, bool saveState = false)
    {
        var worldPos = ScreenToWorld(screenPos);
        var radius = _eraserSize;

        var toRemove = new List<int>();
        for (int i = 0; i < _document.Strokes.Count; i++)
        {
            if (IsStrokeHitByEraser(_document.Strokes[i], worldPos, radius))
                toRemove.Add(i);
        }

        if (toRemove.Count == 0) return;
        if (saveState) _document.SaveState();
        for (int i = toRemove.Count - 1; i >= 0; i--)
            _document.Strokes.RemoveAt(toRemove[i]);
        _dirty = true;
    }
    
    public void RequestRadialMenuIcons()
    {
        _radialMenu?.RequestIcons(_textAtlas!);
    }

    private static bool IsStrokeHitByEraser(Stroke stroke, Vector2 eraserPos, float eraserRadius)
    {
        float threshold = eraserRadius + stroke.Width * 0.5f;
        float thresholdSq = threshold * threshold;
        var pts = stroke.Points;
        if (pts.Count == 0) return false;
        if (pts.Count == 1)
            return Vector2.DistanceSquared(pts[0], eraserPos) < thresholdSq;

        for (int i = 0; i < pts.Count - 1; i++)
        {
            if (PointToSegmentDistanceSq(eraserPos, pts[i], pts[i + 1]) < thresholdSq)
                return true;
        }
        return false;
    }

    private static float PointToSegmentDistanceSq(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lenSq = ab.LengthSquared();
        if (lenSq < 1e-6f) return Vector2.DistanceSquared(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / lenSq, 0f, 1f);
        return Vector2.DistanceSquared(p, a + ab * t);
    }

    public void EndStroke()
    {
        _dirty = true;
    }

    // ==================== VERTEX GENERATION (WORLD SPACE) ====================
    // Вершины штрихов генерируются в WORLD SPACE — трансформация камеры
    // применяется в шейдере через push constant. Движение/зум камеры
    // НЕ требует ребилда вершин.
    private void RebuildAllVertices()
    {
        _vertices.Clear();

        int circleSegments = Settings.StrokeCircleSegments;
        const float twoPi = MathF.PI * 2f;

        // --- Штрихи: WORLD SPACE ---
        foreach (var stroke in _document.Strokes)
        {
            if (stroke.Points.Count == 0) continue;

            var color = stroke.Color;
            var radius = stroke.Width * 0.5f; // world-space радиус (без зума)

            // Круги в каждой точке (плавные стыки)
            foreach (var p in stroke.Points)
            {
                for (int i = 0; i < circleSegments; i++)
                {
                    float a1 = i * twoPi / circleSegments;
                    float a2 = (i + 1) * twoPi / circleSegments;

                    _vertices.Add(new Vertex { Position = p, Color = color });
                    _vertices.Add(new Vertex { Position = p + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * radius, Color = color });
                    _vertices.Add(new Vertex { Position = p + new Vector2(MathF.Cos(a2), MathF.Sin(a2)) * radius, Color = color });
                }
            }

            // Прямоугольники между точками
            for (int i = 0; i < stroke.Points.Count - 1; i++)
            {
                var p1 = stroke.Points[i];
                var p2 = stroke.Points[i + 1];

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

        _strokeVertexCount = (uint)_vertices.Count;

        // --- UI: SCREEN SPACE ---
        // RadialMenu и LibraryPanel генерируют экранные координаты.
        // Для них используется identity-трансформ (только ortho).
        _textAtlas?.BeginFrame();

        if (_radialMenu?.IsOpen == true)
        {
            _radialMenu.RenderUI(_vertices);
        }
        if (_libraryPanel?.IsOpen == true)
        {
            _libraryPanel.RenderToVertices(_vertices, new Vector2(_extent.Width, _extent.Height));
        }

        _uiVertexCount = (uint)_vertices.Count - _strokeVertexCount;
        _vertexCount = (uint)_vertices.Count;
    }

    public void UpdateGeometry()
    {
        if (_dirty)
        {
            RebuildAllVertices();
            UpdateVertexBuffer();
            _textAtlas?.UploadFrameVertices();
            _dirty = false;
        }
    }

    // ==================== RENDER ====================
    public void Render(CommandBuffer cmd)
    {
        if (_vertexCount == 0 || _vertexBuffer.Handle == 0)
            return;

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

        _context.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);

        var vb = _vertexBuffer;
        var offset = 0ul;
        _context.Vk.CmdBindVertexBuffers(cmd, 0, 1, &vb, &offset);

        // Draw 1: штрихи (world-space → camera transform)
        if (_strokeVertexCount > 0)
        {
            var transform = ComputeCameraTransform();
            _context.Vk.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.VertexBit,
                0, (uint)sizeof(Matrix4x4), &transform);
            _context.Vk.CmdDraw(cmd, _strokeVertexCount, 1, 0, 0);
        }

        // Draw 2: UI (screen-space → ortho only)
        if (_uiVertexCount > 0)
        {
            var transform = Matrix4x4.CreateOrthographicOffCenter(
                0, _extent.Width, 0, _extent.Height, -1f, 1f);
            _context.Vk.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.VertexBit,
                0, (uint)sizeof(Matrix4x4), &transform);
            _context.Vk.CmdDraw(cmd, _uiVertexCount, 1, _strokeVertexCount, 0);
        }

        // Draw 3: текст (тот же экранный ortho, отдельный textured pipeline)
        _textAtlas?.Render(cmd, _extent);
    }

    // screenPos = worldPos * zoom + cameraPos
    // Matrix (row-major, v * M): Scale(zoom) * Translate(cam) * Proj
    // GLSL получает транспонированную (column-major) — M^T * v = v * M ✓
    private Matrix4x4 ComputeCameraTransform()
    {
        float z = _camera.Zoom;
        var cam = _camera.Position;

        var view = Matrix4x4.CreateScale(z, z, 1f) * Matrix4x4.CreateTranslation(cam.X, cam.Y, 0f);
        var proj = Matrix4x4.CreateOrthographicOffCenter(0, _extent.Width, 0, _extent.Height, -1f, 1f);
        return view * proj;
    }

    // ==================== Vulkan Pipeline ====================
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

        // 2 атрибута вместо 3 (убран Thickness)
        var attributes = stackalloc VertexInputAttributeDescription[2];
        attributes[0] = new VertexInputAttributeDescription
        {
            Location = 0, Binding = 0, Format = Format.R32G32Sfloat,
            Offset = (uint)Marshal.OffsetOf<Vertex>("Position")
        };
        attributes[1] = new VertexInputAttributeDescription
        {
            Location = 1, Binding = 0, Format = Format.R32G32B32A32Sfloat,
            Offset = (uint)Marshal.OffsetOf<Vertex>("Color")
        };

        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = 1,
            PVertexBindingDescriptions = &binding,
            VertexAttributeDescriptionCount = 2,
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

    // ==================== PERSISTENT HOST-VISIBLE VERTEX BUFFER ====================
    // Без staging buffer, без QueueWaitIdle. Буфер живёт постоянно,
    // перераспределяется только при росте. Синхронизация через fence кадра.
    private void UpdateVertexBuffer()
    {
        _vertexCount = (uint)_vertices.Count;

        if (_vertices.Count == 0)
            return;

        var requiredSize = (ulong)(sizeof(Vertex) * _vertices.Count);

        // Перераспределяем только при нехватке места
        if (_vertexBuffer.Handle == 0 || _vertexBufferAllocatedSize < requiredSize)
        {
            if (_vertexBuffer.Handle != 0)
            {
                _context.Vk.DeviceWaitIdle(_context.Device);
                _context.Vk.DestroyBuffer(_context.Device, _vertexBuffer, null);
                _context.Vk.FreeMemory(_context.Device, _vertexBufferMemory, null);
            }

            // Выделяем с запасом (минимум 1 MB), чтобы избежать частых реаллокаций
            var allocSize = Math.Max(requiredSize * 2, 1UL << 20);
            CreateBuffer(allocSize, BufferUsageFlags.VertexBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _vertexBuffer, out _vertexBufferMemory);
            _vertexBufferAllocatedSize = allocSize;
        }

        // Прямой map + copy — без staging, без QueueWaitIdle
        void* mapped;
        _context.Vk.MapMemory(_context.Device, _vertexBufferMemory, 0, requiredSize, 0, &mapped);
        fixed (Vertex* src = CollectionsMarshal.AsSpan(_vertices))
        {
            System.Buffer.MemoryCopy(src, mapped, requiredSize, requiredSize);
        }
        _context.Vk.UnmapMemory(_context.Device, _vertexBufferMemory);
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

    // CopyBuffer полностью удалён — не нужен с host-visible буфером

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

        _textAtlas?.Dispose();
    }
}