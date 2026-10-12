namespace MathBoard.Backend.Vulkan;

public readonly record struct QueueFamilyIndices(
    uint GraphicsFamily,
    uint PresentFamily);