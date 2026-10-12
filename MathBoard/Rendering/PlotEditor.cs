using System.Globalization;
using System.Numerics;
using MathBoard.Core;

namespace MathBoard.Rendering;

/// <summary>Popup for a plot's functions and x range. Opens right after a plot is drawn and from its selection toolbar.</summary>
public sealed class PlotEditor(StrokeRenderer renderer) : IPopup
{
    private const float Padding = 20f;
    private const float FieldHeight = 40f;

    private static readonly Vector2 Size = new(460f, 300f);
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private readonly TextBoxState[] _fields =
    [
        new(),
        new() { Accepts = c => char.IsDigit(c) || c is '.' or ',' or '-' },
        new() { Accepts = c => char.IsDigit(c) || c is '.' or ',' or '-' }
    ];

    private BoardObject? _plot;
    private Action<BoardObject>? _apply;
    private int _active;
    private string? _error;
    private Vector2 _mouse;

    public bool IsOpen => _plot != null;
    public TextBoxState? ActiveTextBox => IsOpen ? _fields[_active] : null;

    private Vector2 Pos => (renderer.ScreenSize - Size) * 0.5f;

    public void Open(BoardObject plot, Action<BoardObject> apply)
    {
        _plot = plot;
        _apply = apply;
        _fields[0].SetText(plot.Functions, selectAll: true);
        _fields[1].SetText(plot.XMin.ToString("0.##", Invariant));
        _fields[2].SetText(plot.XMax.ToString("0.##", Invariant));
        _active = 0;
        _error = null;
        renderer.SetDirty();
    }

    public void Close()
    {
        _plot = null;
        _apply = null;
        renderer.SetDirty();
    }

    /// <summary>Applies the fields if they parse; otherwise shows what is wrong and stays open.</summary>
    public void Confirm()
    {
        if (_plot == null) return;
        string functions = _fields[0].ToString().Trim();
        var (_, error) = Expression.ParseList(functions);
        bool rangeOk = TryParse(_fields[1], out float xMin) & TryParse(_fields[2], out float xMax);
        if (error != null)
        {
            _error = Localization.Get("plot_error", "Cannot read") + " " + error;
            _active = 0;
        }
        else if (!rangeOk || xMax <= xMin)
        {
            _error = Localization.Get("plot_range_error", "The range must be two numbers, \"from\" smaller than \"to\"");
            _active = 1;
        }
        else
        {
            _apply?.Invoke(_plot with { Functions = functions, XMin = xMin, XMax = xMax });
            Close();
            return;
        }

        renderer.SetDirty();
    }

    private static bool TryParse(TextBoxState field, out float value) =>
        float.TryParse(field.ToString().Replace(',', '.'), NumberStyles.Float, Invariant, out value);

    public void NextField()
    {
        _active = (_active + 1) % _fields.Length;
        _fields[_active].SetText(_fields[_active].ToString(), selectAll: true);
        renderer.SetDirty();
    }

    private (Vector2 Pos, Vector2 Size) FieldRect(int index)
    {
        var p = Pos;
        if (index == 0) return (p + new Vector2(Padding, 82f), new Vector2(Size.X - Padding * 2, FieldHeight));
        float w = (Size.X - Padding * 2 - 16f) * 0.5f;
        return (p + new Vector2(Padding + (index - 1) * (w + 16f), 168f), new Vector2(w, FieldHeight));
    }

    private (Vector2 Cancel, Vector2 Ok, Vector2 Size) Buttons()
    {
        var btn = new Vector2(120f, 38f);
        float y = Pos.Y + Size.Y - 20f - btn.Y;
        return (new Vector2(Pos.X + Size.X - Padding - btn.X * 2 - 10f, y), new Vector2(Pos.X + Size.X - Padding - btn.X, y), btn);
    }

    public void OnMouseDown(Vector2 pos)
    {
        _mouse = pos;
        var (cancel, ok, size) = Buttons();
        if (UiBatch.Contains(ok, size, pos)) { Confirm(); return; }
        if (UiBatch.Contains(cancel, size, pos)) { Close(); return; }

        for (int i = 0; i < _fields.Length; i++)
        {
            var (fieldPos, fieldSize) = FieldRect(i);
            if (UiBatch.Contains(fieldPos, fieldSize, pos))
            {
                _active = i;
                renderer.SetDirty();
            }
        }
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
        var p = Pos;
        ui.Rect(Vector2.Zero, screenSize, Theme.Overlay);
        ui.Rect(p, Size, Theme.PopupBg, Theme.PopupRadius);
        ui.Border(p, Size, Theme.Border, 1f, Theme.PopupRadius);
        ui.TextInRect(Localization.Get("plot_title", "Graph"), p + new Vector2(Padding, 14f), new Vector2(Size.X - Padding * 2, 26f),
            Theme.Text, Theme.Title);

        var fieldLabel = new Vector2(Size.X - Padding * 2, 20f);
        ui.TextInRect(Localization.Get("plot_functions", "Functions of x, separated by ;"), p + new Vector2(Padding, 58f),
            fieldLabel, Theme.TextSecondary, Theme.Small);
        float half = (Size.X - Padding * 2 - 16f) * 0.5f;
        ui.TextInRect(Localization.Get("plot_from", "x from"), p + new Vector2(Padding, 144f), fieldLabel with { X = half },
            Theme.TextSecondary, Theme.Small);
        ui.TextInRect(Localization.Get("plot_to", "x to"), p + new Vector2(Padding + half + 16f, 144f), fieldLabel with { X = half },
            Theme.TextSecondary, Theme.Small);

        for (int i = 0; i < _fields.Length; i++)
        {
            var (fieldPos, fieldSize) = FieldRect(i);
            ui.TextField(_fields[i], fieldPos, fieldSize, i == _active, Theme.Body, Theme.Text);
        }

        string hint = _error ?? Localization.Get("plot_hint", "For example: x^2 - 3; 2sin(x); |x|; √x");
        ui.TextInRect(hint, p + new Vector2(Padding, 124f), fieldLabel, _error != null ? Theme.Danger : Theme.TextMuted, Theme.Small);

        var (cancel, ok, size) = Buttons();
        ui.TextButton(Localization.Get("dialog_button_cancel", "Cancel"), cancel, size, UiBatch.Contains(cancel, size, _mouse), UiBatch.ButtonKind.Secondary);
        ui.TextButton(Localization.Get("quickbar_done", "Done"), ok, size, UiBatch.Contains(ok, size, _mouse), UiBatch.ButtonKind.Primary);
    }
}
