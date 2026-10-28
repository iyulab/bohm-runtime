using System.Reflection;
using System.Runtime.Loader;

namespace Bohm.Runtime.Host.Extensions;

/// <summary>
/// Finds the authoring layer the desktop application ships beside the runtime. A runtime run on its own has none and
/// answers the authoring routes 501; nothing else about it changes.
/// </summary>
/// <remarks>
/// Only one file is looked for, by its fixed name, in a folder next to the executable — the application's own install
/// folder, which the person's data folder is not. A layer that cannot be loaded is left out, not half-used.
/// </remarks>
internal static class AuthoringModuleLoader
{
    /// <summary>The folder beside the executable that holds optional layers.</summary>
    public const string Folder = "modules";

    /// <summary>The file name of the authoring layer.</summary>
    public const string FileName = "Bohm.Authoring.dll";

    /// <summary>Where the authoring layer would be, next to the running executable.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, Folder, FileName);

    /// <summary>
    /// The layer found in the assembly at <paramref name="path"/> (the default place when none is given), or
    /// <see langword="null"/> when there is no such file, no layer in it, or it could not be loaded.
    /// </summary>
    public static IAuthoringModule? Load(string? path = null, Action<string>? warn = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return null;
        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
            return FromAssembly(assembly);
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or FileLoadException or ReflectionTypeLoadException or TypeLoadException or MissingMethodException)
        {
            warn?.Invoke($"authoring layer left out: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>The one class in <paramref name="assembly"/> that implements <see cref="IAuthoringModule"/> and can be made with no arguments.</summary>
    public static IAuthoringModule? FromAssembly(Assembly assembly)
    {
        var found = assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IAuthoringModule).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
            .ToList();
        return found.Count == 1 ? (IAuthoringModule?)Activator.CreateInstance(found[0]) : null;
    }
}
