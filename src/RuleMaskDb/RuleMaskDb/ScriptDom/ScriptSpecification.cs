using System.Collections.Immutable;

namespace RuleMaskDb.ScriptDom;

public record ScriptSpecification(DatabaseSpecification Database, ImmutableArray<Rule> Rules)
{
    public int BatchSize { get; init; } = 200;

    /// <summary>
    /// Validates the BatchSize property to ensure it is a positive integer.
    /// This method ensures that batch sizes are properly configured to avoid invalid operations.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the BatchSize property is less than or equal to zero.
    /// </exception>
    public void ValidateBatchSize()
    {
        if (BatchSize <= 0)
            throw new InvalidOperationException("batchSize must be greater than zero.");
    }

    /// <summary>
    /// Validates the entries in the TableKeys dictionary to ensure each key-value pair has:
    /// - Non-empty table and column names.
    /// - A corresponding rule in the Rules collection that matches the specified table name.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if a table or column name in TableKeys is null, empty, or whitespace,
    /// or if a table in TableKeys does not have a matching entry in the Rules collection.
    /// </exception>
    public void ValidateTablesKeys()
    {
        foreach (var (table, column) in TableKeys)
        {
            if (string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(column))
                throw new InvalidOperationException("tableKeys requires non-empty table and column names.");
            if (!Rules.Any(r => r.Table == table))
                throw new InvalidOperationException($"tableKeys entry has no matching rule: {table}");
        }
    }

    public ImmutableArray<string> TruncateTables { get; init; } = [];

    public ImmutableDictionary<string, string> TableKeys { get; init; }
        = ImmutableDictionary<string, string>.Empty;
}
