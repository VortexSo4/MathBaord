using Silk.NET.Vulkan;

namespace MathBoard.Rendering;

public readonly record struct QueueFamilyIndices(
    uint GraphicsFamily,
    uint PresentFamily);