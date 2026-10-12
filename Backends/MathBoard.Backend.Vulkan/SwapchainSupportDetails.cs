using Silk.NET.Vulkan;

namespace MathBoard.Backend.Vulkan;

public sealed class SwapchainSupportDetails
{
    public SurfaceCapabilitiesKHR Capabilities;
    public SurfaceFormatKHR[] Formats = [];
    public PresentModeKHR[] PresentModes = [];
}