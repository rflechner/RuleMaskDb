using System.Collections.Immutable;
using RuleMaskDb.Yaml;

namespace RuleMaskDb;

public interface IScriptDomProviderFactory
{
    Task<IScriptDomProvider> CreateAsync(Uri uri);
}

public class ScriptDomProviderFactory : IScriptDomProviderFactory
{
    public async Task<IScriptDomProvider> CreateAsync(Uri uri)
    {
        if (uri.Scheme == "file" && uri.LocalPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
        {
            return new YamlScriptDomProvider(uri.LocalPath);
        }
        
        throw new NotSupportedException($"Unsupported URI scheme: {uri.Scheme}");
    }
}

public interface IScriptDomProvider
{
    Task<ScriptSpecification> LoadScriptAsync();
}

public record DatabaseDescription(string Name, ImmutableArray<TableDescription> Tables);

public record TableDescription(string Name, int RowCount, ImmutableArray<FieldDescription> Fields);

public record FieldDescription(string Path, DateType DataType);

public enum DateType
{
    Text,
    IntegerNumber,
    DecimalNumber,
    Date,
    DateTime,
    Boolean
}

public interface IDatabaseAnalyzer
{
    Task<DatabaseDescription> DescribeDatabaseAsync(DatabaseSpecification databaseSpecification);
}
