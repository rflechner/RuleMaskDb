using RuleMaskDb.ScriptDom;

namespace RuleMaskDb.Generators;

public sealed class PluginDataGeneratorFactory(PluginCatalog catalog) : IDataGeneratorFactory
{
    private readonly IDataGeneratorFactory builtins = new BogusDataGeneratorFactory();

    public Sdk.IDataGenerator Create(string name)
    {
        var plugin = catalog.Plugins.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return plugin is null ? builtins.Create(name)
            : (Sdk.IDataGenerator)Activator.CreateInstance(plugin.GeneratorType)!;
    }

    public IDataGenerator Create(GeneratorType generatorType) => new Adapter(Create(generatorType.ToString()));

    private sealed class Adapter(Sdk.IDataGenerator generator) : IDataGenerator
    {
        public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
            => generator.GenerateValueAsync(cancellationToken);
    }
}
