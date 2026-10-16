using Silk.NET.GLFW;

namespace MathBoard.Core;

/// <summary>The main loop sleeps until something happens; work finished on another thread wakes it to show the result.</summary>
public static class AppLoop
{
    public static void Wake() => GlfwProvider.GLFW.Value.PostEmptyEvent();
}
