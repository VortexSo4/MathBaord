using System.Globalization;
using System.Numerics;
using MathBoard.Core;

namespace MathBoard.Rendering;

/// <summary>
/// Popup for typing one measure of a shape (see <see cref="Dimension"/>), opened by tapping its label: a field and keys
/// for the pen, as a board has no keyboard; a real keyboard types into the field too. Lengths are in grid cells.
/// </summary>
public sealed class DimensionEditor(StrokeRenderer renderer) : IPopup
{
    private const float Padding = 14f;
    private const float Key = 56f;
    private const float Gap = 6f;
    private const float FieldHeight = 40f;
    private const string Backspace = "back";
    private static readonly string[] Keys = ["7", "8", "9", "4", "5", "6", "1", "2", "3", ",", "0", Backspace];
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private readonly TextBoxState _field = new() { Accepts = c => char.IsDigit(c) || c is '.' or ',' };
    private Dimension _dimension;
    private Action<float>? _apply;
    private Vector2 _pos;
    private Vector2 _mouse;

    public bool IsOpen => _apply != null;
    public TextBoxState? ActiveTextBox => IsOpen ? _field : null;

    private static Vector2 Size => new(Padding * 2 + Key * 3 + Gap * 2, Padding + 26f + FieldHeight + Gap * 2 + (Key * 0.8f + Gap) * 4 + 40f + Padding);

    /// <summary>The value shown and typed: grid cells for a length, degrees for an angle.</summary>
    public static float Shown(Dimension d) => d.Kind == DimensionKind.Length ? d.Value / ObjectGeometry.GridUnit : d.Value;

    public static string Format(Dimension d) => d.Kind == DimensionKind.Length
        ? MathF.Round(Shown(d), 1).ToString("0.#", Invariant)
        : MathF.Round(d.Value).ToString("0", Invariant) + "°";

    /// <param name="apply">Gets the typed value in world units or degrees.</param>
    public void Open(Dimension dimension, Vector2 anchor, Action<float> apply)
    {
        _dimension = dimension;
        _apply = apply;
        _field.SetText(Format(dimension).TrimEnd('°'), selectAll: true);
        var screen = renderer.ScreenSize;
        _pos = Vector2.Clamp(anchor + new Vector2(24f, -Size.Y * 0.5f), new Vector2(8f), Vector2.Max(screen - Size - new Vector2(8f), new Vector2(8f)));
        renderer.SetDirty();
    }

    public void Close()
    {
        _apply = null;
        renderer.SetDirty();
    }

    /// <summary>Applies the typed number; anything else leaves the popup open.</summary>
    public void Confirm()
    {
        if (!float.TryParse(_field.ToString().Replace(',', '.'), NumberStyles.Float, Invariant, out float value) || value <= 0f) return;
        var apply = _apply;
        Close();
        apply?.Invoke(_dimension.Kind == DimensionKind.Length ? value * ObjectGeometry.GridUnit : value);
    }

    private Vector2 KeyPos(int i)
    {
        float top = _pos.Y + Padding + 26f + FieldHeight + Gap * 2;
        return new Vector2(_pos.X + Padding + i % 3 * (Key + Gap), top + i / 3 * (Key * 0.8f + Gap));
    }

    private static Vector2 KeySize => new(Key, Key * 0.8f);

    private (Vector2 Cancel, Vector2 Ok, Vector2 Size) Buttons()
    {
        var size = new Vector2((Size.X - Padding * 2 - Gap) * 0.5f, 36f);
        float y = _pos.Y + Size.Y - Padding - size.Y;
        return (new Vector2(_pos.X + Padding, y), new Vector2(_pos.X + Padding + size.X + Gap, y), size);
    }

    public void OnMouseDown(Vector2 pos)
    {
        _mouse = pos;
        if (!UiBatch.Contains(_pos, Size, pos))
        {
            Close();
            return;
        }

        var (cancel, ok, size) = Buttons();
        if (UiBatch.Contains(ok, size, pos)) Confirm();
        else if (UiBatch.Contains(cancel, size, pos)) Close();
        for (int i = 0; i < Keys.Length && IsOpen; i++)
        {
            if (!UiBatch.Contains(KeyPos(i), KeySize, pos)) continue;
            if (Keys[i] == Backspace) _field.HandleKey(Silk.NET.Input.Key.Backspace, false, false, null!);
            else _field.Insert(Keys[i]);
        }

        renderer.SetDirty();
    }

    public void OnMouseMove(Vector2 pos)
    {
        _mouse = pos;
        renderer.SetDirty();
    }

    public void OnMouseUp(Vector2 pos) { }

    public void Render(UiBatch ui, Vector2 screenSize)
    {
        if (!IsOpen) return;
        ui.Rect(_pos + new Vector2(0, 4f), Size, new Vector4(0, 0, 0, 0.35f), Theme.PopupRadius);
        ui.Rect(_pos, Size, Theme.PopupBg, Theme.PopupRadius);
        ui.Border(_pos, Size, Theme.Border, 1f, Theme.PopupRadius);

        string title = _dimension.Kind == DimensionKind.Angle
            ? Localization.Get("dimension_angle", "Angle, degrees")
            : Localization.Get("dimension_length", "Length, grid cells");
        ui.TextInRect(title, _pos + new Vector2(Padding, Padding), new Vector2(Size.X - Padding * 2, 22f), Theme.Text, Theme.Label);
        ui.TextField(_field, _pos + new Vector2(Padding, Padding + 26f), new Vector2(Size.X - Padding * 2, FieldHeight), true, Theme.Body, Theme.Text);

        for (int i = 0; i < Keys.Length; i++)
        {
            var pos = KeyPos(i);
            ui.ButtonBackground(pos, KeySize, UiBatch.Contains(pos, KeySize, _mouse), UiBatch.ButtonKind.Secondary);
            if (Keys[i] == Backspace) ui.Icon(Icons.Backspace, pos + KeySize * 0.5f, 18f, Theme.Text);
            else ui.TextInRect(Keys[i], pos, KeySize, Theme.Text, Theme.Title, UiBatch.Align.Center);
        }

        var (cancel, ok, size) = Buttons();
        ui.TextButton(Localization.Get("dialog_button_cancel", "Cancel"), cancel, size, UiBatch.Contains(cancel, size, _mouse), UiBatch.ButtonKind.Secondary);
        ui.TextButton(Localization.Get("quickbar_done", "Done"), ok, size, UiBatch.Contains(ok, size, _mouse), UiBatch.ButtonKind.Primary);
    }
}
