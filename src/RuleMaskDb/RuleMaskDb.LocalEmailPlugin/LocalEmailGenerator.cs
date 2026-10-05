using RuleMaskDb.Sdk;

namespace RuleMaskDb.LocalEmailPlugin;

[Generator("LocalEmail")]
public sealed class LocalEmailGenerator : IDataGenerator
{
    public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<object>($"user-{Guid.NewGuid():N}@local");
    }
}
