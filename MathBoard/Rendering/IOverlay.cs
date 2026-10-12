using System.Numerics;

namespace MathBoard.Rendering;

/// <summary>Interface layer drawn above the panels, such as popups opened from the canvas.</summary>
public interface IOverlay
{
    void Render(UiBatch ui, Vector2 screenSize);
}
