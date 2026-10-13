using System.Runtime.InteropServices;
using SkiaSharp;

namespace MathBoard.Core;

/// <summary>A capture of all monitors; <see cref="Monitors"/> are in its pixels, <see cref="Current"/> is the board's one.</summary>
public sealed record ScreenCapture(SKBitmap Bitmap, SKRectI[] Monitors, int Current) : IDisposable
{
    public void Dispose() => Bitmap.Dispose();
}

/// <summary>Where pictures come from — the screen, the clipboard, the open file dialog — and the export save dialog (Windows only).</summary>
public static unsafe partial class ImageSources
{
    private static readonly List<SKRectI> FoundMonitors = new();

    /// <summary>
    /// Hides the window, copies all monitors and shows the window again, so the capture shows what is behind the board.
    /// </summary>
    public static ScreenCapture? CaptureScreen(nint hwnd)
    {
        int x = GetSystemMetrics(76), y = GetSystemMetrics(77), w = GetSystemMetrics(78), h = GetSystemMetrics(79);
        if (w <= 0 || h <= 0) return null;

        FoundMonitors.Clear();
        EnumDisplayMonitors(0, null, &OnMonitor, 0);
        var monitors = FoundMonitors.Select(m => new SKRectI(m.Left - x, m.Top - y, m.Right - x, m.Bottom - y)).ToArray();
        if (monitors.Length == 0) monitors = [new SKRectI(0, 0, w, h)];
        SKRectI window;
        GetWindowRect(hwnd, &window);
        var center = new SKPointI((window.Left + window.Right) / 2 - x, (window.Top + window.Bottom) / 2 - y);
        int current = Math.Max(0, Array.FindIndex(monitors, m => m.Contains(center.X, center.Y)));

        ShowWindow(hwnd, 0);
        try
        {
            // The windows below need a moment to be repainted.
            Thread.Sleep(300);
            nint screen = GetDC(0);
            nint memory = CreateCompatibleDC(screen);
            nint target = CreateCompatibleBitmap(screen, w, h);
            nint previous = SelectObject(memory, target);
            bool copied = BitBlt(memory, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000); // SRCCOPY | CAPTUREBLT
            SelectObject(memory, previous);

            var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            var header = new BitmapInfoHeader { Size = (uint)sizeof(BitmapInfoHeader), Width = w, Height = -h, Planes = 1, BitCount = 32 };
            if (copied) copied = GetDIBits(memory, target, 0, (uint)h, bitmap.GetPixels(), &header, 0) == h;
            DeleteObject(target);
            DeleteDC(memory);
            ReleaseDC(0, screen);

            if (!copied)
            {
                bitmap.Dispose();
                return null;
            }

            MakeOpaque(bitmap);
            return new ScreenCapture(bitmap, monitors, current);
        }
        finally
        {
            ShowWindow(hwnd, 5);
            SetForegroundWindow(hwnd);
        }
    }

    [UnmanagedCallersOnly]
    private static int OnMonitor(nint monitor, nint dc, SKRectI* rect, nint data)
    {
        FoundMonitors.Add(*rect);
        return 1;
    }

    /// <summary>A picture from the clipboard: a copied image, or the first picture among copied files.</summary>
    public static SKBitmap? ReadClipboard()
    {
        if (!OpenClipboard(0)) return null;
        try
        {
            if (ReadClipboardData(RegisterClipboardFormatW("PNG")) is { } png && SKBitmap.Decode(png) is { } fromPng)
                return fromPng;
            if (ReadClipboardData(8) is { } dib && DecodeDib(dib) is { } fromDib) // CF_DIB
                return fromDib;

            nint drop = GetClipboardData(15); // CF_HDROP
            if (drop == 0) return null;
            uint count = DragQueryFileW(drop, uint.MaxValue, null, 0);
            for (uint i = 0; i < count; i++)
            {
                uint length = DragQueryFileW(drop, i, null, 0);
                var buffer = new char[length + 1];
                fixed (char* p = buffer) DragQueryFileW(drop, i, p, length + 1);
                if (ImageStore.Load(new string(buffer, 0, (int)length)) is { } file) return file;
            }

            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static byte[]? ReadClipboardData(uint format)
    {
        nint handle = GetClipboardData(format);
        if (handle == 0) return null;
        nint data = GlobalLock(handle);
        if (data == 0) return null;
        try
        {
            return new Span<byte>((void*)data, (int)GlobalSize(handle)).ToArray();
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    /// <summary>Turns a clipboard DIB into a BMP file and decodes it.</summary>
    private static SKBitmap? DecodeDib(byte[] dib)
    {
        if (dib.Length < 40) return null;
        int headerSize = BitConverter.ToInt32(dib, 0);
        int bitCount = BitConverter.ToUInt16(dib, 14);
        int compression = BitConverter.ToInt32(dib, 16);
        int colorsUsed = BitConverter.ToInt32(dib, 32);
        int masks = compression == 3 && headerSize == 40 ? 12 : 0; // BI_BITFIELDS masks follow a short header
        int palette = (colorsUsed != 0 ? colorsUsed : bitCount <= 8 ? 1 << bitCount : 0) * 4;

        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.TryWriteBytes(file.AsSpan(2), file.Length);
        BitConverter.TryWriteBytes(file.AsSpan(10), 14 + headerSize + masks + palette);
        dib.CopyTo(file, 14);

        using var codec = SKCodec.Create(new SKMemoryStream(file));
        if (codec == null) return null;
        // Unpremultiplied, so that colors survive when a 32-bit DIB leaves its alpha at zero.
        var info = codec.Info.WithColorType(SKColorType.Bgra8888).WithAlphaType(SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        if (codec.GetPixels(info, bitmap.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            return null;
        }

        if (bitCount == 32 && AllTransparent(bitmap)) MakeOpaque(bitmap);
        return bitmap;
    }

    private static bool AllTransparent(SKBitmap bitmap)
    {
        var pixels = new Span<byte>((void*)bitmap.GetPixels(), bitmap.ByteCount);
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) return false;
        return true;
    }

    private static void MakeOpaque(SKBitmap bitmap)
    {
        var pixels = new Span<byte>((void*)bitmap.GetPixels(), bitmap.ByteCount);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        bitmap.NotifyPixelsChanged();
    }

    /// <summary>Shows the system "Open" dialog for pictures; returns the chosen file or null.</summary>
    public static string? PickFile(nint owner, string title) =>
        FileDialog(owner, title, "Images\0*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp\0\0", "", save: false);

    /// <summary>Shows the system "Save as" dialog for a PNG or PDF; returns the chosen file or null.</summary>
    public static string? PickExportFile(nint owner, string title, string name) =>
        FileDialog(owner, title, "PNG\0*.png\0PDF\0*.pdf\0\0", name, save: true);

    private static string? FileDialog(nint owner, string title, string filter, string name, bool save)
    {
        var buffer = new char[1024];
        name.CopyTo(0, buffer, 0, Math.Min(name.Length, buffer.Length - 1));
        fixed (char* file = buffer)
        fixed (char* filterPtr = filter)
        fixed (char* titlePtr = title)
        fixed (char* extension = "png")
        {
            var dialog = new OpenFileName
            {
                StructSize = (uint)sizeof(OpenFileName),
                Owner = owner,
                Filter = filterPtr,
                File = file,
                MaxFile = (uint)buffer.Length,
                Title = titlePtr,
                DefaultExtension = extension,
                // EXPLORER | PATHMUSTEXIST, and OVERWRITEPROMPT or FILEMUSTEXIST
                Flags = 0x00080000 | 0x00000800 | (save ? 0x00000002u : 0x00001000u)
            };
            if (!(save ? GetSaveFileNameW(&dialog) : GetOpenFileNameW(&dialog))) return null;
        }

        return new string(buffer, 0, Array.IndexOf(buffer, '\0'));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenFileName
    {
        public uint StructSize;
        public nint Owner, Instance;
        public char* Filter, CustomFilter;
        public uint MaxCustomFilter, FilterIndex;
        public char* File;
        public uint MaxFile;
        public char* FileTitle;
        public uint MaxFileTitle;
        public char* InitialDir, Title;
        public uint Flags;
        public ushort FileOffset, FileExtension;
        public char* DefaultExtension;
        public nint CustomData, Hook, TemplateName, Reserved;
        public uint Reserved2, FlagsEx;
    }

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(nint dc, SKRectI* clip, delegate* unmanaged<nint, nint, SKRectI*, nint, int> callback, nint data);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, SKRectI* rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hwnd, nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleBitmap(nint dc, int width, int height);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint dc, int x, int y, int w, int h, nint src, int srcX, int srcY, uint rop);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(nint dc, nint bitmap, uint start, uint lines, nint bits, BitmapInfoHeader* info, uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint owner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    private static partial nint GetClipboardData(uint format);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string name);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalLock(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial nuint GlobalSize(nint handle);

    [LibraryImport("shell32.dll")]
    private static partial uint DragQueryFileW(nint drop, uint index, char* file, uint length);

    [LibraryImport("comdlg32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetOpenFileNameW(OpenFileName* dialog);

    [LibraryImport("comdlg32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSaveFileNameW(OpenFileName* dialog);
}
