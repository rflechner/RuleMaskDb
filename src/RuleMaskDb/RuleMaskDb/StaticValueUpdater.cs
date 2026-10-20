using System.Data.Common;
using RuleMaskDb.Generators;
using RuleMaskDb.ScriptDom;

namespace RuleMaskDb;

/// <summary>
/// Applies static values in bounded key pages, retaining table-wide updates for keyless tables.
/// </summary>
internal sealed class StaticValueUpdater(RelationalDriver driver)
{
    public async Task RunAsync(ScriptSpecification script, IProgressReporter progressReporter,
        TablePlan plan, CancellationToken cancellationToken)
    {
        await using var connection = driver.CreateConnection(script.Database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = script.Database.DatabaseType == DatabaseType.PostgreSQL
            ? await connection.BeginTransactionAsync(cancellationToken) : null;

        if (plan.Keys.Length == 0)
            await UpdateWithoutKeyAsync(connection, transaction, plan, progressReporter, cancellationToken);
        else
            await UpdatePagesAsync(connection, transaction, script.BatchSize, plan, progressReporter, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    private async Task UpdatePagesAsync(DbConnection connection, DbTransaction? transaction, int requestedBatchSize,
        TablePlan plan, IProgressReporter progressReporter, CancellationToken cancellationToken)
    {
        var batchSize = driver.GetEffectiveBatchSize(requestedBatchSize, plan.Keys.Length, plan.Rules.Length);
        var columns = plan.Rules.Select(rule => rule.Column).ToArray();
        var values = GetValues(plan);
        IReadOnlyList<object?>? continuationKey = null;
        var processedRows = 0;
        KeyPage page;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            page = await ReadKeyPageAsync(connection, transaction, plan, batchSize, continuationKey, cancellationToken);
            if (page.IsEmpty) break;

            await using var update = driver.CreateStaticUpdateBatch(connection, plan.Table, plan.Keys, columns, page.RowKeys, values);
            update.Transaction = transaction;
            await BatchUpdateExecutor.ExecuteAsync(update, plan.Table.Name, page.RowCount, cancellationToken);

            continuationKey = page.ContinuationKey;
            processedRows += page.RowCount;
            await progressReporter.ReportProgressAsync(plan.Table.Name, processedRows, plan.Table.RowCount, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        while (page.RequiresNextPage);

        if (processedRows == 0)
            await progressReporter.ReportProgressAsync(plan.Table.Name, 0, plan.Table.RowCount, cancellationToken);
    }

    private async Task<KeyPage> ReadKeyPageAsync(DbConnection connection, DbTransaction? transaction,
        TablePlan plan, int batchSize, IReadOnlyList<object?>? continuationKey, CancellationToken cancellationToken)
    {
        await using var command = driver.CreateKeyPage(connection, plan.Table, plan.Keys, batchSize, continuationKey);
        command.Transaction = transaction;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(Enumerable.Range(0, plan.Keys.Length).Select(reader.GetValue).ToArray());

        // Close the reader before updating, so SQL Server needs neither MARS nor a second connection.
        return new KeyPage(rows, batchSize);
    }

    private async Task UpdateWithoutKeyAsync(DbConnection connection, DbTransaction? transaction,
        TablePlan plan, IProgressReporter progressReporter, CancellationToken cancellationToken)
    {
        await using var update = driver.CreateStaticUpdate(connection, plan.Table,
            plan.Rules.Select(rule => rule.Column).ToArray(), GetValues(plan));
        update.Transaction = transaction;
        await update.ExecuteNonQueryAsync(cancellationToken);

        // Trigger writes and NOCOUNT make ExecuteNonQuery's affected-row count unreliable.
        await progressReporter.ReportProgressAsync(plan.Table.Name, plan.Table.RowCount, plan.Table.RowCount, cancellationToken);
    }

    private static object?[] GetValues(TablePlan plan)
        => plan.Rules.Select(rule => (object?)BogusValueConverter.ToDatabaseValue(rule.Value)).ToArray();
}
