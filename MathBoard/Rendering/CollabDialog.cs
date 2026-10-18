using System.Numerics;
using MathBoard.Collab;
using MathBoard.Core;
using QRCoder;

namespace MathBoard.Rendering;

/// <summary>
/// The two-code exchange for working together. The host makes an invite code, sends it through a chat and enters the
/// answer code that comes back; the one joining enters the invite and sends back the answer it gets. Codes can be
/// copied and pasted with buttons, as a board often has no keyboard, and are shown as a QR code too, for people in the
/// same room: any phone camera turns it back into the text.
/// </summary>
public sealed class CollabDialog(StrokeRenderer renderer, CollabSession session, Func<string?> readClipboard, Action<string> writeClipboard) : IPopup
{
    private enum Page { Start, Invite, Join, Answer }

    private const float Padding = 20f, Column = 560f, QrSide = 230f;
    private static readonly Vector2 ButtonSize = new(150f, 38f);
    // Dark modules on a light card whatever the theme: not every phone camera reads a light code on a dark ground.
    private static readonly Vector4 QrLight = Theme.Rgb(0xF4, 0xF5, 0xF8), QrDark = Theme.Rgb(0x1C, 0x1E, 0x26);

    private readonly TextBoxState _field = new() { Accepts = c => char.IsAsciiLetterOrDigit(c) || c is '-' or ' ' };
    private Page _page;
    private string? _code;
    private QRCodeData? _qr;
    private string? _qrFor;
    private string? _error;
    private bool _busy;
    private Vector2 _mouse;

    public bool IsOpen { get; private set; }
    public TextBoxState? ActiveTextBox => IsOpen && _page is Page.Invite or Page.Join ? _field : null;

    // A page that shows a code has the QR code to the right of it.
    private Vector2 Size => _page is Page.Invite or Page.Answer ? new Vector2(Column + QrSide + Padding, 390f) : new Vector2(Column, 390f);
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
    private Vector2 FieldSize => new(Column - Padding * 3 - ButtonSize.X, 40f);
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
        var line = new Vector2(Column - Padding * 2, 26f);
        ui.TextInRect(L("collab_title", "Working together"), p + new Vector2(Padding, 16f), line, Theme.Text, Theme.Title);

        switch (_page)
        {
            case Page.Start:
                ui.TextInRect(Status(), Row(0), line, Theme.TextSecondary, Theme.Body);
                break;
            case Page.Invite:
                ui.TextInRect(L("collab_send_invite", "1. Send this code to the one joining:"), Row(0) - new Vector2(0, 24f), line, Theme.TextSecondary, Theme.Small);
                CodeBox(ui, 0);
                QrCode(ui);
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
                QrCode(ui);
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
        var size = new Vector2(Column - Padding * 2, 40f);
        ui.Rect(Row(row), size, Theme.Field, 6f);
        ui.Border(Row(row), size, Theme.Border, 1f, 6f);
        ui.TextInRect(_code ?? "...", Row(row) + new Vector2(10f, 0), size - new Vector2(20f, 0), Theme.Text, Theme.Body);
    }

    /// <summary>
    /// The code as a QR code, right of it: the same text, which QR stores compactly (upper-case letters, digits and
    /// dashes). Drawn "liquid": modules sit on whole screen pixels, so neighbours join without seams and straight edges
    /// stay sharp; a corner sticking out is rounded (a lone module is a dot), and a corner where three dark modules meet
    /// around a light one gets a rounded fill. The three corner squares are rounded frames.
    /// </summary>
    private void QrCode(UiBatch ui)
    {
        var pos = new Vector2(Pos.X + Column, Row(0).Y - 24f);
        ui.Rect(pos, new Vector2(QrSide), QrLight, 14f);
        if (_code == null) return;
        if (_qrFor != _code)
        {
            using var generator = new QRCodeGenerator();
            _qr = generator.CreateQrCode(_code, QRCodeGenerator.ECCLevel.Q);
            _qrFor = _code;
        }

        var matrix = _qr!.ModuleMatrix;
        int n = matrix.Count, quiet = (n - (17 + 4 * _qr.Version)) / 2, side = n - quiet * 2;
        // An even number of screen pixels per module, so its quarters end on pixels too.
        float scale = renderer.UiScale;
        float m = MathF.Floor(QrSide * scale / n / 2f) * 2f / scale, h = m / 2f;
        var origin = (pos + new Vector2((QrSide - m * n) * 0.5f)) * scale;
        origin = new Vector2(MathF.Round(origin.X), MathF.Round(origin.Y)) / scale;
        (int X, int Y)[] finders = [(quiet, quiet), (quiet + side - 7, quiet), (quiet, quiet + side - 7)];
        bool InFinder(int x, int y) => finders.Any(f => x >= f.X && x < f.X + 7 && y >= f.Y && y < f.Y + 7);
        bool Dark(int x, int y) => x >= 0 && y >= 0 && x < n && y < n && matrix[y][x] && !InFinder(x, y);

        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            var cell = origin + new Vector2(x, y) * m;
            bool dark = Dark(x, y);
            foreach (var (dx, dy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
            {
                var quarter = cell + new Vector2(dx > 0 ? h : 0, dy > 0 ? h : 0);
                bool side1 = Dark(x + dx, y), side2 = Dark(x, y + dy);
                if (dark)
                {
                    // A corner with no dark neighbour on either side is rounded off; otherwise the quarter stays square.
                    if (side1 || side2) ui.Rect(quarter, new Vector2(h), QrDark);
                    else
                    {
                        ui.PushClip(quarter, new Vector2(h));
                        ui.Rect(cell, new Vector2(m), QrDark, h);
                        ui.PopClip();
                    }
                }
                else if (side1 && side2 && Dark(x + dx, y + dy))
                {
                    // Three dark modules around this light corner: a rounded fill between them.
                    ui.PushClip(quarter, new Vector2(h));
                    ui.Rect(quarter, new Vector2(h), QrDark);
                    ui.Circle(cell + new Vector2(h), h, QrLight);
                    ui.PopClip();
                }
            }
        }

        foreach (var (fx, fy) in finders)
        {
            var corner = origin + new Vector2(fx, fy) * m;
            ui.Border(corner, new Vector2(7 * m), QrDark, m, m * 2f);
            ui.Rect(corner + new Vector2(2 * m), new Vector2(3 * m), QrDark, m);
        }
    }
}
