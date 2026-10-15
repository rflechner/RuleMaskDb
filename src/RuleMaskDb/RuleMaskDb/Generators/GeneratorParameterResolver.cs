using System.Globalization;
using RuleMaskDb.ScriptDom;
using RuleMaskDb.Sdk;
using RuleMaskDb.SqlDomain;

namespace RuleMaskDb.Generators;

public static class GeneratorParameterResolver
{
    public static void Validate(Rule rule, TableDescription table, Sdk.IDataGenerator generator)
    {
        var values = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
        foreach (var (name, parameter) in rule.Parameters)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("Generator parameter names must not be empty.");
            if (string.IsNullOrWhiteSpace(parameter.Column) || !table.Fields.Any(f => f.Path == parameter.Column))
                throw new InvalidOperationException($"Unknown parameter column: {table.Name}.{parameter.Column}");
            if (parameter.Mapping.Values.Any(v => v is null))
                throw new InvalidOperationException($"Null mapping output for parameter '{name}'.");
            values.Add(name, parameter.Mapping.Values
                .Concat(parameter.Fallback is null ? [] : new[] { parameter.Fallback }).Distinct().ToArray());
        }
        GeneratorInvocation.Validate(generator, values);
    }

    public static GenerationContext Resolve(Rule rule, IReadOnlyDictionary<string, object?> originalValues)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, parameter) in rule.Parameters)
        {
            if (!originalValues.TryGetValue(parameter.Column, out var original))
                throw new InvalidOperationException($"Missing source column: {parameter.Column}");
            var key = original is null or DBNull ? null : Convert.ToString(original, CultureInfo.InvariantCulture);
            var value = key is not null && parameter.Mapping.TryGetValue(key, out var mapped)
                ? mapped : parameter.Fallback;
            if (value is not null) parameters.Add(name, value);
        }
        return new GenerationContext(parameters);
    }
}
