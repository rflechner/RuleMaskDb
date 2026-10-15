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
        var plans = new List<(TableDescription Table, Rule[] Rules, Sdk.IDataGenerator[] Generators)>();
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
            var generators = rules.Select(r => dataGeneratorFactory.Create(r.Generator ?? nameof(GeneratorType.Name))).ToArray();
            for (var i = 0; i < rules.Length; i++)
                GeneratorParameterResolver.Validate(rules[i], table, generators[i]);
            plans.Add((table, rules, generators));
        }
        foreach (var (table, rules, generators) in plans)
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
            await using var select = readerConnection.CreateCommand();
            var columns = keys.Concat(rules.Select(r => r.Column))
                .Concat(rules.SelectMany(r => r.Parameters.Values.Select(p => p.Column)))
                .Distinct(StringComparer.Ordinal).ToArray();
            select.CommandText = $"SELECT {string.Join(", ", columns.Select(driver.QuoteIdentifier))} FROM {driver.QuoteTable(table)}";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            var step = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                // Snapshot dependencies before any generator runs; rule order cannot change their values.
                var originalValues = columns.Select((column, i) => (column, value: (object?)reader.GetValue(i)))
                    .ToDictionary(p => p.column, p => p.value, StringComparer.Ordinal);
                var contexts = rules.Select(r => GeneratorParameterResolver.Resolve(r, originalValues)).ToArray();
                await using var update = updaterConnection.CreateCommand();
                update.Transaction = transaction;
                var assignments = new List<string>();
                for (var i = 0; i < generators.Length; i++)
                {
                    var originalValue = reader.GetValue(keys.Length + i);
                    if (originalValue is null or DBNull or string { Length: 0 }) continue;
                    assignments.Add($"{driver.QuoteIdentifier(rules[i].Column)} = @set{i}");
                    AddParameter(update, $"set{i}", await GeneratorInvocation.GenerateAsync(generators[i], contexts[i], cancellationToken));
                }
                if (assignments.Count > 0)
                {
                    var predicates = keys.Select((key, i) => $"{driver.QuoteIdentifier(key)} = @pk{i}");
                    update.CommandText = $"UPDATE {driver.QuoteTable(table)} SET {string.Join(", ", assignments)} WHERE {string.Join(" AND ", predicates)}";
                    for (var i = 0; i < keys.Length; i++) AddParameter(update, $"pk{i}", reader.GetValue(i));
                    var affected = await update.ExecuteNonQueryAsync(cancellationToken);
                    if (affected != 1) throw new InvalidOperationException($"Expected one updated row in {table.Name}, got {affected}.");
                }
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
