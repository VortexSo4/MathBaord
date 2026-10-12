using System.Security.Cryptography;
using MathBoard.Graphics;
using SkiaSharp;

namespace MathBoard.Core;

/// <summary>
/// Pictures pasted into lessons, kept as PNG files in the hidden ".images" folder of the library. Lessons store only
/// the file name, which is a hash of the contents, so the same picture pasted twice is stored once.
/// </summary>
public static class ImageStore
{
    // Textures larger than this are not guaranteed on old GPUs.
    private const int MaxSide = 4096;

    private static readonly Dictionary<string, ImageData?> Cache = new();

    public static string Folder => Path.Combine(Settings.LibraryRootPath, ".images");

    /// <summary>Saves the picture (if it is not stored yet) and returns its file name.</summary>
    public static string Add(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] bytes = png.ToArray();
        string name = Convert.ToHexString(SHA256.HashData(bytes))[..20].ToLowerInvariant() + ".png";

        var folder = Directory.CreateDirectory(Folder);
        folder.Attributes |= FileAttributes.Hidden;
        string path = Path.Combine(Folder, name);
        if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
        Cache[name] = ToImageData(bitmap);
        return name;
    }

    /// <summary>The picture's pixels, or null if its file is missing or unreadable.</summary>
    public static ImageData? Get(string name)
    {
        if (Cache.TryGetValue(name, out var cached)) return cached;
        ImageData? data = null;
        try
        {
            using var bitmap = SKBitmap.Decode(Path.Combine(Folder, name));
            if (bitmap != null) data = ToImageData(bitmap);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Picture {name} not loaded: {ex.Message}");
        }

        Cache[name] = data;
        return data;
    }

    /// <summary>Forgets the pixels of pictures not listed; they are loaded from their files again when needed.</summary>
    public static void Retain(IEnumerable<string> names)
    {
        var keep = names.ToHashSet();
        foreach (var name in Cache.Keys.Where(n => !keep.Contains(n)).ToList()) Cache.Remove(name);
    }

    /// <summary>Loads a picture file, or returns null if it is not one.</summary>
    public static SKBitmap? Load(string path)
    {
        try
        {
            return SKBitmap.Decode(path);
        }
        catch
        {
            return null;
        }
    }

    public static ImageData ToImageData(SKBitmap bitmap)
    {
        float scale = MathF.Min(1f, (float)MaxSide / Math.Max(bitmap.Width, bitmap.Height));
        int w = Math.Max(1, (int)(bitmap.Width * scale)), h = Math.Max(1, (int)(bitmap.Height * scale));
        using var rgba = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.ScalePixels(rgba, new SKSamplingOptions(SKCubicResampler.Mitchell));
        return new ImageData(w, h, rgba.Bytes);
    }
}
