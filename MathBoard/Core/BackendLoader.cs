using System.Reflection;
using System.Runtime.Loader;
using MathBoard.Graphics;

namespace MathBoard.Core;

/// <summary>Finds render backend plugins in <c>backends/*/MathBoard.Backend.*.dll</c>.</summary>
public static class BackendLoader
{
    public const string Auto = "Auto";

    public static List<IRenderBackend> Discover()
    {
        var backends = new List<IRenderBackend>();
        string root = Path.Combine(AppContext.BaseDirectory, "backends");
        if (!Directory.Exists(root)) return backends;

        foreach (string path in Directory.GetFiles(root, "MathBoard.Backend.*.dll", SearchOption.AllDirectories))
        {
            try
            {
                var assembly = new PluginLoadContext(path).LoadFromAssemblyPath(path);
                foreach (var type in assembly.GetExportedTypes())
                {
                    if (type.IsAbstract || !typeof(IRenderBackend).IsAssignableFrom(type) ||
                        type.GetConstructor(Type.EmptyTypes) == null) continue;
                    backends.Add((IRenderBackend)Activator.CreateInstance(type)!);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Backend plugin {path} could not be loaded: {ex.Message}");
            }
        }

        return backends;
    }

    /// <summary>The user's choice first, then the rest by priority.</summary>
    public static IEnumerable<IRenderBackend> InTryOrder(IEnumerable<IRenderBackend> backends, string preferred) =>
        backends.OrderByDescending(b => b.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(b => b.Priority);

    private sealed class PluginLoadContext(string pluginPath) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(pluginPath))
    {
        private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

        protected override Assembly? Load(AssemblyName name)
        {
            // Assemblies the app ships (the contract, Silk.NET windowing, SkiaSharp) must be shared so types match.
            try
            {
                return Default.LoadFromAssemblyName(name);
            }
            catch (FileNotFoundException)
            {
                string? path = _resolver.ResolveAssemblyToPath(name);
                return path == null ? null : LoadFromAssemblyPath(path);
            }
        }

        protected override nint LoadUnmanagedDll(string name)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(name);
            return path == null ? 0 : LoadUnmanagedDllFromPath(path);
        }
    }
}
