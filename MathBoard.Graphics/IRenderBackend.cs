using Silk.NET.Windowing;

namespace MathBoard.Graphics;

/// <summary>
/// A render backend plugin. The app loads every <c>backends/*/MathBoard.Backend.*.dll</c>, instantiates the public
/// implementations of this interface and uses the first one that initializes successfully, so a new backend
/// only needs to be dropped into that folder.
/// </summary>
public interface IRenderBackend : IDisposable
{
    /// <summary>Shown in the settings and stored as the user's choice.</summary>
    string Name { get; }

    /// <summary>Backends with higher priority are tried first unless the user picked one.</summary>
    int Priority { get; }

    /// <summary>Adjusts window options (graphics API, context) before the window is created.</summary>
    WindowOptions ConfigureWindow(WindowOptions options);

    /// <summary>Called once the window exists. Throws if the backend cannot run on this machine.</summary>
    void Initialize(IWindow window);

    /// <summary>
    /// Draws and presents a frame. Returns false if nothing was presented (for example while the window is
    /// being resized); the app then keeps its dirty ranges and submits them again.
    /// </summary>
    bool Render(in FrameData frame);
}
