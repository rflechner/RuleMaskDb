namespace RuleMaskDb.Sdk;

/// <summary>Optional extension for generators that consume mapped row parameters.</summary>
public interface IContextualDataGenerator : IDataGenerator
{
    /// <summary>Validate all configured mapping outputs and fallbacks before database writes.</summary>
    void ValidateParameters(IReadOnlyDictionary<string, IReadOnlyCollection<string>> possibleValues);

    Task<object> GenerateValueAsync(GenerationContext context, CancellationToken cancellationToken = default);
}
