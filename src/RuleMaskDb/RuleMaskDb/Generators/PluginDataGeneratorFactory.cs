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

    private sealed class Adapter(Sdk.IDataGenerator generator) : IDataGenerator, Sdk.IContextualDataGenerator
    {
        public void ValidateParameters(IReadOnlyDictionary<string, IReadOnlyCollection<string>> possibleValues)
            => GeneratorInvocation.Validate(generator, possibleValues);

        public Task<object> GenerateValueAsync(Sdk.GenerationContext context, CancellationToken cancellationToken = default)
            => GeneratorInvocation.GenerateAsync(generator, context, cancellationToken);

        public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
            => generator.GenerateValueAsync(cancellationToken);
    }
}
