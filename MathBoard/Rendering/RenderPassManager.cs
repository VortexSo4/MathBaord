using Silk.NET.Vulkan;

namespace MathBoard.Rendering;

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
        SampleCountFlags samples = _context.GetSampleCount();
        // Принудительно используем 4 сэмпла, если поддерживается, иначе 2 или 1
        if (samples >= SampleCountFlags.Count4Bit)
            samples = SampleCountFlags.Count4Bit;
        else if (samples >= SampleCountFlags.Count2Bit)
            samples = SampleCountFlags.Count2Bit;
        else
            samples = SampleCountFlags.Count1Bit;

        // 1. Мультисэмпловый цветовой аттачмент
        AttachmentDescription colorAttachment = new()
        {
            Format = _swapchain.ImageFormat,
            Samples = samples,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare, // не сохраняем, только resolve
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ColorAttachmentOptimal
        };

        // 2. Resolve аттачмент (обычный, один сэмпл) – это swapchain изображение
        AttachmentDescription resolveAttachment = new()
        {
            Format = _swapchain.ImageFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.DontCare,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr
        };

        AttachmentReference colorRef = new() { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
        AttachmentReference resolveRef = new() { Attachment = 1, Layout = ImageLayout.ColorAttachmentOptimal };

        SubpassDescription subpass = new()
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
            PResolveAttachments = &resolveRef
        };

        var attachments = stackalloc AttachmentDescription[2];
        attachments[0] = colorAttachment;
        attachments[1] = resolveAttachment;

        RenderPassCreateInfo createInfo = new()
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 2,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass
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