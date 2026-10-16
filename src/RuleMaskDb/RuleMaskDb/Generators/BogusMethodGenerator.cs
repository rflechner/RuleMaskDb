using System.Reflection;
using Bogus;
using RuleMaskDb.Sdk;

namespace RuleMaskDb.Generators;

internal sealed class BogusMethodGenerator(Faker faker, BogusGeneratorCatalog.Entry[] entries) : IDataGenerator, IContextualDataGenerator
{
    public void ValidateParameters(IReadOnlyDictionary<string, IReadOnlyCollection<string>> possibleValues)
        => Select(possibleValues);

    private BogusGeneratorCatalog.Entry Select(IReadOnlyDictionary<string, IReadOnlyCollection<string>> values)
    {
        var errors = new List<string>();
        foreach (var entry in entries)
        {
            var parameters = entry.Parameters;
            if (values.Keys.Any(key => parameters.All(p => p.Name != key))) continue;
            if (parameters.Any(p => !p.IsOptional && !values.ContainsKey(p.Name!))) continue;
            try
            {
                foreach (var parameter in parameters)
                    if (values.TryGetValue(parameter.Name!, out var possible))
                        foreach (var value in possible)
                            BogusValueConverter.Parse(value, parameter.ParameterType);
                return entry;
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or System.Text.Json.JsonException)
            {
                errors.Add(ex.Message);
            }
        }
        throw new InvalidOperationException($"Invalid parameters for {entries[0].Name}. "
            + $"Supported signatures: {string.Join("; ", entries.Select(e => "(" + string.Join(", ", e.Parameters.Select(p => p.Name + (p.IsOptional ? "?" : ""))) + ")"))}. "
            + string.Join(" ", errors.Distinct()));
    }

    public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
        => GenerateValueAsync(GenerationContext.Empty, cancellationToken);

    public Task<object> GenerateValueAsync(GenerationContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = Select(context.Parameters.ToDictionary(p => p.Key,
            p => (IReadOnlyCollection<string>)new[] { p.Value }, StringComparer.Ordinal));
        var arguments = entry.Parameters.Select(p => context.Parameters.TryGetValue(p.Name!, out var value)
            ? BogusValueConverter.Parse(value, p.ParameterType) : p.DefaultValue).ToArray();
        try
        {
            return Task.FromResult(BogusValueConverter.ToDatabaseValue(entry.Invoke(faker, arguments)));
        }
        catch (TargetInvocationException ex)
        {
            throw new InvalidOperationException($"Bogus generator {entry.Name} failed: {ex.InnerException?.Message}", ex.InnerException ?? ex);
        }
    }
}
