using System.Buffers;
using System.Collections.Frozen;
using Microsoft.Data.SqlClient;
using RuleMaskDb.Generators;
using RuleMaskDb.ScriptDom;
using RuleMaskDb.SqlDomain;

namespace RuleMaskDb;

public class ScriptRunner(IDatabaseAnalyzer databaseAnalyzer, IDataGeneratorFactory dataGeneratorFactory) : IScriptRunner
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
                    g => new
                    {
                        ColumnsNames = g.Select(r => r.Column).ToFrozenSet(),
                        ColumnsRules = g.ToFrozenDictionary(r => r.Column, r => new
                        {
                            Rule = r,
                            Generator = dataGeneratorFactory.Create(r.Generator ?? GeneratorType.Name)
                        })
                    }
                );

        foreach (var table in database.Tables)
        {
            if (!impactedTablesNames.TryGetValue(table.Name, out var tableRules)) continue;
            
            await StreamRecords(connection, database, table, tableRules.ColumnsNames, async (record, progress) =>
            {
                await progressReporter.ReportProgressAsync(table.Name, progress.step, progress.totalSteps, cancellationToken);

                var anonymizedFields = ArrayPool<TableRecordField?>.Shared.Rent(record.Fields.Length);

                for (var i = 0; i < record.Fields.Length; i++)
                {
                    var field = record.Fields[i];
                    // fields are populated by an array pool, so we need to check if the field is null
                    if (field == null) continue;

                    if (!tableRules.ColumnsRules.TryGetValue(field.Name, out var rule)) continue;

                    var value = await rule.Generator.GenerateValueAsync(cancellationToken);
                    anonymizedFields[i] = field with { Value = value };
                }

                return record with { Fields = anonymizedFields };
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
        
        var columnNames = new Dictionary<int, string>();
        
        while (await reader.ReadAsync(cancellationToken))
        {
            var fields = ArrayPool<TableRecordField?>.Shared.Rent(reader.FieldCount);

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