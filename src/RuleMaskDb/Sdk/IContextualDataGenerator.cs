namespace RuleMaskDb.Sdk;

/// <summary>Optional extension for generators that consume mapped row parameters.</summary>
public interface IContextualDataGenerator : IDataGenerator
{
    /// <summary>
    /// Validate fixed values, mapping outputs and fallbacks before database writes.
    /// Direct-column values must also be validated during generation.
    /// </summary>
    void ValidateParameters(IReadOnlyDictionary<string, IReadOnlyCollection<string>> possibleValues);

    Task<object> GenerateValueAsync(GenerationContext context, CancellationToken cancellationToken = default);
}
