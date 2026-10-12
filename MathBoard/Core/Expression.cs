using System.Globalization;

namespace MathBoard.Core;

/// <summary>
/// Parses school-style formulas of x into a function: "y = 2x^2 - 3", "sin(x)", "|x|", "√x", "tg x", "x²".
/// Implicit multiplication is allowed ("2x", "3(x+1)", "2sin x").
/// </summary>
public sealed class Expression
{
    private static readonly Dictionary<string, Func<double, double>> Functions = new()
    {
        ["sin"] = Math.Sin, ["cos"] = Math.Cos, ["tan"] = Math.Tan, ["tg"] = Math.Tan,
        ["cot"] = x => 1 / Math.Tan(x), ["ctg"] = x => 1 / Math.Tan(x),
        ["asin"] = Math.Asin, ["arcsin"] = Math.Asin, ["acos"] = Math.Acos, ["arccos"] = Math.Acos,
        ["atan"] = Math.Atan, ["arctg"] = Math.Atan, ["arctan"] = Math.Atan,
        ["sqrt"] = Math.Sqrt, ["√"] = Math.Sqrt, ["abs"] = Math.Abs, ["ln"] = Math.Log, ["log"] = Math.Log10,
        ["lg"] = Math.Log10, ["exp"] = Math.Exp, ["floor"] = Math.Floor, ["ceil"] = Math.Ceiling,
        ["round"] = Math.Round, ["sign"] = x => Math.Sign(x)
    };

    private static readonly Dictionary<string, double> Constants = new() { ["pi"] = Math.PI, ["π"] = Math.PI, ["e"] = Math.E };

    private readonly List<string> _tokens;
    private int _pos;
    private int _absDepth;

    public Func<double, double> Evaluate { get; }

    private Expression(string text)
    {
        _tokens = Tokenize(text);
        Evaluate = ParseSum();
        if (_pos < _tokens.Count) throw new FormatException(Localization.Get("expr_unexpected", "unexpected") + $" '{_tokens[_pos]}'");
    }

    /// <summary>Parses one formula. Throws <see cref="FormatException"/> with a short reason.</summary>
    public static Expression Parse(string text) => new(text);

    /// <summary>Parses formulas separated by ';'; returns the error of the first one that fails.</summary>
    public static (List<Func<double, double>> Functions, string? Error) ParseList(string text)
    {
        var result = new List<Func<double, double>>();
        foreach (string part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                result.Add(Parse(part).Evaluate);
            }
            catch (FormatException ex)
            {
                return (result, $"{part}: {ex.Message}");
            }
        }

        return (result, null);
    }

    private static List<string> Tokenize(string text)
    {
        text = text.Trim().ToLowerInvariant().Replace('²', '\u0001').Replace('³', '\u0002').Replace("−", "-")
            .Replace('×', '*').Replace('·', '*').Replace('÷', '/').Replace(',', '.');
        // "y = ..." and "f(x) = ..." prefixes are allowed.
        int eq = text.IndexOf('=');
        if (eq >= 0) text = text[(eq + 1)..];

        var tokens = new List<string>();
        for (int i = 0; i < text.Length;)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (char.IsDigit(c) || c == '.')
            {
                int start = i;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                tokens.Add(text[start..i]);
                continue;
            }

            if (char.IsLetter(c) && c != 'π')
            {
                int start = i;
                while (i < text.Length && char.IsLetter(text[i]) && text[i] != 'π') i++;
                SplitWord(text[start..i], tokens);
                continue;
            }

            tokens.Add(c switch { '\u0001' => "^2", '\u0002' => "^3", _ => c.ToString() });
            i++;
        }

        // Expand "^2" shorthands produced from superscripts.
        return tokens.SelectMany(t => t.Length == 2 && t[0] == '^' ? new[] { "^", t[1..] } : new[] { t }).ToList();
    }

    /// <summary>Splits letter runs like "xsin" or "pix" into known names and single-letter variables.</summary>
    private static void SplitWord(string word, List<string> tokens)
    {
        int i = 0;
        while (i < word.Length)
        {
            string? match = Functions.Keys.Concat(Constants.Keys).Where(k => word.AsSpan(i).StartsWith(k))
                .OrderByDescending(k => k.Length).FirstOrDefault();
            if (match != null)
            {
                tokens.Add(match);
                i += match.Length;
            }
            else
            {
                tokens.Add(word[i].ToString());
                i++;
            }
        }
    }

    private string? Peek => _pos < _tokens.Count ? _tokens[_pos] : null;

    private bool Accept(string token)
    {
        if (Peek != token) return false;
        _pos++;
        return true;
    }

    private Func<double, double> ParseSum()
    {
        var left = ParseProduct();
        while (true)
        {
            if (Accept("+")) { var l = left; var r = ParseProduct(); left = x => l(x) + r(x); }
            else if (Accept("-")) { var l = left; var r = ParseProduct(); left = x => l(x) - r(x); }
            else return left;
        }
    }

    private bool StartsPrimary(string? token) =>
        token != null && (char.IsDigit(token[0]) || token[0] == '.' || token == "(" || token == "x" ||
                          Functions.ContainsKey(token) || Constants.ContainsKey(token) || (token == "|" && _absDepth == 0));

    private Func<double, double> ParseProduct()
    {
        var left = ParseUnary();
        while (true)
        {
            if (Accept("*")) { var l = left; var r = ParseUnary(); left = x => l(x) * r(x); }
            else if (Accept("/")) { var l = left; var r = ParseUnary(); left = x => l(x) / r(x); }
            else if (StartsPrimary(Peek)) { var l = left; var r = ParsePower(); left = x => l(x) * r(x); }
            else return left;
        }
    }

    private Func<double, double> ParseUnary()
    {
        if (Accept("-")) { var inner = ParseUnary(); return x => -inner(x); }
        if (Accept("+")) return ParseUnary();
        return ParsePower();
    }

    private Func<double, double> ParsePower()
    {
        var b = ParsePrimary();
        if (!Accept("^")) return b;
        var e = ParseUnary();
        return x => Math.Pow(b(x), e(x));
    }

    private Func<double, double> ParsePrimary()
    {
        string token = Peek ?? throw new FormatException(Localization.Get("expr_end", "the formula ends too early"));
        _pos++;

        if (char.IsDigit(token[0]) || token[0] == '.')
        {
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                throw new FormatException($"'{token}' " + Localization.Get("expr_number", "is not a number"));
            return _ => value;
        }

        if (token == "x") return x => x;
        if (Constants.TryGetValue(token, out double constant)) return _ => constant;

        if (Functions.TryGetValue(token, out var function))
        {
            // Both "sin(x)" and "sin x" are accepted; without brackets the argument is a single power term.
            var argument = Peek == "(" ? ParsePrimary() : ParsePower();
            return x => function(argument(x));
        }

        if (token == "(")
        {
            var inner = ParseSum();
            if (!Accept(")")) throw new FormatException(Localization.Get("expr_bracket", "a ')' is missing"));
            return inner;
        }

        if (token == "|")
        {
            _absDepth++;
            var inner = ParseSum();
            _absDepth--;
            if (!Accept("|")) throw new FormatException(Localization.Get("expr_abs", "a closing '|' is missing"));
            return x => Math.Abs(inner(x));
        }

        throw new FormatException(Localization.Get("expr_unknown", "unknown") + $" '{token}'");
    }
}
