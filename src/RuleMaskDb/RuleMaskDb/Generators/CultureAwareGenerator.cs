using Bogus;
using RuleMaskDb.Sdk;

namespace RuleMaskDb.Generators;

/// <summary>One per rule, with bounded reuse of generators for installed Bogus locales.</summary>
internal sealed class CultureAwareGenerator(Func<Faker, IDataGenerator> create) : IDataGenerator, IContextualDataGenerator
{
    private readonly Dictionary<string, IDataGenerator> generators = new(StringComparer.Ordinal);

    private IDataGenerator GetGenerator(string locale)
    {
        if (!generators.TryGetValue(locale, out var generator))
            generators.Add(locale, generator = create(new Faker(locale)));
        return generator;
    }

    public void ValidateParameters(IReadOnlyDictionary<string, IReadOnlyCollection<string>> possibleValues)
    {
        var others = possibleValues.Where(p => p.Key != "culture").ToDictionary(p => p.Key, p => p.Value);
        // The default also covers rows whose parameter is omitted (no fallback).
        var locales = new HashSet<string>(StringComparer.Ordinal) { "en" };
        if (possibleValues.TryGetValue("culture", out var cultures))
            foreach (var culture in cultures)
                locales.Add(ResolveLocale(new GenerationContext(new Dictionary<string, string> { ["culture"] = culture })));
        foreach (var locale in locales)
            GeneratorInvocation.Validate(GetGenerator(locale), others);
    }

    private static string ResolveLocale(GenerationContext context)
        => context.GetCulture() is { } culture ? BogusCultureResolver.Resolve(culture) : "en";

    public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
        => GenerateValueAsync(GenerationContext.Empty, cancellationToken);

    public Task<object> GenerateValueAsync(GenerationContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var generator = GetGenerator(ResolveLocale(context));
        var others = new GenerationContext(context.Parameters.Where(p => p.Key != "culture")
            .ToDictionary(p => p.Key, p => p.Value));
        // Direct column values cannot be validated before reading rows.
        GeneratorInvocation.Validate(generator, others.Parameters.ToDictionary(
            p => p.Key, p => (IReadOnlyCollection<string>)new[] { p.Value }));
        return GeneratorInvocation.GenerateAsync(generator, others, cancellationToken);
    }
}
