using System.Data.Common;

namespace RuleMaskDb;

/// <summary>
/// Checks configured row keys using the database's equality and collation rules.
/// Shared by preflight validation and preparation so custom SQL cannot invalidate a key unnoticed.
/// </summary>
internal sealed class ConfiguredRowKeyValidator(RelationalDriver driver)
{
    public async Task ValidateAsync(string connectionString, TablePlan plan, CancellationToken cancellationToken)
    {
        await using var connection = driver.CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await ValidateAsync(connection, null, plan, cancellationToken);
    }

    public async Task ValidateAsync(DbConnection connection, DbTransaction? transaction,
        TablePlan plan, CancellationToken cancellationToken)
    {
        if (plan.ConfiguredRowKey is not { } key) return;

        var column = driver.QuoteIdentifier(key);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // GROUP BY uses the same database equality semantics as the update joins.
        command.CommandText = $"""
            SELECT {column} FROM {driver.QuoteTable(plan.Table)}
            GROUP BY {column} HAVING {column} IS NULL OR COUNT(*) > 1
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException(
                $"Configured row key must be unique and non-null: {plan.Table.Name}.{key}");
    }
}
