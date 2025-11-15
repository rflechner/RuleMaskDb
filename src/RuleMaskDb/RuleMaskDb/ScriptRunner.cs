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
            
            await StreamRecords(script.Database.ConnectionString, database, table, tableRules.ColumnsNames, async (record, progress) =>
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

    private static async Task<SqlConnection> CreateSqlConnection(string connectionString, CancellationToken cancellationToken)
    {
        SqlConnection? connection = null;
        try
        {
            connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            if (connection != null) await connection.DisposeAsync();
            throw;
        }
    }

    private async Task StreamRecords(
        string connectionString,
        DatabaseDescription database, 
        TableDescription table, 
        FrozenSet<string> rulesFields,
        Func<TableRecord, (int step, int totalSteps), Task<TableRecord>> transformRecord, 
        CancellationToken cancellationToken = default)
    {
        if (!rulesFields.Any()) return;
        
        await using var readerConnection = await CreateSqlConnection(connectionString, cancellationToken);
        await using var updaterConnection = await CreateSqlConnection(connectionString, cancellationToken);
        
        // also fetch primary keys so we can build the WHERE clause for updates
        var pkFields = table.Fields.Where(f => f.IsPrimaryKey).Select(f => f.Path).ToArray();
        var selectedFields = rulesFields.Concat(pkFields).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var sqlFields = string.Join(", ", selectedFields.Select(f => $"[{f}]"));

        var tablesSql = $"SELECT {sqlFields} FROM {table.Name}";
        await using var cmd = new SqlCommand(tablesSql, readerConnection);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        
        var step = 0;
        
        var columnsTypes = table.Fields.ToFrozenDictionary(f => f.Path, f => f.DataType);
        var pkSet = new HashSet<string>(pkFields, StringComparer.OrdinalIgnoreCase);
        
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

            await UpdateRecordAsync(updaterConnection, table, record, anonymizedRecord, pkSet, cancellationToken);

            step++;
        }
    }

    private static async Task UpdateRecordAsync(
        SqlConnection connection, 
        TableDescription table,
        TableRecord record, 
        TableRecord anonymizedRecord, 
        HashSet<string> pkSet, 
        CancellationToken cancellationToken = default)
    {
        // Update database with anonymized record (basic update logic)
        // Build a map of original values by column name (for PKs)
        var originalByName = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var rf in record.Fields)
        {
            if (rf == null) continue;
            originalByName[rf.Name] = rf.Value;
        }

        // Determine changed fields (those provided by the transformer) excluding PKs
        var changed = anonymizedRecord.Fields
            .OfType<TableRecordField>()
            .Where(nf => !pkSet.Contains(nf.Name))
            .ToList();

        // If nothing changed or no PK available, skip update
        if (changed.Count <= 0 || pkSet.Count <= 0) return;

        var setClause = string.Join(", ", changed.Select((f, idx) => $"[{f.Name}] = @set{idx}"));
        // Fix order determinism: build a fixed array order for PKs to reuse for params and WHERE
        var pkOrder = pkSet.ToArray();
        var whereClause = string.Join(" AND ", pkOrder.Select((pk, idx) => $"[{pk}] = @pk{idx}"));
        var updateSql = $"UPDATE {table.Name} SET {setClause} WHERE {whereClause}";

        await using var updateCmd = new SqlCommand(updateSql, connection);

        // SET parameters
        for (var i = 0; i < changed.Count; i++)
        {
            var val = changed[i].Value ?? DBNull.Value;
            updateCmd.Parameters.AddWithValue($"@set{i}", val);
        }

        // WHERE parameters (PKs) – use original values
        for (var pkIndex = 0; pkIndex < pkOrder.Length; pkIndex++)
        {
            var pk = pkOrder[pkIndex];
            originalByName.TryGetValue(pk, out var pkVal);
            updateCmd.Parameters.AddWithValue($"@pk{pkIndex}", pkVal ?? DBNull.Value);
        }

        await updateCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<bool> IsImpactedAsync(ScriptSpecification script, TableDescription table, FieldDescription field, CancellationToken cancellationToken = default)
    {
        foreach (var rule in script.Rules)
        {
            if (rule.Table != table.Name) continue;
            
            if (rule.Column == field.Path) return Task.FromResult(true);
        }
        
        return Task.FromResult(false);
    }
}