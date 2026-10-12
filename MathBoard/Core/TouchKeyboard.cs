using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace MathBoard.Core;

/// <summary>The Windows touch keyboard (TabTip), with the classic On-Screen Keyboard as a fallback.</summary>
public static unsafe partial class TouchKeyboard
{
    private static bool _visible;
    private static Process? _osk;

    /// <summary>Asks Windows whether a hardware keyboard is attached (Windows.Devices.Input.KeyboardCapabilities).</summary>
    public static bool IsHardwareKeyboardPresent()
    {
        if (!OperatingSystem.IsWindows()) return true;
        nint className = 0, instance = 0, capabilities = 0;
        try
        {
            RoInitialize(1);
            const string name = "Windows.Devices.Input.KeyboardCapabilities";
            if (WindowsCreateString(name, (uint)name.Length, out className) < 0) return true;
            if (RoActivateInstance(className, out instance) < 0) return true;

            var iid = new Guid("3A3F9B56-6798-4BBC-833E-0F34B17C65FF");
            var vtable = *(nint**)instance;
            var queryInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[0];
            if (queryInterface(instance, &iid, &capabilities) < 0) return true;

            // IInspectable occupies slots 0-5, get_KeyboardPresent is the first method of the interface.
            var getKeyboardPresent = (delegate* unmanaged[Stdcall]<nint, int*, int>)(*(nint**)capabilities)[6];
            int present;
            return getKeyboardPresent(capabilities, &present) < 0 || present != 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Keyboard detection failed: {ex.Message}");
            return true;
        }
        finally
        {
            Release(capabilities);
            Release(instance);
            if (className != 0) WindowsDeleteString(className);
        }
    }

    private static void Release(nint unknown)
    {
        if (unknown == 0) return;
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)unknown)[2];
        release(unknown);
    }

    public static bool IsVisible
    {
        get
        {
            // Windows 10/11 keep this window around and disable it while the keyboard is hidden.
            nint window = FindWindow("IPTip_Main_Window", null);
            if (window == 0) return _visible;
            int style = GetWindowLong(window, -16);
            return (style & 0x10000000) != 0 && (style & 0x08000000) == 0;
        }
    }

    public static void Show()
    {
        if (!IsVisible) Toggle();
    }

    public static void Hide()
    {
        if (IsVisible) Toggle();
    }

    public static void Toggle()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            string tabTip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                "microsoft shared", "ink", "TabTip.exe");
            if (File.Exists(tabTip))
            {
                if (Process.GetProcessesByName("TabTip").Length == 0)
                {
                    // Starting the host shows the keyboard by itself.
                    Process.Start(new ProcessStartInfo(tabTip) { UseShellExecute = true });
                    _visible = true;
                    return;
                }

                var tip = (ITipInvocation)Activator.CreateInstance(
                    Type.GetTypeFromCLSID(new Guid("4CE576FA-83DC-4F88-951C-9D0782B4E376"))!)!;
                tip.Toggle(GetDesktopWindow());
                _visible = !_visible;
                return;
            }

            ToggleOsk();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Touch keyboard failed: {ex.Message}");
            ToggleOsk();
        }
    }

    private static void ToggleOsk()
    {
        try
        {
            if (_osk is { HasExited: false })
            {
                _osk.Kill();
                _osk = null;
                _visible = false;
                return;
            }

            _osk = Process.Start(new ProcessStartInfo("osk.exe") { UseShellExecute = true });
            _visible = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"On-screen keyboard failed: {ex.Message}");
        }
    }

    [ComImport, Guid("37C994E7-432B-4834-A2F7-DCE1F13B834B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITipInvocation
    {
        void Toggle(nint hwnd);
    }

    [LibraryImport("combase.dll")]
    private static partial int RoInitialize(int type);

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string source, uint length, out nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int RoActivateInstance(nint classId, out nint instance);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindow(string className, string? windowName);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial nint GetDesktopWindow();
}
