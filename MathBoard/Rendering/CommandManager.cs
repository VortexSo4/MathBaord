using MathBoard.Core;
using Silk.NET.Vulkan;

namespace MathBoard.Rendering;

public sealed unsafe class CommandManager : IDisposable
{
    private readonly VulkanContext _context;

    private CommandPool _commandPool;
    private CommandBuffer[] _commandBuffers = [];
    

    public CommandManager(VulkanContext context)
    {
        _context = context;
    }

    public void Initialize(uint framebufferCount)
    {
        CreateCommandPool();
        CreateCommandBuffers(framebufferCount);
        Console.WriteLine($"Command buffers created: {_commandBuffers.Length}");
    }

    private void CreateCommandPool()
    {
        CommandPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = _context.QueueFamilies.GraphicsFamily
        };

        var result = _context.Vk.CreateCommandPool(
            _context.Device,
            &poolInfo,
            null,
            out _commandPool);

        if (result != Result.Success)
            throw new Exception($"CreateCommandPool failed: {result}");
    }

    private void CreateCommandBuffers(uint count)
    {
        _commandBuffers = new CommandBuffer[count];

        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = count
        };

        fixed (CommandBuffer* buffersPtr = _commandBuffers)
        {
            var result = _context.Vk.AllocateCommandBuffers(
                _context.Device,
                &allocInfo,
                buffersPtr);

            if (result != Result.Success)
                throw new Exception($"AllocateCommandBuffers failed: {result}");
        }
    }

    public void RecordCommandBuffers(
        IReadOnlyList<Framebuffer> framebuffers,
        RenderPass renderPass,
        Extent2D extent,
        StrokeRenderer strokeRenderer)
    {
        for (int i = 0; i < _commandBuffers.Length; i++)
        {
            var cmd = _commandBuffers[i];

            _context.Vk.ResetCommandBuffer(cmd, 0);
        
            CommandBufferBeginInfo beginInfo = new() { SType = StructureType.CommandBufferBeginInfo };
            _context.Vk.BeginCommandBuffer(cmd, &beginInfo);

            RenderPassBeginInfo renderPassInfo = new()
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = renderPass,
                Framebuffer = framebuffers[i],
                RenderArea = new Rect2D { Extent = extent }
            };

            ClearValue clearColor = new()
            {
                Color = new ClearColorValue 
                { 
                    Float32_0 = Settings.BackgroundColor.Value.X,
                    Float32_1 = Settings.BackgroundColor.Value.Y,
                    Float32_2 = Settings.BackgroundColor.Value.Z,
                    Float32_3 = Settings.BackgroundColor.Value.W
                }
            };

            renderPassInfo.ClearValueCount = 1;
            renderPassInfo.PClearValues = &clearColor;

            _context.Vk.CmdBeginRenderPass(cmd, &renderPassInfo, SubpassContents.Inline);
        
            // ← Рисуем наши линии
            strokeRenderer.Render(cmd);

            _context.Vk.CmdEndRenderPass(cmd);

            var endResult = _context.Vk.EndCommandBuffer(cmd);
            if (endResult != Result.Success)
                throw new Exception($"EndCommandBuffer failed: {endResult}");
        }
    }
    
    private void FreeCommandBuffers()
    {
        if (_commandBuffers.Length == 0)
            return;

        fixed (CommandBuffer* ptr = _commandBuffers)
        {
            _context.Vk.FreeCommandBuffers(
                _context.Device,
                _commandPool,
                (uint)_commandBuffers.Length,
                ptr);
        }

        _commandBuffers = [];
    }
    
    public void Recreate(
        uint framebufferCount,
        IReadOnlyList<Framebuffer> framebuffers,
        RenderPass renderPass,
        Extent2D extent,
        StrokeRenderer strokeRenderer)
    {
        FreeCommandBuffers();
        CreateCommandBuffers(framebufferCount);
        RecordCommandBuffers(framebuffers, renderPass, extent, strokeRenderer);
    }

    public CommandBuffer[] CommandBuffers => _commandBuffers;
    public CommandPool CommandPool => _commandPool;

    public void Dispose()
    {
        if (_commandBuffers.Length > 0)
        {
            fixed (CommandBuffer* buffersPtr = _commandBuffers)
            {
                _context.Vk.FreeCommandBuffers(_context.Device, _commandPool, (uint)_commandBuffers.Length, buffersPtr);
            }
        }

        if (_commandPool.Handle != 0)
        {
            _context.Vk.DestroyCommandPool(_context.Device, _commandPool, null);
        }
    }
}