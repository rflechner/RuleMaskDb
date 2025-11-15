using System.Collections.Immutable;

namespace RuleMaskDb;

public interface IScriptDomProvider
{
    Task<ScriptSpecification> LoadScriptAsync();
}

public class YamlScriptDomProvider(string filePath) : IScriptDomProvider
{
    public async Task<ScriptSpecification> LoadScriptAsync()
    {
        // Read YAML content from the provided file path
        var yaml = await File.ReadAllTextAsync(filePath);

        // Build a YAML deserializer with camelCase naming convention
        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        // Deserialize into internal DTOs matching the YAML schema
        var doc = deserializer.Deserialize<YamlScript>(yaml) ?? new YamlScript();

        // Map database type
        var dbType = Enum.TryParse<DatabaseType>(doc.Database?.Type ?? string.Empty, true, out var parsedDbType)
            ? parsedDbType
            : DatabaseType.SqlServer; // default to SqlServer if unspecified/unknown

        var database = new DatabaseSpecification(dbType, doc.Database?.ConnectionString ?? string.Empty);

        // Map rules
        var rules = (doc.Rules ?? Enumerable.Empty<YamlRule>())
            .Select(r =>
            {
                GeneratorType? gen = null;
                if (!string.IsNullOrWhiteSpace(r.Generator) && Enum.TryParse<GeneratorType>(r.Generator, true, out var g))
                {
                    gen = g;
                }
                return new Rule(r.Table ?? string.Empty, r.Column ?? string.Empty, r.Mask, gen);
            })
            .ToImmutableArray();

        return new ScriptSpecification(database, rules);
    }

    // Internal DTOs to match the YAML structure without leaking YamlDotNet attributes into domain types
    private sealed class YamlScript
    {
        public YamlDatabase? Database { get; set; }
        public List<YamlRule>? Rules { get; set; }
    }

    private sealed class YamlDatabase
    {
        public string? Type { get; set; }
        public string? ConnectionString { get; set; }
    }

    private sealed class YamlRule
    {
        public string? Table { get; set; }
        public string? Column { get; set; }
        public string? Mask { get; set; }
        public string? Generator { get; set; }
    }
}