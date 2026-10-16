using System.Collections.Immutable;

namespace RuleMaskDb.ScriptDom;

public record ScriptSpecification(DatabaseSpecification Database, ImmutableArray<Rule> Rules)
{
    public int BatchSize { get; init; } = 200;

    public void ValidateBatchSize()
    {
        if (BatchSize <= 0)
            throw new InvalidOperationException("batchSize must be greater than zero.");
    }

    public ImmutableArray<string> TruncateTables { get; init; } = [];

    public ImmutableDictionary<string, string> TableKeys { get; init; }
        = ImmutableDictionary<string, string>.Empty;
}
