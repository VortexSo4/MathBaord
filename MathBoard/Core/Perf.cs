namespace MathBoard.Core;

/// <summary>
/// Timing lines for the performance tests in .agent/perf, written to the log only when MATHBOARD_PERF=1. Times are
/// Environment.TickCount64 milliseconds (GetTickCount64), the clock the test scripts mark their steps with.
/// </summary>
public static class Perf
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("MATHBOARD_PERF") == "1";

    /// <summary>One turn of the main loop: handling input, then building and presenting a frame if one was drawn.</summary>
    public static void Loop(double inputMs, double frameMs, bool drew) =>
        Write($"PERF t={Environment.TickCount64} ev={inputMs:F3} fr={frameMs:F3} drew={(drew ? 1 : 0)}");

    /// <summary>A drawn frame: preparing the primitives, then the backend drawing and presenting them.</summary>
    public static void Frame(double prepareMs, double backendMs) =>
        Write($"PERF2 t={Environment.TickCount64} prep={prepareMs:F3} gpu={backendMs:F3}");

    /// <summary>A step of the start, to see where the time before the window shows goes.</summary>
    public static void Stage(string name) => Write($"PERFS {Environment.TickCount64} {name}");

    public static void Time(string what, double ms) => Write($"PERF {what} {ms:F1} ms");

    private static void Write(FormattableString line)
    {
        if (Enabled) Console.WriteLine(FormattableString.Invariant(line));
    }
}
