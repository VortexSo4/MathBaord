namespace MathBoard.Core;

/// <summary>
/// Where MathBoard keeps its settings, log, autosave and default library: %AppData%\MathBoard, or the folder in the
/// MATHBOARD_DATA environment variable, so the performance tests in .agent/perf run without touching the user's data.
/// </summary>
public static class AppPaths
{
    public static readonly string Data = Environment.GetEnvironmentVariable("MATHBOARD_DATA") is { Length: > 0 } dir
        ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MathBoard");

    public static string Log => Path.Combine(Data, "latest.log");
}
