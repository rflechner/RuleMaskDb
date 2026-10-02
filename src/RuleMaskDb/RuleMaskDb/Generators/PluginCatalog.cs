using System.Reflection;
using System.Runtime.Loader;
using RuleMaskDb.Sdk;

namespace RuleMaskDb.Generators;

public sealed record GeneratorPlugin(string Name, Type GeneratorType, string AssemblyPath);

public sealed class PluginCatalog
{
    public IReadOnlyList<GeneratorPlugin> Plugins { get; }

    private PluginCatalog(IEnumerable<GeneratorPlugin> plugins) => Plugins = Array.AsReadOnly(plugins.ToArray());

    public static PluginCatalog Discover(string directory)
    {
        var plugins = new Dictionary<string, GeneratorPlugin>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory)) return new PluginCatalog([]);

        foreach (var path in Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var fullPath = Path.GetFullPath(path);
            // Always share the host's SDK so interface and attribute identities remain identical.
            if (Path.GetFileNameWithoutExtension(path).Equals(typeof(GeneratorAttribute).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase))
                continue;
            Assembly assembly;
            try
            {
                AssemblyName.GetAssemblyName(fullPath);
            }
            catch (BadImageFormatException) { continue; } // Native dependency, not a managed plugin.
            try
            {
                assembly = new PluginLoadContext(fullPath).LoadManagedAssembly(fullPath);
                foreach (var type in assembly.GetTypes())
                {
                    var attribute = type.GetCustomAttribute<GeneratorAttribute>();
                    if (attribute is null) continue;
                    if (!type.IsVisible || !type.IsClass || type.IsAbstract || type.ContainsGenericParameters ||
                        !typeof(Sdk.IDataGenerator).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) is null)
                        throw new InvalidOperationException($"Generator {type.FullName} must be a public, concrete IDataGenerator with a public parameterless constructor.");
                    var plugin = new GeneratorPlugin(attribute.Name, type, fullPath);
                    if (!plugins.TryAdd(plugin.Name, plugin))
                        throw new InvalidOperationException($"Duplicate plugin generator '{plugin.Name}' in '{plugins[plugin.Name].AssemblyPath}' and '{fullPath}'.");
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not load plugin DLL '{fullPath}': {ex.Message}", ex);
            }
        }
        return new PluginCatalog(plugins.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase));
    }

    private sealed class PluginLoadContext(string path) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver resolver = new(path);

        public Assembly LoadManagedAssembly(string assemblyPath)
        {
            using var stream = File.OpenRead(assemblyPath);
            return LoadFromStream(stream);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var sdk = typeof(GeneratorAttribute).Assembly;
            if (assemblyName.Name == sdk.GetName().Name) return sdk;
            var resolved = resolver.ResolveAssemblyToPath(assemblyName);
            // Support simple DLL-only deployments with adjacent dependencies too.
            var adjacent = Path.Combine(Path.GetDirectoryName(path)!, assemblyName.Name + ".dll");
            return resolved is not null ? LoadManagedAssembly(resolved)
                : File.Exists(adjacent) ? LoadManagedAssembly(adjacent) : null;
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var resolved = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return resolved is null ? 0 : LoadUnmanagedDllFromPath(resolved);
        }
    }
}
