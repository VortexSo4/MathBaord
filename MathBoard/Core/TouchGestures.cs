using System.Numerics;
using System.Runtime.InteropServices;

namespace MathBoard.Core;

/// <summary>
/// Touch and pen input from WM_POINTER messages, which GLFW does not report: one finger or a pen in contact works like
/// the left mouse button, two fingers pan and pinch-zoom. Contacts are not left to Windows' mouse emulation, because
/// it holds back the button-down of a finger or pen that does not move until it lifts, so a long press (the radial
/// menu) never arrived. A hovering pen still goes through it, which moves the cursor. Windows' press-and-hold gesture
/// is turned off as well: it turned a long press into a right click (the eraser here).
/// </summary>
public sealed partial class TouchGestures : IDisposable
{
    private const uint WM_POINTERUPDATE = 0x0245, WM_POINTERDOWN = 0x0246, WM_POINTERUP = 0x0247;
    private const uint WM_TABLET_QUERYSYSTEMGESTURESTATUS = 0x02CC;
    private const uint PT_TOUCH = 2, PT_PEN = 3;
    // TABLET_DISABLE_PRESSANDHOLD | PENTAPFEEDBACK | PENBARRELFEEDBACK | FLICKS
    private const int DisabledTabletGestures = 0x1 | 0x8 | 0x10 | 0x10000;
    private const string TabletProperty = "MicrosoftTabletPenServiceProperty";
    private const int LoggedContacts = 20;
    // PEN_FLAG_INVERTED | PEN_FLAG_ERASER: a pen held the other way round, or touching with its eraser end.
    private const uint PenEraserFlags = 0x2 | 0x4;

    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    private readonly nint _hwnd;
    private readonly WndProc _proc;
    private readonly nint _original;
    private readonly SortedDictionary<uint, Vector2> _touches = new();
    private Vector2 _lastCenter;
    private float _lastDistance;
    private bool _resync = true;
    private uint? _primary;
    private uint? _pen;
    private bool _primaryCancelled;
    private int _loggedContacts;

    public bool IsActive { get; private set; }

    /// <summary>
    /// The first finger or a pen touched down: position in window pixels, contact size in millimetres (0 if unknown) and
    /// whether it is a pen's eraser end.
    /// </summary>
    public event Action<Vector2, float, bool>? Down;

    /// <summary>The first finger or the pen moved; not raised while two fingers pan or zoom.</summary>
    public event Action<Vector2, float>? Move;

    /// <summary>The first finger or the pen lifted.</summary>
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
            GetPointerType(id, out uint type);
            // A pen only counts while it touches the board: from its down until its up.
            bool pen = type == PT_PEN && (msg == WM_POINTERDOWN || id == _pen);
            if (type == PT_TOUCH || pen)
            {
                var point = new Point { X = (short)(lParam & 0xFFFF), Y = (short)((lParam >> 16) & 0xFFFF) };
                ScreenToClient(hwnd, ref point);
                var pos = new Vector2(point.X, point.Y);
                if (pen) HandlePen(msg, id, pos);
                else HandleTouch(msg, id, pos);
                // Handled here, so Windows generates no mouse messages for this contact.
                return 0;
            }
        }

        return CallWindowProc(_original, hwnd, msg, wParam, lParam);
    }

    private void HandlePen(uint msg, uint id, Vector2 pos)
    {
        switch (msg)
        {
            case WM_POINTERDOWN:
                _pen = id;
                bool eraser = GetPointerPenInfo(id, out var info) && (info.PenFlags & PenEraserFlags) != 0;
                Log($"Pen down: flags 0x{info.PenFlags:X}, pressure {info.Pressure}{(eraser ? ", eraser end" : "")}");
                Down?.Invoke(pos, 0f, eraser);
                break;
            case WM_POINTERUPDATE:
                Move?.Invoke(pos, 0f);
                break;
            default:
                _pen = null;
                Up?.Invoke(pos);
                break;
        }
    }

    private void HandleTouch(uint msg, uint id, Vector2 pos)
    {
        if (msg == WM_POINTERDOWN && _touches.Count == 0)
        {
            _primary = id;
            _primaryCancelled = false;
            Down?.Invoke(pos, ContactMillimetres(id, log: true), false);
        }
        else if (msg == WM_POINTERUPDATE && id == _primary && !_primaryCancelled && !IsActive)
        {
            Move?.Invoke(pos, ContactMillimetres(id, log: false));
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

    /// <summary>
    /// The larger side of the touch's contact area: boards report a palm as a large contact, and some tell the thin and
    /// the thick end of a stylus apart this way.
    /// </summary>
    private float ContactMillimetres(uint id, bool log)
    {
        if (!GetPointerTouchInfo(id, out var info))
        {
            if (log) Log("Touch down: no touch info");
            return 0f;
        }

        int w = info.ContactRight - info.ContactLeft, h = info.ContactBottom - info.ContactTop;
        uint dpi = GetDpiForWindow(_hwnd);
        float mm = w > 0 && h > 0 && dpi > 0 ? Math.Max(w, h) * 25.4f / dpi : 0f;
        if (log) Log(mm > 0 ? $"Touch down: contact {w}x{h} px, {mm:0.0} mm" : $"Touch down: contact size not reported ({w}x{h})");
        return mm;
    }

    /// <summary>The first touches are written to the log, so the palm and stylus thresholds can be tuned on a real board.</summary>
    private void Log(string line)
    {
        if (_loggedContacts >= LoggedContacts) return;
        _loggedContacts++;
        Console.WriteLine(line);
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
    private struct PointerInfo
    {
        public uint PointerType, PointerId, FrameId, PointerFlags;
        public nint SourceDevice, HwndTarget;
        public Point PixelLocation, HimetricLocation, PixelLocationRaw, HimetricLocationRaw;
        public uint Time, HistoryCount;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerPenInfo
    {
        public PointerInfo Pointer;
        public uint PenFlags, PenMask, Pressure, Rotation;
        public int TiltX, TiltY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerTouchInfo
    {
        public PointerInfo Pointer;
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPointerPenInfo(uint pointerId, out PointerPenInfo info);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ScreenToClient(nint hwnd, ref Point point);
}
