using System.Data.Common;

namespace RuleMaskDb;

/// <summary>
/// Prepares a table before either update strategy opens a connection.
/// Preparation commits independently so subsequent readers see its changes without holding its locks.
/// </summary>
internal sealed class TablePreparationExecutor(RelationalDriver driver)
{
    public async Task<TablePlan> PrepareAsync(string connectionString, TablePlan plan,
        CancellationToken cancellationToken)
    {
        await using var connection = driver.CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = plan.PreparationSql is not null
            ? await connection.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            if (plan.PreparationSql is { } sql)
                await ExecuteSqlAsync(connection, transaction!, sql, cancellationToken);

            // An earlier table's script can also change this table, even without its own script.
            await new ConfiguredRowKeyValidator(driver).ValidateAsync(connection, transaction, plan, cancellationToken);
            var rowCount = await CountRowsAsync(connection, transaction, plan, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);

            return plan.WithRowCount(rowCount);
        }
        catch (DbException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException($"beforeTableSql preparation failed for table '{plan.Table.Name}'.", exception);
        }
    }

    private static async Task ExecuteSqlAsync(DbConnection connection, DbTransaction transaction,
        string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> CountRowsAsync(DbConnection connection, DbTransaction? transaction,
        TablePlan plan, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {driver.QuoteTable(plan.Table)}";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }
}
