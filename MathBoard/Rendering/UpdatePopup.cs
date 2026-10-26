using System.Numerics;
using MathBoard.Core;

namespace MathBoard.Rendering;

/// <summary>
/// Offers a newly found version. "Update" downloads and installs it, after which the installer starts the app again;
/// "Later" hides the offer until the next start.
/// </summary>
public sealed class UpdatePopup(StrokeRenderer renderer, Updater updater) : IPopup
{
    private const float Padding = 20f;
    private static readonly Vector2 PopupSize = new(460f, 200f);
    private static readonly Vector2 ButtonSize = new(130f, 40f);

    private bool _dismissed;
    private bool _started;
    private Vector2 _mouse;

    public bool IsOpen => !_dismissed && (updater.Status == Updater.State.Available ||
                                          _started && updater.Status is Updater.State.Downloading or Updater.State.Installing or Updater.State.Failed);

    private bool Busy => updater.Status is Updater.State.Downloading or Updater.State.Installing;

    private Vector2 Pos => Vector2.Round((renderer.ScreenSize - PopupSize) * 0.5f);

    private Vector2 PrimaryPos => Pos + PopupSize - new Vector2(Padding) - ButtonSize;
    private Vector2 SecondaryPos => PrimaryPos - new Vector2(ButtonSize.X + 10f, 0);

    public void Close()
    {
        if (Busy) return;
        _dismissed = true;
        renderer.SetDirty();
    }

    public void OnMouseDown(Vector2 pos) => _mouse = pos;

    public void OnMouseMove(Vector2 pos)
    {
        _mouse = pos;
        renderer.SetDirty();
    }

    public void OnMouseUp(Vector2 pos)
    {
        if (Busy) return;
        if (UiBatch.Contains(PrimaryPos, ButtonSize, pos))
        {
            _started = true;
            updater.Install();
        }
        else if (UiBatch.Contains(SecondaryPos, ButtonSize, pos))
        {
            Close();
        }

        renderer.SetDirty();
    }

    public void Render(UiBatch ui, Vector2 screenSize)
    {
        if (!IsOpen) return;
        var pos = Pos;
        ui.Rect(Vector2.Zero, screenSize, new Vector4(0, 0, 0, 0.35f));
        ui.Rect(pos + new Vector2(0, 4f), PopupSize, new Vector4(0, 0, 0, 0.35f), Theme.PopupRadius);
        ui.Rect(pos, PopupSize, Theme.PopupBg, Theme.PopupRadius);
        ui.Border(pos, PopupSize, Theme.Border, 1f, Theme.PopupRadius);

        string version = updater.AvailableVersion?.ToString(3) ?? "";
        var lineSize = new Vector2(PopupSize.X - Padding * 2, 24f);
        ui.TextInRect(Localization.Get("update_title", "A new version is available") + " - " + version,
            pos + new Vector2(Padding, Padding), lineSize, Theme.Text, Theme.Title);

        string status = updater.Status switch
        {
            Updater.State.Downloading => Localization.Get("update_downloading", "Downloading") + $" {updater.Progress:P0}",
            Updater.State.Installing => Localization.Get("update_installing", "Installing, MathBoard will open again..."),
            Updater.State.Failed => Localization.Get("update_error", "Could not download the update"),
            _ => Localization.Get("update_current", "Installed version") + " " + Updater.CurrentVersion.ToString(3) + "."
        };
        ui.TextInRect(status, pos + new Vector2(Padding, Padding + 40f), lineSize,
            updater.Status == Updater.State.Failed ? Theme.Danger : Theme.TextSecondary, Theme.Body);

        if (Busy)
        {
            var track = pos + new Vector2(Padding, Padding + 84f);
            var trackSize = new Vector2(PopupSize.X - Padding * 2, 8f);
            float done = updater.Status == Updater.State.Installing ? 1f : (float)updater.Progress;
            ui.Rect(track, trackSize, Theme.Surface, 4f);
            ui.Rect(track, new Vector2(MathF.Max(trackSize.X * done, 8f), trackSize.Y), Theme.Accent, 4f);
            return;
        }

        ui.TextInRect(Localization.Get("update_note", "The board is saved and MathBoard restarts by itself."),
            pos + new Vector2(Padding, Padding + 66f), lineSize, Theme.TextSecondary, Theme.Body);

        bool failed = updater.Status == Updater.State.Failed;
        ui.TextButton(Localization.Get(failed ? "update_retry" : "update_now", failed ? "Retry" : "Update"), PrimaryPos, ButtonSize,
            UiBatch.Contains(PrimaryPos, ButtonSize, _mouse), UiBatch.ButtonKind.Primary);
        ui.TextButton(Localization.Get("update_later", "Later"), SecondaryPos, ButtonSize,
            UiBatch.Contains(SecondaryPos, ButtonSize, _mouse), UiBatch.ButtonKind.Secondary);
    }
}
