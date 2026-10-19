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

    public ImmutableDictionary<string, string> BeforeTableSql { get; init; }
        = ImmutableDictionary<string, string>.Empty;

    public void ValidateBeforeTableSql()
    {
        foreach (var (table, sql) in BeforeTableSql)
        {
            if (string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(sql))
                throw new InvalidOperationException("beforeTableSql requires non-empty table names and SQL scripts.");
            if (!Rules.Any(rule => rule.Table == table))
                throw new InvalidOperationException($"beforeTableSql entry has no matching anonymization rule: {table}");
        }
    }

    public ImmutableDictionary<string, string> TableKeys { get; init; }
        = ImmutableDictionary<string, string>.Empty;
}
