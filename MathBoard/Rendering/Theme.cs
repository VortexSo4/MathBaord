using System.Numerics;

namespace MathBoard.Rendering;

public enum FontFace
{
    Regular,
    Semibold,
    Icons
}

public readonly record struct TextStyle(FontFace Face, float Size);

public static class Theme
{
    public static readonly TextStyle Body = new(FontFace.Regular, 15f);
    public static readonly TextStyle Small = new(FontFace.Regular, 13f);
    public static readonly TextStyle Title = new(FontFace.Semibold, 17f);
    public static readonly TextStyle Label = new(FontFace.Semibold, 15f);

    public static readonly Vector4 PanelBg = Rgb(0x15, 0x16, 0x1C);
    public static readonly Vector4 HeaderBg = Rgb(0x20, 0x22, 0x2B);
    public static readonly Vector4 PopupBg = Rgb(0x1C, 0x1E, 0x26);
    public static readonly Vector4 Surface = Rgb(0x2A, 0x2D, 0x39);
    public static readonly Vector4 SurfaceHover = Rgb(0x36, 0x3A, 0x48);
    public static readonly Vector4 Field = Rgb(0x12, 0x13, 0x18);
    public static readonly Vector4 Border = Rgb(0x34, 0x37, 0x45);
    public static readonly Vector4 RowHover = new(1f, 1f, 1f, 0.05f);
    public static readonly Vector4 Overlay = new(0f, 0f, 0f, 0.55f);

    public static readonly Vector4 Accent = Rgb(0x4C, 0x8D, 0xFF);
    public static readonly Vector4 AccentHover = Rgb(0x6A, 0xA1, 0xFF);
    public static readonly Vector4 AccentSoft = new(0.30f, 0.55f, 1f, 0.18f);
    public static readonly Vector4 Danger = Rgb(0xE5, 0x48, 0x4D);
    public static readonly Vector4 DangerHover = Rgb(0xF0, 0x62, 0x67);

    public static readonly Vector4 Text = Rgb(0xEC, 0xED, 0xF2);
    public static readonly Vector4 TextSecondary = Rgb(0xA3, 0xA8, 0xB8);
    public static readonly Vector4 TextMuted = Rgb(0x6E, 0x73, 0x84);

    public const float ButtonRadius = 8f;
    public const float PopupRadius = 12f;

    public static Vector4 Rgb(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f, 1f);
    public static Vector4 WithAlpha(Vector4 c, float a) => new(c.X, c.Y, c.Z, a);
}

/// <summary>Segoe Fluent Icons / Segoe MDL2 Assets code points.</summary>
public static class Icons
{
    public const string Menu = "";
    public const string Add = "";
    public const string Close = "";
    public const string Settings = "";
    public const string Delete = "";
    public const string Save = "";
    public const string Edit = "";
    public const string Undo = "";
    public const string Redo = "";
    public const string NewFolder = "";
    public const string Folder = "";
    public const string FolderOpen = "";
    public const string Document = "";
    public const string ChevronRight = "";
    public const string ChevronDown = "";
    public const string ChevronLeft = "";
    public const string ZoomIn = "";
    public const string ZoomOut = "";
    public const string FitView = "";
    public const string Pan = "";
    public const string Gripper = "";
    public const string Pen = "";
    public const string Eraser = "";
    public const string Thickness = "";
    public const string Select = "";
    public const string Palette = "";
    public const string History = "";
    public const string CheckMark = "";
    public const string Customize = "";
    public const string Log = "";
    public const string Keyboard = "";
}
