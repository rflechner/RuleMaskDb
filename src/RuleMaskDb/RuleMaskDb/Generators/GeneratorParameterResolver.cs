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
            parameter.Validate(name);
            if (parameter.Column is not null && !table.Fields.Any(f => f.Path == parameter.Column))
                throw new InvalidOperationException($"Unknown parameter column: {table.Name}.{parameter.Column}");
            values.Add(name, (parameter.Value is not null ? new[] { parameter.Value }
                    : parameter.Mapping?.Values ?? Enumerable.Empty<string>())
                .Concat(parameter.Fallback is null ? [] : new[] { parameter.Fallback }).Distinct().ToArray());
        }
        GeneratorInvocation.Validate(generator, values);
    }

    public static GenerationContext Resolve(Rule rule, IReadOnlyDictionary<string, object?> originalValues)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, parameter) in rule.Parameters)
        {
            if (parameter.Value is not null)
            {
                parameters.Add(name, parameter.Value);
                continue;
            }
            if (parameter.Column is null || !originalValues.TryGetValue(parameter.Column, out var original))
                throw new InvalidOperationException($"Missing source column: {parameter.Column}");
            var key = original is null or DBNull ? null : Convert.ToString(original, CultureInfo.InvariantCulture);
            var value = parameter.Mapping is null
                ? string.IsNullOrEmpty(key) ? parameter.Fallback : key
                : key is not null && parameter.Mapping.TryGetValue(key, out var mapped) ? mapped : parameter.Fallback;
            if (value is not null) parameters.Add(name, value);
        }
        return new GenerationContext(parameters);
    }
}
