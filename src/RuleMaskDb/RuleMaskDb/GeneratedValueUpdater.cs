using System.Data.Common;
using System.Runtime.CompilerServices;
using RuleMaskDb.Generators;
using RuleMaskDb.ScriptDom;

namespace RuleMaskDb;

/// <summary>
/// Streams original rows, prepares generated and mixed assignments, and flushes bounded batches.
/// </summary>
internal sealed class GeneratedValueUpdater(RelationalDriver driver, TablePlan plan)
{
    public async Task RunAsync(ScriptSpecification script, IProgressReporter progressReporter, CancellationToken cancellationToken)
    {
        await using var readerConnection = driver.CreateConnection(script.Database.ConnectionString);
        await using var updaterConnection = driver.CreateConnection(script.Database.ConnectionString);
        await readerConnection.OpenAsync(cancellationToken);
        await updaterConnection.OpenAsync(cancellationToken);

        // PostgreSQL supports a streaming reader alongside a table transaction. SQL Server
        // retains autocommit so updater lock escalation cannot block its separate reader.
        await using var transaction = script.Database.DatabaseType == DatabaseType.PostgreSQL
            ? await updaterConnection.BeginTransactionAsync(cancellationToken) : null;
        var sourceColumns = GetSourceColumns();
        await using var select = CreateSourceQuery(readerConnection, sourceColumns);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        var columnTypes = GetUpdateColumnTypes(reader);
        var batchSize = driver.GetEffectiveBatchSize(script.BatchSize, plan.Keys.Length + plan.Rules.Length);
        var processedRowCount = 0;

        await foreach (var batch in ReadPreparedBatchesAsync(reader, sourceColumns, batchSize, cancellationToken))
        {
            await FlushAsync(batch, updaterConnection, transaction, columnTypes, cancellationToken);

            processedRowCount += batch.ReadRowCount;
            await progressReporter.ReportProgressAsync(plan.Table.Name, processedRowCount, plan.Table.RowCount, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    private string[] GetSourceColumns()
        => plan.Keys.Concat(plan.Rules.Select(rule => rule.Column))
            .Concat(plan.Rules.SelectMany(rule => rule.Parameters.Values
                .Where(parameter => parameter.Column is not null).Select(parameter => parameter.Column!)))
            .Distinct(StringComparer.Ordinal).ToArray();

    private DbCommand CreateSourceQuery(DbConnection connection, IReadOnlyList<string> sourceColumns)
    {
        var query = $"SELECT {string.Join(", ", sourceColumns.Select(driver.QuoteIdentifier))} FROM {driver.QuoteTable(plan.Table)}";
        var command = connection.CreateCommand();
        command.CommandText = query;
        return command;
    }

    private string[] GetUpdateColumnTypes(DbDataReader reader)
        => plan.Keys.Concat(plan.Rules.Select(rule => rule.Column))
            .Select(column => reader.GetDataTypeName(reader.GetOrdinal(column))).ToArray();

    private async IAsyncEnumerable<PendingUpdateBatch> ReadPreparedBatchesAsync(DbDataReader reader,
        IReadOnlyList<string> sourceColumns, int batchSize, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var batch = new PendingUpdateBatch(batchSize);

        while (await reader.ReadAsync(cancellationToken))
        {
            batch.RecordRow(await PrepareRowUpdateAsync(reader, sourceColumns, cancellationToken));
            if (!batch.IsFull) continue;

            yield return batch;
            batch = new PendingUpdateBatch(batchSize);
        }

        if (!batch.IsEmpty) yield return batch;
    }

    private async Task<PreparedRowUpdate> PrepareRowUpdateAsync(DbDataReader reader,
        IReadOnlyList<string> sourceColumns, CancellationToken cancellationToken)
    {
        // Resolve all parameter contexts before any generator runs, so rule order cannot
        // change the original values used by another rule's parameters.
        var originalValues = ReadOriginalValues(reader, sourceColumns);
        var contexts = plan.Rules.Select(rule => GeneratorParameterResolver.Resolve(rule, originalValues)).ToArray();
        var changedRuleIndices = new List<int>();
        var values = plan.Keys.Select(key => originalValues[key]).ToList();

        for (var ruleIndex = 0; ruleIndex < plan.Rules.Length; ruleIndex++)
        {
            var rule = plan.Rules[ruleIndex];
            if (ShouldPreserveOriginalValue(rule, originalValues[rule.Column])) continue;

            changedRuleIndices.Add(ruleIndex);
            values.Add(rule.HasValue
                ? BogusValueConverter.ToDatabaseValue(rule.Value)
                : await GeneratorInvocation.GenerateAsync(plan.Generators[ruleIndex]!, contexts[ruleIndex], cancellationToken));
        }

        return new PreparedRowUpdate([..changedRuleIndices], [..values]);
    }

    private static Dictionary<string, object?> ReadOriginalValues(DbDataReader reader, IReadOnlyList<string> sourceColumns)
    {
        var values = new Dictionary<string, object?>(sourceColumns.Count, StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < sourceColumns.Count; ordinal++)
            values.Add(sourceColumns[ordinal], reader.GetValue(ordinal));

        return values;
    }

    private static bool ShouldPreserveOriginalValue(Rule rule, object? originalValue)
        => !rule.HasValue && (originalValue is null or DBNull or string { Length: 0 });

    /// <summary>
    /// Writes each group of compatible assignments and verifies that every submitted key matched one row.
    /// </summary>
    private async Task FlushAsync(PendingUpdateBatch batch, DbConnection connection, DbTransaction? transaction,
        IReadOnlyList<string> columnTypes, CancellationToken cancellationToken)
    {
        foreach (var group in batch.GroupByChangedColumns())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var changedColumns = group.ChangedRuleIndices.Select(index => plan.Rules[index].Column).ToArray();
            var changedColumnTypes = columnTypes.Take(plan.Keys.Length)
                .Concat(group.ChangedRuleIndices.Select(index => columnTypes[plan.Keys.Length + index])).ToArray();

            await using var update = driver.CreateUpdateBatch(connection, plan.Table, plan.Keys,
                changedColumns, group.Rows, changedColumnTypes);
            update.Transaction = transaction;
            await BatchUpdateExecutor.ExecuteAsync(update, plan.Table.Name, group.RowCount, cancellationToken);
        }
    }
}
