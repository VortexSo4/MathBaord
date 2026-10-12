using Silk.NET.Vulkan;

namespace MathBoard.Rendering;

public sealed unsafe class FramebufferManager : IDisposable
{
    private readonly VulkanContext _context;
    private readonly SwapchainManager _swapchain;
    private readonly RenderPassManager _renderPass;

    private Framebuffer[] _framebuffers = [];

    public FramebufferManager(
        VulkanContext context,
        SwapchainManager swapchain,
        RenderPassManager renderPass)
    {
        _context = context;
        _swapchain = swapchain;
        _renderPass = renderPass;
    }

    public void Initialize()
    {
        CreateFramebuffers();

        Console.WriteLine(
            $"Framebuffers created: {_framebuffers.Length}");
    }

    private void CreateFramebuffers()
    {
        _framebuffers = new Framebuffer[_swapchain.ImageViews.Count];

        for (int i = 0; i < _swapchain.ImageViews.Count; i++)
        {
            var attachment = _swapchain.ImageViews[i];
            FramebufferCreateInfo fbInfo = new()
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _renderPass.RenderPass,
                AttachmentCount = 1,
                PAttachments = &attachment,
                Width = _swapchain.Extent.Width,
                Height = _swapchain.Extent.Height,
                Layers = 1
            };
            _context.Vk.CreateFramebuffer(_context.Device, &fbInfo, null, out _framebuffers[i]);
        }
    }

    private void DestroyFramebuffers()
    {
        foreach (var fb in _framebuffers)
            if (fb.Handle != 0) _context.Vk.DestroyFramebuffer(_context.Device, fb, null);
        _framebuffers = [];
    }

    public void Recreate()
    {
        DestroyFramebuffers();
        CreateFramebuffers();

        Console.WriteLine(
            $"Framebuffers recreated: {_framebuffers.Length}");
    }

    public IReadOnlyList<Framebuffer> Framebuffers => _framebuffers;

    public void Dispose()
    {
        DestroyFramebuffers();
    }
}
