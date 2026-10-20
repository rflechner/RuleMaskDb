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

    /// <summary>
    /// Maps exact table names to provider-native SQL executed once before their anonymization.
    /// Each script commits independently; a later anonymization failure does not undo it.
    /// </summary>
    public ImmutableDictionary<string, string> BeforeTableSql { get; init; }
        = ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// Ensures preparation scripts are non-empty and target tables with anonymization rules.
    /// This validation must complete before accessing or modifying the database.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A table name or script is empty, or a preparation target has no matching rule.
    /// </exception>
    public void ValidateBeforeTableSql()
    {
        foreach (var (tableName, sql) in BeforeTableSql)
        {
            if (string.IsNullOrWhiteSpace(tableName) || string.IsNullOrWhiteSpace(sql))
                throw new InvalidOperationException("beforeTableSql requires non-empty table names and SQL scripts.");

            if (!Rules.Any(rule => rule.Table == tableName))
                throw new InvalidOperationException($"beforeTableSql entry has no matching anonymization rule: {tableName}");
        }
    }

    public ImmutableDictionary<string, string> TableKeys { get; init; }
        = ImmutableDictionary<string, string>.Empty;
}
