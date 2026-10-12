using System.Numerics;

namespace MathBoard.Rendering;

/// <summary>Interface layer drawn above the panels, such as popups opened from the canvas.</summary>
public interface IOverlay
{
    void Render(UiBatch ui, Vector2 screenSize);
}

/// <summary>A modal popup over the canvas: while open it receives all pointer input.</summary>
public interface IPopup : IOverlay
{
    bool IsOpen { get; }
    void OnMouseDown(Vector2 pos);
    void OnMouseMove(Vector2 pos);
    void OnMouseUp(Vector2 pos);
    void Close();
}
