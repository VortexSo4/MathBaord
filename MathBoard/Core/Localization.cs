namespace MathBoard.Core;

public static class Localization
{
    private static readonly Dictionary<string, string> _strings = new();

    public static void Load(string language)
    {
        _strings.Clear();
        
        // Ищем папку resources относительно исполняемого файла
        string baseDir = AppContext.BaseDirectory;
        string path = Path.Combine(baseDir, "resources", "languages", $"{language}.lang");
        
        if (!File.Exists(path))
        {
            // Fallback на EN_US, если файл не найден
            path = Path.Combine(baseDir, "resources", "languages", "EN_US.lang");
            if (!File.Exists(path)) return;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
            var parts = line.Split('=', 2);
            if (parts.Length == 2)
                _strings[parts[0].Trim()] = parts[1].Trim();
        }
    }

    public static string Get(string key)
    {
        return _strings.TryGetValue(key, out var val) ? val : key;
    }
}