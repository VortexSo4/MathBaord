﻿using System.Numerics;
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

        _document.SaveToFile(path);
        Console.WriteLine($"Saved: {path}");
        return path;
    }

    public void DeleteFile(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            File.Delete(path);
            Console.WriteLine($"Deleted: {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Delete failed: {ex.Message}");
        }
    }

    public void RenameFile(string oldPath, string newName)
    {
        if (!File.Exists(oldPath)) return;
        try
        {
            string dir = Path.GetDirectoryName(oldPath) ?? Settings.LibraryRootPath.Value;
            string newPath = Path.Combine(dir, $"{newName}.mathboard");
            if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(newPath))
            {
                Console.WriteLine($"Rename failed: destination already exists: {newPath}");
                return;
            }
            File.Move(oldPath, newPath);
            Console.WriteLine($"Renamed: {oldPath} -> {newPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Rename failed: {ex.Message}");
        }
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
        }
    }
    
    public void LoadFile(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            _document.LoadFromFile(path);
            _renderer.SetDirty();
            Console.WriteLine($"Loaded: {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Load failed: {ex.Message}");
        }
    }
}