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

    public DrawFrameResult DrawFrame(SwapchainManager swapchain, CommandManager commandManager)
    {
        fixed (Fence* fencePtr = &_inFlightFence)
        {
            var waitResult = _context.Vk.WaitForFences(_context.Device, 1, fencePtr, true, 500_000_000);
            if (waitResult == Result.Timeout)
            {
                Console.WriteLine("WARNING: WaitForFences timeout — skipping frame");
                return DrawFrameResult.SkipFrame;
            }

            if (waitResult != Result.Success)
                throw new Exception($"WaitForFences failed: {waitResult}");
        }

        uint imageIndex = 0;
        var acquireResult = swapchain.KhrSwapchain.AcquireNextImage(
            _context.Device,
            swapchain.Swapchain,
            1_000_000_000,
            _imageAvailableSemaphore,
            default,
            &imageIndex);

        if (acquireResult == Result.ErrorOutOfDateKhr)
            return DrawFrameResult.NeedsRecreation;

        if (acquireResult == Result.Timeout)
            return DrawFrameResult.SkipFrame;

        if (acquireResult != Result.Success && acquireResult != Result.SuboptimalKhr)
            throw new Exception($"AcquireNextImage failed: {acquireResult}");

        bool needsRecreationAfter = acquireResult == Result.SuboptimalKhr;

        fixed (Fence* fencePtr = &_inFlightFence)
            _context.Vk.ResetFences(_context.Device, 1, fencePtr);

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

        fixed (Silk.NET.Vulkan.Semaphore* pWaitSemaphore = &_renderFinishedSemaphore)
        {
            var swapchainHandle = swapchain.Swapchain;
            SwapchainKHR* pSwapchain = &swapchainHandle;

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
                return DrawFrameResult.NeedsRecreation;

            if (presentResult != Result.Success)
                throw new Exception($"QueuePresent failed: {presentResult}");
        }

        return needsRecreationAfter ? DrawFrameResult.NeedsRecreation : DrawFrameResult.Success;
    }

    public bool WaitForPreviousFrame()
    {
        fixed (Fence* fencePtr = &_inFlightFence)
        {
            var result = _context.Vk.WaitForFences(_context.Device, 1, fencePtr, true, 500_000_000);
            if (result == Result.Timeout)
            {
                Console.WriteLine("WARNING: WaitForFences timeout — skipping frame");
                return false;
            }

            if (result != Result.Success)
                throw new Exception($"WaitForFences failed: {result}");
            return true;
        }
    }

    public DrawFrameResult SubmitFrame(SwapchainManager swapchain, CommandManager commandManager)
    {
        uint imageIndex = 0;
        var acquireResult = swapchain.KhrSwapchain.AcquireNextImage(
            _context.Device, swapchain.Swapchain, 1_000_000_000,
            _imageAvailableSemaphore, default, &imageIndex);

        if (acquireResult == Result.ErrorOutOfDateKhr)
            return DrawFrameResult.NeedsRecreation;

        if (acquireResult == Result.Timeout)
            return DrawFrameResult.SkipFrame;

        if (acquireResult != Result.Success && acquireResult != Result.SuboptimalKhr)
            throw new Exception($"AcquireNextImage failed: {acquireResult}");

        bool needsRecreationAfter = acquireResult == Result.SuboptimalKhr;

        fixed (Fence* fencePtr = &_inFlightFence)
            _context.Vk.ResetFences(_context.Device, 1, fencePtr);

        var commandBuffer = commandManager.CommandBuffers[imageIndex];
        PipelineStageFlags waitStage = PipelineStageFlags.ColorAttachmentOutputBit;

        fixed (Silk.NET.Vulkan.Semaphore* pWait = &_imageAvailableSemaphore)
        fixed (Silk.NET.Vulkan.Semaphore* pSignal = &_renderFinishedSemaphore)
        {
            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = pWait,
                PWaitDstStageMask = &waitStage,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = pSignal
            };

            var submitResult = _context.Vk.QueueSubmit(_context.GraphicsQueue, 1, &submitInfo, _inFlightFence);
            if (submitResult != Result.Success)
                throw new Exception($"QueueSubmit failed: {submitResult}");
        }

        fixed (Silk.NET.Vulkan.Semaphore* pWait = &_renderFinishedSemaphore)
        {
            var swapchainHandle = swapchain.Swapchain;
            PresentInfoKHR presentInfo = new()
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = pWait,
                SwapchainCount = 1,
                PSwapchains = &swapchainHandle,
                PImageIndices = &imageIndex
            };

            var presentResult = swapchain.KhrSwapchain.QueuePresent(_context.PresentQueue, &presentInfo);
            if (presentResult is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr)
                return DrawFrameResult.NeedsRecreation;
            if (presentResult != Result.Success)
                throw new Exception($"QueuePresent failed: {presentResult}");
        }

        return needsRecreationAfter ? DrawFrameResult.NeedsRecreation : DrawFrameResult.Success;
    }

    public void Dispose()
    {
        _context.Vk.DeviceWaitIdle(_context.Device);

        _context.Vk.DestroySemaphore(_context.Device, _imageAvailableSemaphore, null);
        _context.Vk.DestroySemaphore(_context.Device, _renderFinishedSemaphore, null);
        _context.Vk.DestroyFence(_context.Device, _inFlightFence, null);
    }
}