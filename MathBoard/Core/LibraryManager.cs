using MathBoard.Rendering;

namespace MathBoard.Core;

public class LibraryManager
{
    private const string Extension = ".mathboard";

    private readonly Document _document;
    private readonly StrokeRenderer _renderer;
    private DateTime _lastAutoSave = DateTime.Now;

    /// <summary>File the canvas was loaded from or last saved to; null for a new canvas.</summary>
    public string? CurrentPath { get; private set; }
    public string? CurrentName => CurrentPath == null ? null : Path.GetFileNameWithoutExtension(CurrentPath);

    public bool IsDocumentDirty() => _document.IsDirty;

    private static string AutoSavePath => Path.Combine(Settings.LibraryRootPath, "AutoSave" + Extension);

    public LibraryManager(Document document, StrokeRenderer renderer)
    {
        _document = document;
        _renderer = renderer;
        EnsureLibraryRoot();
    }

    private static void EnsureLibraryRoot()
    {
        if (!Directory.Exists(Settings.LibraryRootPath))
            Directory.CreateDirectory(Settings.LibraryRootPath);
    }

    public void NewFile()
    {
        _document.Clear();
        CurrentPath = null;
        _renderer.OnDocumentReplaced();
    }

    public string SaveCanvas(string? customName = null)
    {
        EnsureLibraryRoot();
        string name = customName ?? $"Untitled_{DateTime.Now:yyyy-MM-dd_HH-mm}";
        string path = Path.Combine(Settings.LibraryRootPath, name + Extension);

        _document.SaveToFile(path);
        _document.IsDirty = false;
        CurrentPath = path;
        _renderer.SetDirty();
        Console.WriteLine($"Saved: {path}");
        return path;
    }

    /// <summary>Saves over the current file. Returns false when the canvas has no file yet.</summary>
    public bool SaveCurrent()
    {
        if (CurrentPath == null) return false;
        try
        {
            _document.SaveToFile(CurrentPath);
            _document.IsDirty = false;
            _renderer.SetDirty();
            Console.WriteLine($"Saved: {CurrentPath}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Save failed: {ex.Message}");
            return false;
        }
    }

    public void DeleteFile(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            File.Delete(path);
            if (IsCurrent(path)) CurrentPath = null;
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
        string dir = Path.GetDirectoryName(oldPath) ?? Settings.LibraryRootPath.Value;
        MovePath(oldPath, Path.Combine(dir, newName + Extension), isDirectory: false);
    }

    public void MoveFile(string sourcePath, string destDir)
    {
        if (!File.Exists(sourcePath) || !Directory.Exists(destDir)) return;
        MovePath(sourcePath, Path.Combine(destDir, Path.GetFileName(sourcePath)), isDirectory: false);
    }

    public void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            Directory.Delete(path, recursive: true);
            if (CurrentPath != null && IsInside(CurrentPath, path)) CurrentPath = null;
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
        string parentDir = Path.GetDirectoryName(oldPath) ?? Settings.LibraryRootPath.Value;
        MovePath(oldPath, Path.Combine(parentDir, newName), isDirectory: true);
    }

    public void MoveDirectory(string sourcePath, string destDir)
    {
        if (!Directory.Exists(sourcePath) || !Directory.Exists(destDir)) return;
        string destPath = Path.Combine(destDir, Path.GetFileName(sourcePath));
        if (IsInside(destPath, sourcePath))
        {
            Console.WriteLine("MoveDirectory failed: cannot move into own child");
            return;
        }

        MovePath(sourcePath, destPath, isDirectory: true);
    }

    /// <summary>Returns the path an item would get when moved into <paramref name="destDir"/>, or null if that is not possible.</summary>
    public static string? GetMoveDestination(string sourcePath, bool isDirectory, string destDir)
    {
        string destPath = Path.Combine(destDir, Path.GetFileName(sourcePath));
        if (PathsEqual(sourcePath, destPath)) return null;
        if (isDirectory && IsInside(destPath, sourcePath)) return null;
        if (File.Exists(destPath) || Directory.Exists(destPath)) return null;
        return destPath;
    }

    private void MovePath(string oldPath, string newPath, bool isDirectory)
    {
        if (PathsEqual(oldPath, newPath)) return;
        try
        {
            if (File.Exists(newPath) || Directory.Exists(newPath))
            {
                Console.WriteLine($"Move failed: destination already exists: {newPath}");
                return;
            }

            if (isDirectory) Directory.Move(oldPath, newPath);
            else File.Move(oldPath, newPath);

            if (CurrentPath != null && (IsCurrent(oldPath) || (isDirectory && IsInside(CurrentPath, oldPath))))
                CurrentPath = newPath + CurrentPath[oldPath.Length..];
            Console.WriteLine($"Moved: {oldPath} -> {newPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Move failed: {ex.Message}");
        }
    }

    private bool IsCurrent(string path) => CurrentPath != null && PathsEqual(CurrentPath, path);

    private static bool PathsEqual(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string path, string directory) =>
        path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Periodically writes unsaved changes to AutoSave without touching the current file or dirty state.</summary>
    public void AutoSaveIfNeeded()
    {
        if ((DateTime.Now - _lastAutoSave).TotalMinutes < Settings.AutoSaveIntervalMinutes)
            return;

        _lastAutoSave = DateTime.Now;
        if (!_document.IsDirty) return;

        try
        {
            EnsureLibraryRoot();
            _document.SaveToFile(AutoSavePath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AutoSave failed: {ex.Message}");
        }
    }

    public void LoadLastSave()
    {
        if (!File.Exists(AutoSavePath)) return;
        LoadFile(AutoSavePath);
        // The autosave is a recovery copy, not the user's file.
        CurrentPath = null;
        _document.IsDirty = true;
    }

    public void LoadFile(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            _document.LoadFromFile(path);
            CurrentPath = path;
            _renderer.OnDocumentReplaced();
            Console.WriteLine($"Loaded: {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Load failed: {ex.Message}");
        }
    }
}
