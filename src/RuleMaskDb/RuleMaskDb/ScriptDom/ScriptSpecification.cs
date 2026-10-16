using System.Collections.Immutable;

namespace RuleMaskDb.ScriptDom;

public record ScriptSpecification(DatabaseSpecification Database, ImmutableArray<Rule> Rules)
{
    public ImmutableArray<string> TruncateTables { get; init; } = [];

    public ImmutableDictionary<string, string> TableKeys { get; init; }
        = ImmutableDictionary<string, string>.Empty;
}
