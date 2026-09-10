using System.Data.Common;
using RuleMaskDb.Generators;
using RuleMaskDb.ScriptDom;
using RuleMaskDb.SqlDomain;

namespace RuleMaskDb;

public class ScriptRunner(IDatabaseAnalyzer databaseAnalyzer, IDataGeneratorFactory dataGeneratorFactory) : IScriptRunner
{
    public async Task RunAsync(ScriptSpecification script, IProgressReporter progressReporter, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var driver = new RelationalDriver(script.Database.DatabaseType);
        var database = await databaseAnalyzer.DescribeDatabaseAsync(script.Database);
        var plans = new List<(TableDescription Table, Rule[] Rules)>();
        // Validate every target before making the first change.
        foreach (var group in script.Rules.GroupBy(r => r.Table))
        {
            var matches = database.Tables.Where(t => t.Name == group.Key).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException($"Table target is missing or ambiguous: {group.Key}");
            var table = matches[0];
            var rules = group.ToArray();
            if (rules.Select(r => r.Column).Distinct().Count() != rules.Length)
                throw new InvalidOperationException($"Duplicate column rules for {table.Name}");
            foreach (var rule in rules)
            {
                var field = table.Fields.SingleOrDefault(f => f.Path == rule.Column)
                    ?? throw new InvalidOperationException($"Unknown column: {table.Name}.{rule.Column}");
                if (field.IsPrimaryKey) throw new InvalidOperationException($"Cannot anonymize primary key: {table.Name}.{field.Path}");
            }
            if (!table.Fields.Any(f => f.IsPrimaryKey))
                throw new InvalidOperationException($"Table has no primary key: {table.Name}");
            plans.Add((table, rules));
        }
        foreach (var (table, rules) in plans)
        {
            await using var readerConnection = driver.CreateConnection(script.Database.ConnectionString);
            await using var updaterConnection = driver.CreateConnection(script.Database.ConnectionString);
            await readerConnection.OpenAsync(cancellationToken);
            await updaterConnection.OpenAsync(cancellationToken);
            // PostgreSQL MVCC allows a streaming reader alongside a table transaction.
            // Preserve SQL Server autocommit to avoid updater lock escalation blocking the reader.
            await using var transaction = script.Database.DatabaseType == DatabaseType.PostgreSQL
                ? await updaterConnection.BeginTransactionAsync(cancellationToken) : null;
            var keys = table.Fields.Where(f => f.IsPrimaryKey).Select(f => f.Path).ToArray();
            var generators = rules.Select(r => dataGeneratorFactory.Create(r.Generator ?? GeneratorType.Name)).ToArray();
            await using var select = readerConnection.CreateCommand();
            select.CommandText = $"SELECT {string.Join(", ", keys.Select(driver.QuoteIdentifier))} FROM {driver.QuoteTable(table)}";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            var step = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                await using var update = updaterConnection.CreateCommand();
                update.Transaction = transaction;
                var assignments = rules.Select((r, i) => $"{driver.QuoteIdentifier(r.Column)} = @set{i}");
                var predicates = keys.Select((key, i) => $"{driver.QuoteIdentifier(key)} = @pk{i}");
                update.CommandText = $"UPDATE {driver.QuoteTable(table)} SET {string.Join(", ", assignments)} WHERE {string.Join(" AND ", predicates)}";
                for (var i = 0; i < generators.Length; i++)
                    AddParameter(update, $"set{i}", await generators[i].GenerateValueAsync(cancellationToken));
                for (var i = 0; i < keys.Length; i++) AddParameter(update, $"pk{i}", reader.GetValue(i));
                var affected = await update.ExecuteNonQueryAsync(cancellationToken);
                if (affected != 1) throw new InvalidOperationException($"Expected one updated row in {table.Name}, got {affected}.");
                await progressReporter.ReportProgressAsync(table.Name, ++step, table.RowCount, cancellationToken);
            }
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    public Task<bool> IsImpactedAsync(ScriptSpecification script, TableDescription table, FieldDescription field, CancellationToken cancellationToken = default)
        => Task.FromResult(!field.IsPrimaryKey && script.Rules.Any(r => r.Table == table.Name && r.Column == field.Path));
}
