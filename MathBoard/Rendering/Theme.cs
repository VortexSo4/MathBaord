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
    public const string Menu = "\uE700";
    public const string Add = "\uE710";
    public const string Close = "\uE711";
    public const string Settings = "\uE713";
    public const string Delete = "\uE74D";
    public const string Save = "\uE74E";
    public const string Edit = "\uE70F";
    public const string Undo = "\uE7A7";
    public const string Redo = "\uE7A6";
    public const string NewFolder = "\uE8F4";
    public const string Folder = "\uE8B7";
    public const string FolderOpen = "\uE838";
    public const string Document = "\uE8A5";
    public const string ChevronRight = "\uE76C";
    public const string ChevronDown = "\uE70D";
    public const string ChevronLeft = "\uE76B";
    public const string ZoomIn = "\uE8A3";
    public const string ZoomOut = "\uE71F";
    public const string FitView = "\uE9A6";
    public const string Pan = "\uE7C2";
    public const string Gripper = "\uE76F";
    public const string Pen = "\uEC87";
    public const string Eraser = "\uE75C";
    public const string Thickness = "\uEDA8";
    public const string Select = "\uEF20";
    public const string Palette = "\uE790";
    public const string History = "\uE81C";
    public const string CheckMark = "\uE73E";
    public const string Customize = "\uE771";
    public const string Log = "\uE9F9";
    public const string Keyboard = "\uE765";
    public const string Copy = "\uE8C8";
    public const string Shapes = "\uE71A";
    public const string Grid = "\uE80A";
    public const string Screenshot = "\uF406";
    public const string Picture = "\uEB9F";
    public const string Monitor = "\uE7F4";
}
