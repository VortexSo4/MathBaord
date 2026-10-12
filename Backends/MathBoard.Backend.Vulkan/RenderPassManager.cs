using Silk.NET.Vulkan;

namespace MathBoard.Backend.Vulkan;

public sealed unsafe class RenderPassManager : IDisposable
{
    private readonly VulkanContext _context;
    private readonly SwapchainManager _swapchain;

    private RenderPass _renderPass;

    public RenderPassManager(VulkanContext context, SwapchainManager swapchain)
    {
        _context = context;
        _swapchain = swapchain;
    }

    public void Initialize()
    {
        CreateRenderPass();
        Console.WriteLine("RenderPass created successfully");
    }

    private void CreateRenderPass()
    {
        // Single-sampled: all shapes are anti-aliased analytically in prim.frag.
        AttachmentDescription colorAttachment = new()
        {
            Format = _swapchain.ImageFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr
        };

        AttachmentReference colorRef = new() { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };

        SubpassDescription subpass = new()
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef
        };

        SubpassDependency dependency = new()
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit
        };

        RenderPassCreateInfo createInfo = new()
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &colorAttachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency
        };

        var result = _context.Vk.CreateRenderPass(_context.Device, &createInfo, null, out _renderPass);
        if (result != Result.Success)
            throw new Exception($"CreateRenderPass failed: {result}");
    }

    public RenderPass RenderPass => _renderPass;

    public void Dispose()
    {
        if (_renderPass.Handle != 0)
        {
            _context.Vk.DestroyRenderPass(_context.Device, _renderPass, null);
        }
    }
}
