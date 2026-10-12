using System.Numerics;
using System.Runtime.InteropServices;

namespace MathBoard.Core;

/// <summary>
/// Two-finger pan and pinch zoom from WM_POINTER touch messages. GLFW does not report touches, so the window
/// procedure is subclassed; every message is still passed on, which keeps single-finger mouse emulation working.
/// </summary>
public sealed partial class TouchGestures : IDisposable
{
    private const uint WM_POINTERUPDATE = 0x0245, WM_POINTERDOWN = 0x0246, WM_POINTERUP = 0x0247;
    private const uint PT_TOUCH = 2;

    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    private readonly nint _hwnd;
    private readonly WndProc _proc;
    private readonly nint _original;
    private readonly SortedDictionary<uint, Vector2> _touches = new();
    private Vector2 _lastCenter;
    private float _lastDistance;
    private bool _resync = true;

    public bool IsActive { get; private set; }

    /// <summary>A second finger touched down: whatever the first finger started should be cancelled.</summary>
    public event Action? Started;

    /// <summary>Center in window pixels, center movement since the last update, and distance ratio (zoom).</summary>
    public event Action<Vector2, Vector2, float>? Changed;

    /// <summary>All fingers lifted.</summary>
    public event Action? Ended;

    public TouchGestures(nint hwnd)
    {
        _hwnd = hwnd;
        _proc = Proc;
        _original = SetWindowLongPtr(hwnd, -4, Marshal.GetFunctionPointerForDelegate(_proc));
    }

    private nint Proc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg is WM_POINTERDOWN or WM_POINTERUPDATE or WM_POINTERUP)
        {
            uint id = (uint)(wParam & 0xFFFF);
            if (GetPointerType(id, out uint type) && type == PT_TOUCH)
            {
                var point = new Point { X = (short)(lParam & 0xFFFF), Y = (short)((lParam >> 16) & 0xFFFF) };
                ScreenToClient(hwnd, ref point);
                if (msg == WM_POINTERUP) _touches.Remove(id);
                else _touches[id] = new Vector2(point.X, point.Y);
                Track();
            }
        }

        return CallWindowProc(_original, hwnd, msg, wParam, lParam);
    }

    private void Track()
    {
        if (_touches.Count < 2)
        {
            // Going from two fingers to one would make the center jump, so the next pair starts afresh.
            _resync = true;
            if (_touches.Count == 0 && IsActive)
            {
                IsActive = false;
                Ended?.Invoke();
            }

            return;
        }

        using var e = _touches.Values.GetEnumerator();
        e.MoveNext();
        var a = e.Current;
        e.MoveNext();
        var b = e.Current;
        var center = (a + b) * 0.5f;
        float distance = MathF.Max(Vector2.Distance(a, b), 1f);

        if (!IsActive)
        {
            IsActive = true;
            Started?.Invoke();
        }
        else if (!_resync)
        {
            Changed?.Invoke(center, center - _lastCenter, distance / _lastDistance);
        }

        _resync = false;
        _lastCenter = center;
        _lastDistance = distance;
    }

    public void Dispose() => SetWindowLongPtr(_hwnd, -4, _original);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static partial nint CallWindowProc(nint previous, nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPointerType(uint pointerId, out uint type);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ScreenToClient(nint hwnd, ref Point point);
}
