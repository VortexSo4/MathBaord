using System.Numerics;
using MathBoard.Rendering;

namespace MathBoard.Core;

public class LibraryManager
{
    private readonly Document _document;
    private readonly StrokeRenderer _renderer;
    private DateTime _lastAutoSave = DateTime.MinValue;

    public LibraryManager(Document document, StrokeRenderer renderer)
    {
        _document = document;
        _renderer = renderer;
        Settings.Load();
        EnsureLibraryRoot();
    }

    private void EnsureLibraryRoot()
    {
        if (!Directory.Exists(Settings.LibraryRootPath))
            Directory.CreateDirectory(Settings.LibraryRootPath);
    }

    public string SaveCanvas(string? customName = null)
    {
        EnsureLibraryRoot();
        string name = customName ?? $"Untitled_{DateTime.Now:yyyy-MM-dd_HH-mm}";
        string path = Path.Combine(Settings.LibraryRootPath, $"{name}.mathboard");

        int counter = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(Settings.LibraryRootPath, $"{name}_{counter}.mathboard");
            counter++;
        }

        _document.SaveToFile(path);
        Console.WriteLine($"Saved: {path}");
        return path;
    }

    public void AutoSaveIfNeeded()
    {
        if ((DateTime.Now - _lastAutoSave).TotalMinutes < Settings.AutoSaveIntervalMinutes)
            return;

        try
        {
            SaveCanvas("AutoSave");
            _lastAutoSave = DateTime.Now;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AutoSave failed: {ex.Message}");
        }
    }

    public void LoadLastSave()
    {
        string lastSave = "last_save.mathboard";
        if (File.Exists(lastSave))
        {
            _document.LoadFromFile(lastSave);
            _renderer.SetDirty();
            _renderer.UpdateGeometry();
        }
    }
}