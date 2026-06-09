using Silk.NET.Vulkan;

namespace MathBoard.Rendering;

public sealed unsafe class FrameSync : IDisposable
{
    private readonly VulkanContext _context;

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
    }

    private void CreateSyncObjects()
    {
        SemaphoreCreateInfo semaphoreInfo = new() { SType = StructureType.SemaphoreCreateInfo };
        FenceCreateInfo fenceInfo = new()
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit
        };

        _context.Vk.CreateSemaphore(_context.Device, &semaphoreInfo, null, out _imageAvailableSemaphore);
        _context.Vk.CreateSemaphore(_context.Device, &semaphoreInfo, null, out _renderFinishedSemaphore);
        _context.Vk.CreateFence(_context.Device, &fenceInfo, null, out _inFlightFence);
    }

    public bool DrawFrame(SwapchainManager swapchain, CommandManager commandManager)
    {
        // 1. Wait for fence with timeout
        fixed (Fence* fencePtr = &_inFlightFence)
        {
            var waitResult = _context.Vk.WaitForFences(_context.Device, 1, fencePtr, true, 500_000_000); // 500ms

            if (waitResult == Result.Timeout)
            {
                Console.WriteLine("WARNING: WaitForFences timeout → forcing recreate");
                return false;
            }

            if (waitResult != Result.Success)
                throw new Exception($"WaitForFences failed: {waitResult}");
        }

        // 2. Acquire next image
        uint imageIndex = 0;
        var acquireResult = swapchain.KhrSwapchain.AcquireNextImage(
            _context.Device,
            swapchain.Swapchain,
            1_000_000_000, // 1 second timeout
            _imageAvailableSemaphore,
            default,
            &imageIndex);

        if (acquireResult is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr || acquireResult == Result.Timeout)
            return false;

        if (acquireResult != Result.Success)
            throw new Exception($"AcquireNextImage failed: {acquireResult}");

        // 3. Reset fence
        fixed (Fence* fencePtr = &_inFlightFence)
            _context.Vk.ResetFences(_context.Device, 1, fencePtr);

        // 4. Submit — исправлено: используем fixed для семафоров
        var commandBuffer = commandManager.CommandBuffers[imageIndex];
        PipelineStageFlags waitStage = PipelineStageFlags.ColorAttachmentOutputBit;

        fixed (Silk.NET.Vulkan.Semaphore* pWaitSemaphore = &_imageAvailableSemaphore)
        fixed (Silk.NET.Vulkan.Semaphore* pSignalSemaphore = &_renderFinishedSemaphore)
        {
            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = pWaitSemaphore,
                PWaitDstStageMask = &waitStage,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = pSignalSemaphore
            };

            var submitResult = _context.Vk.QueueSubmit(_context.GraphicsQueue, 1, &submitInfo, _inFlightFence);
            if (submitResult != Result.Success)
                throw new Exception($"QueueSubmit failed: {submitResult}");
        }

        // 5. Present — исправлено
        fixed (Silk.NET.Vulkan.Semaphore* pWaitSemaphore = &_renderFinishedSemaphore)
        {
            var swapchainHandle = swapchain.Swapchain;
            SwapchainKHR* pSwapchain = &swapchainHandle;
            {
                PresentInfoKHR presentInfo = new()
                {
                    SType = StructureType.PresentInfoKhr,
                    WaitSemaphoreCount = 1,
                    PWaitSemaphores = pWaitSemaphore,
                    SwapchainCount = 1,
                    PSwapchains = pSwapchain,
                    PImageIndices = &imageIndex
                };

                var presentResult = swapchain.KhrSwapchain.QueuePresent(_context.PresentQueue, &presentInfo);

                if (presentResult is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr)
                    return false;

                if (presentResult != Result.Success)
                    throw new Exception($"QueuePresent failed: {presentResult}");
            }
        }

        return true;
    }

    public void Dispose()
    {
        _context.Vk.DeviceWaitIdle(_context.Device);

        _context.Vk.DestroySemaphore(_context.Device, _imageAvailableSemaphore, null);
        _context.Vk.DestroySemaphore(_context.Device, _renderFinishedSemaphore, null);
        _context.Vk.DestroyFence(_context.Device, _inFlightFence, null);
    }
}