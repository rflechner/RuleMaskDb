namespace RuleMaskDb.Sdk;

/// <summary>A generator is instantiated once per column and run sequentially for its rows.</summary>
public interface IDataGenerator
{
    Task<object> GenerateValueAsync(CancellationToken cancellationToken = default);
}
