using RuleMaskDb.Generators;
using RuleMaskDb.ScriptDom;
using RuleMaskDb.SqlDomain;

namespace RuleMaskDb;

public class ScriptRunner(IDatabaseAnalyzer databaseAnalyzer, IDataGeneratorFactory dataGeneratorFactory) : IScriptRunner
{
    public async Task RunAsync(ScriptSpecification script, IProgressReporter progressReporter, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        script.ValidateBatchSize();
        script.ValidateTablesKeys();

        var database = await databaseAnalyzer.DescribeDatabaseAsync(script.Database);
        var driver = new RelationalDriver(script.Database.DatabaseType);
        var plans = await LoadAndValidatePlans(script, cancellationToken, database, driver);

        await TruncateTablesAsync(script, progressReporter, database, driver, cancellationToken);

        foreach (var plan in plans)
        {
            await RunPlanAsync(script, progressReporter, plan, driver, cancellationToken);
        }
    }

    /// <summary>
    /// Executes the provided table plan asynchronously by streaming data from the source database,
    /// applying rules transformations, and committing updates using the relational driver.
    /// Ensures efficient processing by batching and handling transactions when supported.
    /// </summary>
    /// <param name="script">The script specification containing the database configuration and execution parameters.</param>
    /// <param name="progressReporter">The progress reporter to provide feedback on the execution status.</param>
    /// <param name="plan">The table plan specifying the transformation rules, keys, and target table mappings.</param>
    /// <param name="driver">The relational driver used to interact with the database during execution.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests during the execution process.</param>
    /// <returns>A task that represents the asynchronous execution of the plan.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the execution encounters errors in applying rules or processing updates.</exception>
    private static async Task RunPlanAsync(ScriptSpecification script, IProgressReporter progressReporter, TablePlan plan,
        RelationalDriver driver, CancellationToken cancellationToken)
    {
        // Static pages only read row keys. Mixed tables retain the original-row
        // context required by generators.
        if (plan.HasOnlyStaticValues)
        {
            await new StaticValueUpdater(driver).RunAsync(script, progressReporter, plan, cancellationToken);
            return;
        }

        await using var readerConnection = driver.CreateConnection(script.Database.ConnectionString);
        await using var updaterConnection = driver.CreateConnection(script.Database.ConnectionString);
        await readerConnection.OpenAsync(cancellationToken);
        await updaterConnection.OpenAsync(cancellationToken);
        // PostgreSQL MVCC allows a streaming reader alongside a table transaction.
        // Preserve SQL Server autocommit to avoid updater lock escalation blocking the reader.
        await using var transaction = script.Database.DatabaseType == DatabaseType.PostgreSQL
            ? await updaterConnection.BeginTransactionAsync(cancellationToken) : null;
        await using var select = readerConnection.CreateCommand();
        var columns = plan.Keys.Concat(plan.Rules.Select(r => r.Column))
            .Concat(plan.Rules.SelectMany(r => r.Parameters.Values.Where(p => p.Column is not null).Select(p => p.Column!)))
            .Distinct(StringComparer.Ordinal).ToArray();
        select.CommandText = $"SELECT {string.Join(", ", columns.Select(driver.QuoteIdentifier))} FROM {driver.QuoteTable(plan.Table)}";
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        var batchSize = driver.GetEffectiveBatchSize(script.BatchSize, plan.Keys.Length + plan.Rules.Length);
        var dataTypeNames = Enumerable.Range(0, plan.Keys.Length + plan.Rules.Length).Select(reader.GetDataTypeName).ToArray();
        var pending = new List<(int[] Columns, object?[] Values)>();
        var step = 0;
        var buffered = 0;
        
        while (await reader.ReadAsync(cancellationToken))
        {
            // Snapshot dependencies before any generator runs; rule order cannot change their values.
            var originalValues = columns.Select((column, i) => (column, value: (object?)reader.GetValue(i)))
                .ToDictionary(p => p.column, p => p.value, StringComparer.Ordinal);
            var contexts = plan.Rules.Select(r => GeneratorParameterResolver.Resolve(r, originalValues)).ToArray();
            var changedColumns = new List<int>();
            var values = plan.Keys.Select(key => originalValues[key]).ToList();
            for (var i = 0; i < plan.Generators.Length; i++)
            {
                var originalValue = originalValues[plan.Rules[i].Column];
                if (!plan.Rules[i].HasValue && (originalValue is null or DBNull or string { Length: 0 })) continue;
                changedColumns.Add(i);
                values.Add(plan.Rules[i].HasValue
                    ? BogusValueConverter.ToDatabaseValue(plan.Rules[i].Value)
                    : await GeneratorInvocation.GenerateAsync(plan.Generators[i]!, contexts[i], cancellationToken));
            }
            if (changedColumns.Count > 0) pending.Add((changedColumns.ToArray(), values.ToArray()));
            buffered++;
            if (buffered == batchSize) await FlushAsync();
        }
        if (buffered > 0) await FlushAsync();

        async Task FlushAsync()
        {
            // A skipped column must not appear in SET, even with its unchanged value:
            // column-specific triggers should not fire for preserved NULL/empty cells.
            foreach (var group in pending.GroupBy(row => string.Join(",", row.Columns)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var indices = group.First().Columns;
                var rows = group.Select(row => row.Values).ToArray();
                var types = dataTypeNames.Take(plan.Keys.Length)
                    .Concat(indices.Select(i => dataTypeNames[plan.Keys.Length + i])).ToArray();
                await using var update = driver.CreateUpdateBatch(updaterConnection, plan.Table, plan.Keys,
                    indices.Select(i => plan.Rules[i].Column).ToArray(), rows, types);
                update.Transaction = transaction;
                await BatchUpdateExecutor.ExecuteAsync(update, plan.Table.Name, rows.Length, cancellationToken);
            }
            step += buffered;
            await progressReporter.ReportProgressAsync(plan.Table.Name, step, plan.Table.RowCount, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            pending.Clear();
            buffered = 0;
        }
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    private static async Task TruncateTablesAsync(ScriptSpecification script, IProgressReporter progressReporter, DatabaseDescription database, RelationalDriver driver, CancellationToken cancellationToken)
    {
        var truncateTables = LoadTruncateTables(script, database);

        if (truncateTables.Any())
        {
            await using var connection = driver.CreateConnection(script.Database.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            foreach (var sql in driver.CreateTruncateCommands(truncateTables))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            foreach (var table in truncateTables)
                await progressReporter.ReportProgressAsync(table.Name, 1, 1, cancellationToken);
        }
    }

    /// <summary>
    /// Loads and validates the table plans based on the given script specification, database description,
    /// and relational driver. Validates all target rules and configured keys before processing any table updates.
    /// </summary>
    /// <param name="script">The script specification containing the database configuration and transformation rules.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <param name="database">The description of the target database including metadata and tables.</param>
    /// <param name="driver">The relational driver used to interact with the database.</param>
    /// <returns>A list of validated table plans including rules, keys, and data generators.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a validation step fails during plan evaluation.</exception>
    private async Task<List<TablePlan>> LoadAndValidatePlans(ScriptSpecification script, CancellationToken cancellationToken,
        DatabaseDescription database, RelationalDriver driver)
    {
        var plans = new List<TablePlan>();

        // Validate every target before making the first change.
        foreach (var group in script.Rules.GroupBy(r => r.Table))
        {
            var matches = database.Tables.Where(t => t.Name == group.Key).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException($"Table target is missing or ambiguous: {group.Key}");
            var table = matches[0];
            var keys = table.Fields.Where(f => f.IsPrimaryKey)
                .OrderBy(f => f.PrimaryKeyOrdinal ?? int.MaxValue).Select(f => f.Path).ToArray();
            if (script.TableKeys.TryGetValue(table.Name, out var configuredKey))
            {
                if (keys.Length > 0)
                    throw new InvalidOperationException($"Table already has a primary key: {table.Name}. Remove its tableKeys entry.");
                if (!table.Fields.Any(f => f.Path == configuredKey))
                    throw new InvalidOperationException($"Unknown configured row key: {table.Name}.{configuredKey}");
                keys = [configuredKey];
            }
            var rules = group.ToArray();
            if (rules.Select(r => r.Column).Distinct().Count() != rules.Length)
                throw new InvalidOperationException($"Duplicate column rules for {table.Name}");
            foreach (var rule in rules)
            {
                rule.ValidateValue();
                var field = table.Fields.SingleOrDefault(f => f.Path == rule.Column)
                            ?? throw new InvalidOperationException($"Unknown column: {table.Name}.{rule.Column}");
                if (field.IsPrimaryKey) throw new InvalidOperationException($"Cannot anonymize primary key: {table.Name}.{field.Path}");
                if (keys.Contains(field.Path))
                    throw new InvalidOperationException($"Cannot anonymize configured row key: {table.Name}.{field.Path}");
            }
            var staticOnly = rules.All(r => r.HasValue);
            if (!staticOnly && keys.Length == 0)
                throw new InvalidOperationException($"Table has no primary key: {table.Name}. Configure a unique, non-null column in tableKeys.");
            if (staticOnly && keys.Length > 0)
                driver.GetEffectiveBatchSize(script.BatchSize, keys.Length, rules.Length);
            else
                driver.GetEffectiveBatchSize(script.BatchSize, rules.Length + keys.Length);
            var generators = rules.Select(r => r.HasValue ? null : dataGeneratorFactory.Create(r.Generator ?? nameof(GeneratorType.Name))).ToArray();
            for (var i = 0; i < rules.Length; i++)
                if (generators[i] is { } generator)
                    GeneratorParameterResolver.Validate(rules[i], table, generator);
            plans.Add(new TablePlan(table, rules, keys, generators));
        }
        
        // Check all configured keys before any table is updated.
        foreach (var plan in plans.Where(p => script.TableKeys.ContainsKey(p.Table.Name)))
            await ValidateConfiguredKeyAsync(driver, script.Database.ConnectionString, plan.Table, plan.Keys[0], cancellationToken);
        return plans;
    }

    private static List<TableDescription> LoadTruncateTables(ScriptSpecification script, DatabaseDescription database)
    {
        var truncateTables = new List<TableDescription>();
        var seenTruncations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in script.TruncateTables)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("truncateTables requires non-empty table names.");
            if (!seenTruncations.Add(name))
                throw new InvalidOperationException($"Duplicate truncateTables entry: {name}");
            var matches = database.Tables.Where(t => t.Name == name).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException($"Table target is missing or ambiguous: {name}");
            if (script.Rules.Any(r => r.Table == name))
                throw new InvalidOperationException($"Table cannot appear in both truncateTables and rules: {name}");
            truncateTables.Add(matches[0]);
        }

        return truncateTables;
    }

    private static async Task ValidateConfiguredKeyAsync(RelationalDriver driver, string connectionString,
        TableDescription table, string key, CancellationToken cancellationToken)
    {
        await using var connection = driver.CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var column = driver.QuoteIdentifier(key);
        // GROUP BY follows the database's own equality/collation semantics, like UPDATE.
        command.CommandText = $"SELECT {column} FROM {driver.QuoteTable(table)} GROUP BY {column} HAVING {column} IS NULL OR COUNT(*) > 1";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException($"Configured row key must be unique and non-null: {table.Name}.{key}");
    }

    public Task<bool> IsImpactedAsync(ScriptSpecification script, TableDescription table, FieldDescription field, CancellationToken cancellationToken = default)
        => Task.FromResult(!field.IsPrimaryKey
            && !(script.TableKeys.TryGetValue(table.Name, out var key) && key == field.Path)
            && script.Rules.Any(r => r.Table == table.Name && r.Column == field.Path));
}
