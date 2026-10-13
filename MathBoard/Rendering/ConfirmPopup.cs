using System.Numerics;
using MathBoard.Core;

namespace MathBoard.Rendering;

/// <summary>Asks before a destructive action; tapping outside cancels.</summary>
public sealed class ConfirmPopup(StrokeRenderer renderer) : IPopup
{
    private const float Padding = 20f;
    private static readonly Vector2 PopupSize = new(440f, 164f);
    private static readonly Vector2 ButtonSize = new(130f, 40f);

    private Action? _confirm;
    private string _title = "", _text = "", _confirmLabel = "";
    private Vector2 _mouse;

    public bool IsOpen => _confirm != null;

    public void Open(string title, string text, string confirmLabel, Action confirm)
    {
        (_title, _text, _confirmLabel, _confirm) = (title, text, confirmLabel, confirm);
        renderer.SetDirty();
    }

    public void Close()
    {
        _confirm = null;
        renderer.SetDirty();
    }

    private Vector2 Pos => Vector2.Round((renderer.ScreenSize - PopupSize) * 0.5f);
    private Vector2 ConfirmPos => Pos + PopupSize - new Vector2(Padding) - ButtonSize;
    private Vector2 CancelPos => ConfirmPos - new Vector2(ButtonSize.X + 10f, 0);

    public void OnMouseDown(Vector2 pos) => _mouse = pos;

    public void OnMouseMove(Vector2 pos)
    {
        _mouse = pos;
        renderer.SetDirty();
    }

    public void OnMouseUp(Vector2 pos)
    {
        var confirm = _confirm;
        Close();
        if (UiBatch.Contains(ConfirmPos, ButtonSize, pos)) confirm?.Invoke();
    }

    public void Render(UiBatch ui, Vector2 screenSize)
    {
        if (!IsOpen) return;
        var pos = Pos;
        ui.Rect(Vector2.Zero, screenSize, new Vector4(0, 0, 0, 0.35f));
        ui.Rect(pos, PopupSize, Theme.PopupBg, Theme.PopupRadius);
        ui.Border(pos, PopupSize, Theme.Border, 1f, Theme.PopupRadius);

        var line = new Vector2(PopupSize.X - Padding * 2, 24f);
        ui.TextInRect(_title, pos + new Vector2(Padding, Padding), line, Theme.Text, Theme.Title);
        ui.TextInRect(_text, pos + new Vector2(Padding, Padding + 38f), line, Theme.TextSecondary, Theme.Body);
        ui.TextButton(Localization.Get("dialog_button_cancel", "Cancel"), CancelPos, ButtonSize,
            UiBatch.Contains(CancelPos, ButtonSize, _mouse), UiBatch.ButtonKind.Secondary);
        ui.TextButton(_confirmLabel, ConfirmPos, ButtonSize, UiBatch.Contains(ConfirmPos, ButtonSize, _mouse), UiBatch.ButtonKind.Danger);
    }
}
