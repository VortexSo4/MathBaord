using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MathBoard.Core;

/// <summary>Hardware and OS summary written to the log at startup, so bug reports from school PCs are useful.</summary>
public static partial class SystemInfo
{
    public static string AppVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";

    public static void Log()
    {
        Console.WriteLine($"MathBoard {AppVersion}, {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"OS: {RuntimeInformation.OSDescription} ({(Environment.Is64BitOperatingSystem ? "64" : "32")}-bit)");
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            string name = (cpu?.GetValue("ProcessorNameString") as string)?.Trim() ?? "unknown";
            object? mhz = cpu?.GetValue("~MHz");
            Console.WriteLine($"CPU: {name}, {Environment.ProcessorCount} logical processors{(mhz != null ? $", {mhz} MHz" : "")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CPU: unavailable ({ex.Message})");
        }

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref memory))
            Console.WriteLine($"RAM: {memory.TotalPhys / 1048576} MB total, {memory.AvailPhys / 1048576} MB free");

        var adapters = new List<string>();
        var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref device, 0); i++)
        {
            if (!string.IsNullOrWhiteSpace(device.DeviceString) && !adapters.Contains(device.DeviceString))
                adapters.Add(device.DeviceString);
            device.Size = Marshal.SizeOf<DisplayDevice>();
        }

        Console.WriteLine($"GPU: {(adapters.Count > 0 ? string.Join("; ", adapters) : "unknown")}");
        Console.WriteLine($"Screen: {GetSystemMetrics(0)}x{GetSystemMetrics(1)} (primary), {GetSystemMetrics(80)} monitor(s)");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice info, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);
}
