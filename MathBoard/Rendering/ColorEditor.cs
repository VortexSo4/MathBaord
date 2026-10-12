using System.Numerics;
using MathBoard.Core;

namespace MathBoard.Rendering;

/// <summary>
/// Edits a palette color with vertical hue / saturation / brightness bars. Opened by double-tapping a color button,
/// changes apply live; tapping outside or "Done" closes it.
/// </summary>
public sealed class ColorEditor(StrokeRenderer renderer) : IOverlay
{
    private const float Padding = 16f;
    private const float TitleHeight = 40f;
    private const float BarWidth = 44f;
    private const float BarHeight = 220f;
    private const float BarGap = 22f;
    private const float LabelHeight = 24f;
    private const float FooterHeight = 52f;

    private static readonly Vector2 PopupSize = new(
        Padding * 2 + BarWidth * 3 + BarGap * 2,
        TitleHeight + BarHeight + LabelHeight + FooterHeight + Padding);

    private int _index = -1;
    private float _hue, _saturation, _value;
    private Vector2 _pos;
    private Vector2 _mouse;
    private int _activeBar = -1;

    public bool IsOpen => _index >= 0;

    public void Open(int index, Vector2 anchor)
    {
        _index = index;
        (_hue, _saturation, _value) = ColorMath.RgbToHsv(Settings.Colors[index]);

        // Above the button when there is room, otherwise below; always inside the screen.
        var screen = renderer.ScreenSize;
        var pos = new Vector2(anchor.X - PopupSize.X * 0.5f, anchor.Y - 34f - PopupSize.Y);
        if (pos.Y < 8f) pos.Y = anchor.Y + 34f;
        _pos = Vector2.Clamp(pos, new Vector2(8f), Vector2.Max(screen - PopupSize - new Vector2(8f), new Vector2(8f)));
        renderer.SetDirty();
    }

    public void Close()
    {
        if (!IsOpen) return;
        _index = -1;
        _activeBar = -1;
        Settings.Save();
        renderer.SetDirty();
    }

    private Vector2 BarPos(int bar) => _pos + new Vector2(Padding + bar * (BarWidth + BarGap), TitleHeight);

    private (Vector2 Pos, Vector2 Size) DoneButton() =>
        (_pos + new Vector2(Padding, PopupSize.Y - Padding - 36f), new Vector2(PopupSize.X - Padding * 2, 36f));

    public void OnMouseDown(Vector2 pos)
    {
        _mouse = pos;
        var (donePos, doneSize) = DoneButton();
        if (!UiBatch.Contains(_pos, PopupSize, pos) || UiBatch.Contains(donePos, doneSize, pos))
        {
            Close();
            return;
        }

        for (int bar = 0; bar < 3; bar++)
        {
            // A little wider than the bar so it is easy to hit with a finger.
            if (UiBatch.Contains(BarPos(bar) - new Vector2(BarGap * 0.5f, 8f), new Vector2(BarWidth + BarGap, BarHeight + 16f), pos))
            {
                _activeBar = bar;
                SetFromPointer(pos);
                return;
            }
        }
    }

    public void OnMouseMove(Vector2 pos)
    {
        _mouse = pos;
        if (_activeBar >= 0) SetFromPointer(pos);
        renderer.SetDirty();
    }

    public void OnMouseUp(Vector2 pos) => _activeBar = -1;

    private void SetFromPointer(Vector2 pos)
    {
        float t = Math.Clamp((BarPos(_activeBar).Y + BarHeight - pos.Y) / BarHeight, 0f, 1f);
        switch (_activeBar)
        {
            case 0: _hue = Math.Min(t, 0.9999f); break;
            case 1: _saturation = t; break;
            default: _value = t; break;
        }

        var color = ColorMath.HsvToRgb(_hue, _saturation, _value);
        Settings.Colors[_index] = color;
        renderer.SetColor(color);
        if (renderer.Tool is Tool.Eraser or Tool.Pan) renderer.SetTool(Tool.Brush);
        renderer.SetDirty();
    }

    public void Render(UiBatch ui, Vector2 screenSize)
    {
        if (!IsOpen) return;

        ui.Rect(_pos + new Vector2(0, 4f), PopupSize, new Vector4(0, 0, 0, 0.35f), Theme.PopupRadius);
        ui.Rect(_pos, PopupSize, Theme.PopupBg, Theme.PopupRadius);
        ui.Border(_pos, PopupSize, Theme.Border, 1f, Theme.PopupRadius);

        var color = ColorMath.HsvToRgb(_hue, _saturation, _value);
        ui.Circle(_pos + new Vector2(Padding + 10f, TitleHeight * 0.5f), 10f, color);
        ui.Ring(_pos + new Vector2(Padding + 10f, TitleHeight * 0.5f), 10f, 1f, Theme.Border);
        ui.TextInRect(Localization.Get($"tooltip_color_{_index + 1}", $"Color {_index + 1}"), _pos + new Vector2(Padding + 28f, 0),
            new Vector2(PopupSize.X - Padding * 2 - 28f, TitleHeight), Theme.Text, Theme.Title);

        float[] values = [_hue, _saturation, _value];
        string[] labels =
        [
            Localization.Get("color_hue", "Hue"),
            Localization.Get("color_saturation", "Saturation"),
            Localization.Get("color_brightness", "Brightness")
        ];
        for (int bar = 0; bar < 3; bar++)
        {
            var pos = BarPos(bar);
            ui.ColorBar(pos, new Vector2(BarWidth, BarHeight), bar, _hue, 8f);
            ui.Border(pos, new Vector2(BarWidth, BarHeight), Theme.Border, 1f, 8f);

            float y = pos.Y + (1f - values[bar]) * BarHeight;
            var knobPos = new Vector2(pos.X - 4f, y - 6f);
            var knobSize = new Vector2(BarWidth + 8f, 12f);
            ui.Rect(knobPos, knobSize, Vector4.One, 6f);
            ui.Border(knobPos, knobSize, new Vector4(0.05f, 0.05f, 0.08f, 0.9f), 2f, 6f);

            ui.TextInRect(labels[bar], new Vector2(pos.X - BarGap * 0.5f, pos.Y + BarHeight + 4f),
                new Vector2(BarWidth + BarGap, LabelHeight), Theme.TextSecondary, Theme.Small, UiBatch.Align.Center);
        }

        var (donePos, doneSize) = DoneButton();
        ui.TextButton(Localization.Get("quickbar_done", "Done"), donePos, doneSize, UiBatch.Contains(donePos, doneSize, _mouse),
            UiBatch.ButtonKind.Primary);
    }
}
