using System.Text;
using Silk.NET.Input;

namespace MathBoard.Rendering;

/// <summary>
/// Single-line text editing shared by every text field. Implements the standard Windows edit-control
/// shortcuts (word navigation, selection, clipboard) in one place so dialogs never handle keys themselves.
/// </summary>
public class TextBoxState
{
    public StringBuilder Text = new();
    public int CursorPos = 0;
    public int AnchorPos = 0;
    public DateTime LastEditTime = DateTime.Now;

    /// <summary>Characters accepted on input and paste; everything else is dropped.</summary>
    public Func<char, bool> Accepts { get; set; } = c => !char.IsControl(c);

    public int SelStart => Math.Min(CursorPos, AnchorPos);
    public int SelEnd => Math.Max(CursorPos, AnchorPos);
    public bool HasSelection => CursorPos != AnchorPos;

    public void SetText(string text, bool selectAll = false)
    {
        Text.Clear();
        Text.Append(text);
        CursorPos = Text.Length;
        AnchorPos = selectAll ? 0 : CursorPos;
        LastEditTime = DateTime.Now;
    }

    public void Insert(char c) => Insert(c.ToString());

    public void Insert(string s)
    {
        var filtered = new StringBuilder(s.Length);
        foreach (char c in s)
            if (Accepts(c)) filtered.Append(c);
        if (filtered.Length == 0) return;

        DeleteSelection();
        Text.Insert(CursorPos, filtered);
        CursorPos += filtered.Length;
        AnchorPos = CursorPos;
        Touch();
    }

    /// <summary>Handles an editing key. Returns true if the key was consumed.</summary>
    public bool HandleKey(Key key, bool ctrl, bool shift, IKeyboard keyboard)
    {
        switch (key)
        {
            case Key.Left:
                MoveTo(ctrl ? WordLeft(CursorPos) : !shift && HasSelection ? SelStart : CursorPos - 1, shift);
                return true;
            case Key.Right:
                MoveTo(ctrl ? WordRight(CursorPos) : !shift && HasSelection ? SelEnd : CursorPos + 1, shift);
                return true;
            case Key.Home:
                MoveTo(0, shift);
                return true;
            case Key.End:
                MoveTo(Text.Length, shift);
                return true;
            case Key.Backspace:
                if (!DeleteSelection() && CursorPos > 0)
                    RemoveRange(ctrl ? WordLeft(CursorPos) : CursorPos - 1, CursorPos);
                Touch();
                return true;
            case Key.Delete:
                if (!DeleteSelection() && CursorPos < Text.Length)
                    RemoveRange(CursorPos, ctrl ? WordRight(CursorPos) : CursorPos + 1);
                Touch();
                return true;
        }

        if (!ctrl) return false;

        switch (key)
        {
            case Key.A:
                AnchorPos = 0;
                CursorPos = Text.Length;
                Touch();
                return true;
            case Key.C when HasSelection:
                keyboard.ClipboardText = SelectedText;
                return true;
            case Key.X when HasSelection:
                keyboard.ClipboardText = SelectedText;
                DeleteSelection();
                Touch();
                return true;
            case Key.V:
                Insert(keyboard.ClipboardText ?? "");
                return true;
        }

        return false;
    }

    private string SelectedText => Text.ToString(SelStart, SelEnd - SelStart);

    private bool DeleteSelection()
    {
        if (!HasSelection) return false;
        RemoveRange(SelStart, SelEnd);
        return true;
    }

    private void RemoveRange(int start, int end)
    {
        Text.Remove(start, end - start);
        CursorPos = AnchorPos = start;
    }

    private void MoveTo(int pos, bool extendSelection)
    {
        CursorPos = Math.Clamp(pos, 0, Text.Length);
        if (!extendSelection) AnchorPos = CursorPos;
        Touch();
    }

    private void Touch() => LastEditTime = DateTime.Now;

    // Like Windows edit controls: words are runs of letters/digits/underscores; other symbols form their own runs.
    private static int CharClass(char c) => char.IsWhiteSpace(c) ? 0 : char.IsLetterOrDigit(c) || c == '_' ? 1 : 2;

    private int WordLeft(int pos)
    {
        while (pos > 0 && CharClass(Text[pos - 1]) == 0) pos--;
        if (pos == 0) return 0;
        int cls = CharClass(Text[pos - 1]);
        while (pos > 0 && CharClass(Text[pos - 1]) == cls) pos--;
        return pos;
    }

    private int WordRight(int pos)
    {
        int len = Text.Length;
        if (pos < len)
        {
            int cls = CharClass(Text[pos]);
            if (cls != 0)
                while (pos < len && CharClass(Text[pos]) == cls) pos++;
        }

        while (pos < len && CharClass(Text[pos]) == 0) pos++;
        return pos;
    }

    public override string ToString() => Text.ToString();
}
