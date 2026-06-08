using Silk.NET.Vulkan;

namespace MathBoard.Rendering;

public sealed unsafe class FrameSync : IDisposable
{
    private readonly VulkanContext _context;

    // Используем полное имя, чтобы избежать неоднозначности
    private Silk.NET.Vulkan.Semaphore _imageAvailableSemaphore;
    private Silk.NET.Vulkan.Semaphore _renderFinishedSemaphore;
    private Fence _inFlightFence;

    public FrameSync(VulkanContext context)
    {
        _context = context;
    }

    public void Initialize()
    {
        CreateSyncObjects();
        Console.WriteLine("Synchronization objects created");
    }

    private void CreateSyncObjects()
    {
        SemaphoreCreateInfo semaphoreInfo = new()
        {
            SType = StructureType.SemaphoreCreateInfo
        };

        FenceCreateInfo fenceInfo = new()
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit
        };

        _context.Vk.CreateSemaphore(_context.Device, &semaphoreInfo, null, out _imageAvailableSemaphore);
        _context.Vk.CreateSemaphore(_context.Device, &semaphoreInfo, null, out _renderFinishedSemaphore);
        _context.Vk.CreateFence(_context.Device, &fenceInfo, null, out _inFlightFence);
    }

    public void DrawFrame(
        SwapchainManager swapchain,
        CommandManager commandManager)
    {
        // Ждём предыдущий кадр
        fixed (Fence* fencePtr = &_inFlightFence)
        {
            _context.Vk.WaitForFences(
                _context.Device,
                1,
                fencePtr,
                true,
                ulong.MaxValue);

            _context.Vk.ResetFences(
                _context.Device,
                1,
                fencePtr);
        }

        // Acquire next image
        uint imageIndex = 0;

        var acquireResult = swapchain.KhrSwapchain.AcquireNextImage(
            _context.Device,
            swapchain.Swapchain,
            ulong.MaxValue,
            _imageAvailableSemaphore,
            default,
            &imageIndex);

        if (acquireResult != Result.Success && acquireResult != Result.SuboptimalKhr)
        {
            Console.WriteLine($"AcquireNextImage failed: {acquireResult}");
            return;
        }

        // Submit
        var commandBuffer = commandManager.CommandBuffers[imageIndex];

        PipelineStageFlags waitStage = PipelineStageFlags.ColorAttachmentOutputBit;

        // Используем локальные переменные для указателей
        var waitSemaphore = _imageAvailableSemaphore;
        var signalSemaphore = _renderFinishedSemaphore;

        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSemaphore,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &signalSemaphore
        };

        var submitResult = _context.Vk.QueueSubmit(
            _context.GraphicsQueue,
            1,
            &submitInfo,
            _inFlightFence);

        if (submitResult != Result.Success)
        {
            Console.WriteLine($"QueueSubmit failed: {submitResult}");
            return;
        }

        // Present
        var presentSwapchain = swapchain.Swapchain;
        PresentInfoKHR presentInfo = new()
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &signalSemaphore,
            SwapchainCount = 1,
            PSwapchains = &presentSwapchain,
            PImageIndices = &imageIndex
        };

        var presentResult = swapchain.KhrSwapchain.QueuePresent(
            _context.PresentQueue,
            &presentInfo);

        if (presentResult != Result.Success && presentResult != Result.SuboptimalKhr)
        {
            Console.WriteLine($"QueuePresent failed: {presentResult}");
        }
    }

    public void Dispose()
    {
        _context.Vk.DestroySemaphore(_context.Device, _imageAvailableSemaphore, null);
        _context.Vk.DestroySemaphore(_context.Device, _renderFinishedSemaphore, null);
        _context.Vk.DestroyFence(_context.Device, _inFlightFence, null);
    }
}
