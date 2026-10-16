using System.Numerics;
using MathBoard.Collab;
using MathBoard.Core;

namespace MathBoard.Rendering;

/// <summary>
/// The two-code exchange for working together. The host makes an invite code, sends it through a chat and enters the
/// answer code that comes back; the one joining enters the invite and sends back the answer it gets. Codes can be
/// copied and pasted with buttons, as a board often has no keyboard.
/// </summary>
public sealed class CollabDialog(StrokeRenderer renderer, CollabSession session, Func<string?> readClipboard, Action<string> writeClipboard) : IPopup
{
    private enum Page { Start, Invite, Join, Answer }

    private const float Padding = 20f;
    private static readonly Vector2 Size = new(560f, 390f);
    private static readonly Vector2 ButtonSize = new(150f, 38f);

    private readonly TextBoxState _field = new() { Accepts = c => char.IsAsciiLetterOrDigit(c) || c is '-' or ' ' };
    private Page _page;
    private string? _code;
    private string? _error;
    private bool _busy;
    private Vector2 _mouse;

    public bool IsOpen { get; private set; }
    public TextBoxState? ActiveTextBox => IsOpen && _page is Page.Invite or Page.Join ? _field : null;

    private Vector2 Pos => (renderer.ScreenSize - Size) * 0.5f;

    public void Open()
    {
        IsOpen = true;
        _error = null;
        _page = session.CurrentRole switch { CollabSession.Role.Participant => Page.Answer, _ => Page.Start };
        renderer.SetDirty();
    }

    public void Close()
    {
        IsOpen = false;
        renderer.SetDirty();
    }

    private static string L(string key, string fallback) => Localization.Get(key, fallback);

    /// <summary>Enter: the main action of the page.</summary>
    public void Confirm()
    {
        if (_busy) return;
        switch (_page)
        {
            case Page.Invite:
                _error = session.AcceptAnswer(_field.ToString()) switch
                {
                    null => null,
                    "bad_code" => L("collab_bad_code", "This is not an answer code (check for typos)"),
                    _ => L("collab_no_invite", "Make a new invite first")
                };
                if (_error == null)
                {
                    _field.SetText("");
                    _page = Page.Start;
                }

                break;
            case Page.Join:
                Run(async () =>
                {
                    _code = await session.JoinAsync(_field.ToString());
                    if (_code == null) _error = L("collab_bad_invite", "This is not an invite code (check for typos), or there is no network");
                    else _page = Page.Answer;
                });
                break;
        }

        renderer.SetDirty();
    }

    private void Run(Func<Task> work)
    {
        _busy = true;
        _error = null;
        _ = Task.Run(async () =>
        {
            try
            {
                await work();
            }
            finally
            {
                _busy = false;
                renderer.SetDirty();
                AppLoop.Wake();
            }
        });
    }

    private void StartInvite()
    {
        _page = Page.Invite;
        _code = null;
        _field.SetText("");
        Run(async () =>
        {
            _code = await session.CreateInviteAsync();
            if (_code == null) _error = L("collab_no_network", "No network: this computer's address could not be found");
        });
    }

    // ------------------------------------------------------------- layout

    private Vector2 Row(int i) => Pos + new Vector2(Padding, 68f + i * 62f);
    private Vector2 FieldSize => new(Size.X - Padding * 3 - ButtonSize.X, 40f);
    private Vector2 SideButton(int row) => Row(row) + new Vector2(FieldSize.X + Padding, 1f);
    private Vector2 BottomButton(int fromRight) => Pos + Size - new Vector2(Padding + ButtonSize.X * (fromRight + 1) + 10f * fromRight, Padding + ButtonSize.Y);

    private (string Label, Vector2 Pos, Action Click, UiBatch.ButtonKind Kind)[] Buttons()
    {
        var list = new List<(string, Vector2, Action, UiBatch.ButtonKind)>
        {
            (L("collab_close", "Close"), BottomButton(0), Close, UiBatch.ButtonKind.Secondary)
        };

        switch (_page)
        {
            case Page.Start when session.CurrentRole == CollabSession.Role.None:
                list.Add((L("collab_host", "Invite"), Row(1), StartInvite, UiBatch.ButtonKind.Primary));
                list.Add((L("collab_join", "Join by code"), Row(1) + new Vector2(ButtonSize.X + 12f, 0), () =>
                {
                    _page = Page.Join;
                    _field.SetText("");
                }, UiBatch.ButtonKind.Secondary));
                break;
            case Page.Start:
                list.Add((L("collab_invite_more", "Invite one more"), Row(1), StartInvite, UiBatch.ButtonKind.Primary));
                list.Add((L("collab_end", "End"), BottomButton(1), session.Leave, UiBatch.ButtonKind.Danger));
                break;
            case Page.Invite:
                if (_code != null) list.Add((L("collab_copy", "Copy"), Row(1) - new Vector2(0, 10f), () => writeClipboard(_code), UiBatch.ButtonKind.Secondary));
                list.Add((L("collab_paste", "Paste"), SideButton(2), Paste, UiBatch.ButtonKind.Secondary));
                list.Add((L("collab_connect", "Connect"), BottomButton(1), Confirm, UiBatch.ButtonKind.Primary));
                break;
            case Page.Join:
                list.Add((L("collab_paste", "Paste"), SideButton(0), Paste, UiBatch.ButtonKind.Secondary));
                list.Add((L("collab_next", "Next"), BottomButton(1), Confirm, UiBatch.ButtonKind.Primary));
                break;
            case Page.Answer:
                if (_code != null) list.Add((L("collab_copy", "Copy"), Row(1) - new Vector2(0, 10f), () => writeClipboard(_code), UiBatch.ButtonKind.Secondary));
                list.Add((L("collab_end", "End"), BottomButton(1), () =>
                {
                    session.Leave();
                    _page = Page.Start;
                }, UiBatch.ButtonKind.Danger));
                break;
        }

        return list.Select(b => (b.Item1, b.Item2, b.Item3, b.Item4)).ToArray();
    }

    private void Paste()
    {
        if (readClipboard() is { } text) _field.SetText(text.Trim());
    }

    public void OnMouseDown(Vector2 pos)
    {
        _mouse = pos;
        if (!UiBatch.Contains(Pos, Size, pos))
        {
            Close();
            return;
        }

        if (_busy) return;
        foreach (var b in Buttons())
        {
            if (!UiBatch.Contains(b.Pos, ButtonSize, pos)) continue;
            b.Click();
            renderer.SetDirty();
            return;
        }
    }

    public void OnMouseMove(Vector2 pos)
    {
        _mouse = pos;
        renderer.SetDirty();
    }

    public void OnMouseUp(Vector2 pos) { }

    /// <summary>What the session is doing, in words.</summary>
    private string Status()
    {
        if (_busy) return L("collab_working", "Finding this computer's address...");
        int connected = session.ConnectedCount;
        return session.CurrentRole switch
        {
            CollabSession.Role.Host when connected > 0 =>
                string.Format(L("collab_hosting", "Lesson shared, connected: {0}"), connected),
            CollabSession.Role.Host when session.IsConnecting => L("collab_connecting", "Connecting..."),
            CollabSession.Role.Host when session.HasOpenInvite => L("collab_waiting_answer", "Waiting for the answer code..."),
            CollabSession.Role.Participant when connected > 0 => L("collab_joined", "Connected to the host"),
            CollabSession.Role.Participant when session.IsConnecting => L("collab_waiting", "Waiting for the host to enter the answer code..."),
            CollabSession.Role.Participant => L("collab_lost", "Could not connect or the connection was lost"),
            _ => L("collab_intro", "Work on one lesson from several computers, without servers")
        };
    }

    public void Render(UiBatch ui, Vector2 screenSize)
    {
        if (!IsOpen) return;
        var p = Pos;
        ui.Rect(Vector2.Zero, screenSize, Theme.Overlay);
        ui.Rect(p, Size, Theme.PopupBg, Theme.PopupRadius);
        ui.Border(p, Size, Theme.Border, 1f, Theme.PopupRadius);
        var line = new Vector2(Size.X - Padding * 2, 26f);
        ui.TextInRect(L("collab_title", "Working together"), p + new Vector2(Padding, 16f), line, Theme.Text, Theme.Title);

        switch (_page)
        {
            case Page.Start:
                ui.TextInRect(Status(), Row(0), line, Theme.TextSecondary, Theme.Body);
                break;
            case Page.Invite:
                ui.TextInRect(L("collab_send_invite", "1. Send this code to the one joining:"), Row(0) - new Vector2(0, 24f), line, Theme.TextSecondary, Theme.Small);
                CodeBox(ui, 0);
                ui.TextInRect(L("collab_enter_answer", "2. Paste the code they send back:"), Row(2) - new Vector2(0, 24f), line, Theme.TextSecondary, Theme.Small);
                ui.TextField(_field, Row(2), FieldSize, true, Theme.Body, Theme.Text);
                break;
            case Page.Join:
                ui.TextInRect(L("collab_enter_invite", "Paste the host's invite code:"), Row(0) - new Vector2(0, 24f), line, Theme.TextSecondary, Theme.Small);
                ui.TextField(_field, Row(0), FieldSize, true, Theme.Body, Theme.Text);
                break;
            case Page.Answer:
                ui.TextInRect(L("collab_send_answer", "Send this code back to the host:"), Row(0) - new Vector2(0, 24f), line, Theme.TextSecondary, Theme.Small);
                CodeBox(ui, 0);
                break;
        }

        if (_page != Page.Start) ui.TextInRect(Status(), Row(3), line, Theme.TextSecondary, Theme.Body);
        if (_error != null) ui.TextInRect(_error, Row(3) + new Vector2(0, 28f), line, Theme.Danger, Theme.Small);

        foreach (var b in Buttons())
            ui.TextButton(b.Label, b.Pos, ButtonSize, !_busy && UiBatch.Contains(b.Pos, ButtonSize, _mouse), b.Kind);
    }

    /// <summary>A code to send, across the whole popup: about 45 characters.</summary>
    private void CodeBox(UiBatch ui, int row)
    {
        var size = new Vector2(Size.X - Padding * 2, 40f);
        ui.Rect(Row(row), size, Theme.Field, 6f);
        ui.Border(Row(row), size, Theme.Border, 1f, 6f);
        ui.TextInRect(_code ?? "...", Row(row) + new Vector2(10f, 0), size - new Vector2(20f, 0), Theme.Text, Theme.Body);
    }
}
