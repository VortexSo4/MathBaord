using Silk.NET.Vulkan;

namespace MathBoard.Rendering;

public sealed class SwapchainSupportDetails
{
    public SurfaceCapabilitiesKHR Capabilities;
    public SurfaceFormatKHR[] Formats = [];
    public PresentModeKHR[] PresentModes = [];
}