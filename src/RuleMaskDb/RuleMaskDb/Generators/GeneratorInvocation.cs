using RuleMaskDb.Sdk;

namespace RuleMaskDb.Generators;

internal static class GeneratorInvocation
{
    public static void Validate(Sdk.IDataGenerator generator, IReadOnlyDictionary<string, IReadOnlyCollection<string>> values)
    {
        if (generator is IContextualDataGenerator contextual)
            contextual.ValidateParameters(values);
        else if (values.Count > 0)
            throw new InvalidOperationException($"Generator {generator.GetType().Name} does not support parameters.");
    }

    public static Task<object> GenerateAsync(Sdk.IDataGenerator generator, GenerationContext context, CancellationToken cancellationToken)
        => generator is IContextualDataGenerator contextual
            ? contextual.GenerateValueAsync(context, cancellationToken)
            : generator.GenerateValueAsync(cancellationToken);
}
