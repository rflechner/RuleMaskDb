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
        script.ValidateBeforeTableSql();

        var database = await databaseAnalyzer.DescribeDatabaseAsync(script.Database);
        var driver = new RelationalDriver(script.Database.DatabaseType);
        var truncateTables = LoadTruncateTables(script, database);
        var plans = await LoadAndValidatePlans(script, cancellationToken, database, driver);

        await TruncateTablesAsync(script, progressReporter, truncateTables, driver, cancellationToken);

        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var preparedPlan = script.BeforeTableSql.IsEmpty
                ? plan
                : await new TablePreparationExecutor(driver).PrepareAsync(
                    script.Database.ConnectionString, plan, cancellationToken);

            await RunPlanAsync(script, progressReporter, preparedPlan, driver, cancellationToken);
        }
    }

    /// <summary>
    /// Selects key-only updates for static plans and original-row batches for plans containing generators.
    /// </summary>
    private static Task RunPlanAsync(ScriptSpecification script, IProgressReporter progressReporter, TablePlan plan,
        RelationalDriver driver, CancellationToken cancellationToken)
    {
        if (plan.HasOnlyStaticValues)
            return new StaticValueUpdater(driver).RunAsync(script, progressReporter, plan, cancellationToken);

        return new GeneratedValueUpdater(driver, plan).RunAsync(script, progressReporter, cancellationToken);
    }

    private static async Task TruncateTablesAsync(ScriptSpecification script, IProgressReporter progressReporter,
        IReadOnlyList<TableDescription> truncateTables, RelationalDriver driver, CancellationToken cancellationToken)
    {
        if (truncateTables.Count == 0) return;

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
            plans.Add(new TablePlan(table, rules, keys, generators)
            {
                ConfiguredRowKey = configuredKey,
                PreparationSql = script.BeforeTableSql.GetValueOrDefault(table.Name)
            });
        }
        
        // Check all configured keys before any table is updated.
        var keyValidator = new ConfiguredRowKeyValidator(driver);
        foreach (var plan in plans.Where(plan => plan.ConfiguredRowKey is not null))
            await keyValidator.ValidateAsync(script.Database.ConnectionString, plan, cancellationToken);

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

    public Task<bool> IsImpactedAsync(ScriptSpecification script, TableDescription table, FieldDescription field, CancellationToken cancellationToken = default)
        => Task.FromResult(!field.IsPrimaryKey
            && !(script.TableKeys.TryGetValue(table.Name, out var key) && key == field.Path)
            && script.Rules.Any(r => r.Table == table.Name && r.Column == field.Path));
}
