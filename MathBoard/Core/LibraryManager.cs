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

    public void MoveFile(string sourcePath, string destDir)
    {
        if (!File.Exists(sourcePath)) return;
        if (!Directory.Exists(destDir)) return;
        try
        {
            string fileName = Path.GetFileName(sourcePath);
            string destPath = Path.Combine(destDir, fileName);
            if (string.Equals(sourcePath, destPath, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(destPath))
            {
                Console.WriteLine($"Move failed: destination exists: {destPath}");
                return;
            }

            File.Move(sourcePath, destPath);
            Console.WriteLine($"Moved: {sourcePath} -> {destPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Move failed: {ex.Message}");
        }
    }

    public void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            Directory.Delete(path, recursive: true);
            Console.WriteLine($"Directory deleted: {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"DeleteDirectory failed: {ex.Message}");
        }
    }

    public void RenameDirectory(string oldPath, string newName)
    {
        if (!Directory.Exists(oldPath)) return;
        try
        {
            string parentDir = Path.GetDirectoryName(oldPath) ?? Settings.LibraryRootPath.Value;
            string newPath = Path.Combine(parentDir, newName);
            if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return;
            if (Directory.Exists(newPath))
            {
                Console.WriteLine($"RenameDirectory failed: destination already exists: {newPath}");
                return;
            }

            Directory.Move(oldPath, newPath);
            Console.WriteLine($"Directory renamed: {oldPath} -> {newPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"RenameDirectory failed: {ex.Message}");
        }
    }

    public void MoveDirectory(string sourcePath, string destDir)
    {
        if (!Directory.Exists(sourcePath)) return;
        if (!Directory.Exists(destDir)) return;
        try
        {
            string dirName = Path.GetFileName(sourcePath);
            string destPath = Path.Combine(destDir, dirName);
            if (string.Equals(sourcePath, destPath, StringComparison.OrdinalIgnoreCase)) return;
            if (Directory.Exists(destPath))
            {
                Console.WriteLine($"MoveDirectory failed: destination exists: {destPath}");
                return;
            }

            if (destPath.StartsWith(sourcePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"MoveDirectory failed: cannot move into own child");
                return;
            }

            Directory.Move(sourcePath, destPath);
            Console.WriteLine($"Directory moved: {sourcePath} -> {destPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MoveDirectory failed: {ex.Message}");
        }
    }

    public void CreateFolder(string parentDir, string folderName)
    {
        try
        {
            string path = Path.Combine(parentDir, folderName);
            Directory.CreateDirectory(path);
            Console.WriteLine($"Folder created: {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CreateFolder failed: {ex.Message}");
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