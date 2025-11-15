using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Immutable;
using Microsoft.Data.SqlClient;
using RuleMaskDb.ScriptDom;

namespace RuleMaskDb;

public interface IScriptRunner
{
    Task RunAsync(ScriptSpecification script, IProgressReporter progressReporter, CancellationToken cancellationToken = default);
    
    Task<bool> IsImpactedAsync(ScriptSpecification script, TableDescription table, FieldDescription field, CancellationToken cancellationToken = default);
}

public class ScriptRunner(IDatabaseAnalyzer databaseAnalyzer) : IScriptRunner
{
    public async Task RunAsync(ScriptSpecification script, IProgressReporter progressReporter, CancellationToken cancellationToken = default)
    {
        var database = await databaseAnalyzer.DescribeDatabaseAsync(new DatabaseSpecification(script.Database.DatabaseType, script.Database.ConnectionString));

        await using var connection = new SqlConnection(script.Database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var impactedTablesNames =
            script.Rules
                .GroupBy(r => r.Table)
                .ToFrozenDictionary(
                    g => g.Key, 
                    g => g.Select(r => r.Column).ToFrozenSet()
                );

        foreach (var table in database.Tables)
        {
            if (!impactedTablesNames.TryGetValue(table.Name, out var rulesFields)) continue;
            
            await StreamRecords(connection, database, table, rulesFields, async (record, progress) =>
            {
                await progressReporter.ReportProgressAsync(table.Name, progress.step, progress.totalSteps, cancellationToken);
                return record;
            }, cancellationToken);
        }
    }

    private async Task StreamRecords(SqlConnection connection, 
        DatabaseDescription database, 
        TableDescription table, 
        FrozenSet<string> rulesFields,
        Func<TableRecord, (int step, int totalSteps), Task<TableRecord>> transformRecord, 
        CancellationToken cancellationToken = default)
    {
        if (!rulesFields.Any()) return;
        
        var sqlFields = string.Join(", ", rulesFields.Select(f => $"[{f}]"));

        var tablesSql = $"SELECT {sqlFields} FROM {table.Name}";
        await using var cmd = new SqlCommand(tablesSql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        
        var step = 0;
        
        var columnsTypes = table.Fields.ToFrozenDictionary(f => f.Path, f => f.DataType);
        
        Dictionary<int, string> columnNames = new();
        
        while (await reader.ReadAsync(cancellationToken))
        {
            var fields = ArrayPool<TableRecordField>.Shared.Rent(reader.FieldCount);

            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = await reader.IsDBNullAsync(i, cancellationToken)
                    ? null
                    : reader.GetValue(i);
            
                if (!columnNames.TryGetValue(i, out var colName)) 
                    columnNames[i] = colName = reader.GetName(i);
                
                var dataType = columnsTypes[colName];
            
                var recordField = new TableRecordField(dataType, colName, value!);
                fields[i] = recordField;
            }

            var record = new TableRecord(database.Name, table.Name, fields);
            
            var anonymizedRecord = await transformRecord(record, (step, table.RowCount));

            // await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken);
            
            step++;
        }
    }

    public async Task<bool> IsImpactedAsync(ScriptSpecification script, TableDescription table, FieldDescription field, CancellationToken cancellationToken = default)
    {
        foreach (var rule in script.Rules)
        {
            if (rule.Table != table.Name) continue;
            
            if (rule.Column == field.Path) return true;
        }
        
        return false;
    }
}

public record TableRecord(string DatabaseName, string TableName, TableRecordField[] Fields);

public record TableRecordField(DateType DataType, string Name, object Value);
