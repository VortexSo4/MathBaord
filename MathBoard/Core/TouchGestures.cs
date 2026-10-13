using System.Numerics;
using System.Runtime.InteropServices;

namespace MathBoard.Core;

/// <summary>
/// Touch input from WM_POINTER messages, which GLFW does not report: one finger works like the left mouse button,
/// two fingers pan and pinch-zoom. Touches are not left to Windows' mouse emulation, because it holds back the
/// button-down of a finger that does not move until the finger lifts, so a long press (the radial menu) never
/// arrived. Windows' press-and-hold gesture is turned off as well: it turned a long press into a right click
/// (the eraser here).
/// </summary>
public sealed partial class TouchGestures : IDisposable
{
    private const uint WM_POINTERUPDATE = 0x0245, WM_POINTERDOWN = 0x0246, WM_POINTERUP = 0x0247;
    private const uint WM_TABLET_QUERYSYSTEMGESTURESTATUS = 0x02CC;
    private const uint PT_TOUCH = 2;
    // TABLET_DISABLE_PRESSANDHOLD | PENTAPFEEDBACK | PENBARRELFEEDBACK | FLICKS
    private const int DisabledTabletGestures = 0x1 | 0x8 | 0x10 | 0x10000;
    private const string TabletProperty = "MicrosoftTabletPenServiceProperty";
    private const int LoggedContacts = 20;

    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    private readonly nint _hwnd;
    private readonly WndProc _proc;
    private readonly nint _original;
    private readonly SortedDictionary<uint, Vector2> _touches = new();
    private Vector2 _lastCenter;
    private float _lastDistance;
    private bool _resync = true;
    private uint? _primary;
    private bool _primaryCancelled;
    private int _loggedContacts;

    public bool IsActive { get; private set; }

    /// <summary>The first finger touched down: position in window pixels and contact size in millimetres (0 if unknown).</summary>
    public event Action<Vector2, float>? Down;

    /// <summary>The first finger moved; not raised while two fingers pan or zoom.</summary>
    public event Action<Vector2, float>? Move;

    /// <summary>The first finger lifted.</summary>
    public event Action<Vector2>? Up;

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
        ushort atom = GlobalAddAtomW(TabletProperty);
        SetPropW(hwnd, TabletProperty, DisabledTabletGestures);
        GlobalDeleteAtom(atom);
    }

    private nint Proc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_TABLET_QUERYSYSTEMGESTURESTATUS) return DisabledTabletGestures;
        if (msg is WM_POINTERDOWN or WM_POINTERUPDATE or WM_POINTERUP)
        {
            uint id = (uint)(wParam & 0xFFFF);
            if (GetPointerType(id, out uint type) && type == PT_TOUCH)
            {
                var point = new Point { X = (short)(lParam & 0xFFFF), Y = (short)((lParam >> 16) & 0xFFFF) };
                ScreenToClient(hwnd, ref point);
                var pos = new Vector2(point.X, point.Y);
                HandleTouch(msg, id, pos);
                // Handled here, so Windows generates no mouse messages for this touch.
                return 0;
            }
        }

        return CallWindowProc(_original, hwnd, msg, wParam, lParam);
    }

    private void HandleTouch(uint msg, uint id, Vector2 pos)
    {
        if (msg == WM_POINTERDOWN && _touches.Count == 0)
        {
            _primary = id;
            _primaryCancelled = false;
            Down?.Invoke(pos, ContactMillimetres(id));
        }
        else if (msg == WM_POINTERUPDATE && id == _primary && !_primaryCancelled && !IsActive)
        {
            Move?.Invoke(pos, ContactMillimetres(id));
        }
        else if (msg == WM_POINTERUP && id == _primary)
        {
            _primary = null;
            Up?.Invoke(pos);
        }

        if (msg == WM_POINTERUP) _touches.Remove(id);
        else _touches[id] = pos;
        Track();
    }

    /// <summary>The larger side of the touch's contact area; boards report a palm as a large contact.</summary>
    private float ContactMillimetres(uint id)
    {
        if (!GetPointerTouchInfo(id, out var info)) return 0f;
        int w = info.ContactRight - info.ContactLeft, h = info.ContactBottom - info.ContactTop;
        uint dpi = GetDpiForWindow(_hwnd);
        if (w <= 0 || h <= 0 || dpi == 0) return 0f;

        float mm = Math.Max(w, h) * 25.4f / dpi;
        // Logged for the first touches so the palm threshold can be tuned on a real board.
        if (_loggedContacts < LoggedContacts && (info.PointerFlags & 0x10000) != 0)
        {
            _loggedContacts++;
            Console.WriteLine($"Touch contact: {w}x{h} px, {mm:0} mm");
        }

        return mm;
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
            _primaryCancelled = true;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerTouchInfo
    {
        public uint PointerType, PointerId, FrameId, PointerFlags;
        public nint SourceDevice, HwndTarget;
        public Point PixelLocation, HimetricLocation, PixelLocationRaw, HimetricLocationRaw;
        public uint Time, HistoryCount;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
        // POINTER_INFO ends padded to 8 bytes.
        private int _padding;
        public uint TouchFlags, TouchMask;
        public int ContactLeft, ContactTop, ContactRight, ContactBottom;
        public int RawLeft, RawTop, RawRight, RawBottom;
        public uint Orientation, Pressure;
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial ushort GlobalAddAtomW(string name);

    [LibraryImport("kernel32.dll")]
    private static partial ushort GlobalDeleteAtom(ushort atom);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetPropW(nint hwnd, string name, nint data);

    [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static partial nint CallWindowProc(nint previous, nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPointerType(uint pointerId, out uint type);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPointerTouchInfo(uint pointerId, out PointerTouchInfo info);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ScreenToClient(nint hwnd, ref Point point);
}
