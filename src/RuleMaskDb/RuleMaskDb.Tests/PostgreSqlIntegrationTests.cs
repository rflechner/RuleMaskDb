using System.Collections.Immutable;
using Npgsql;
using RuleMaskDb.Generators;
using RuleMaskDb.ScriptDom;
using RuleMaskDb.Yaml;

namespace RuleMaskDb.Tests;

[NonParallelizable]
public class PostgreSqlIntegrationTests
{
    private string connectionString = null!;
    private string schema = null!;
    private string table = null!;
    private readonly RelationalDriver driver = new(DatabaseType.PostgreSQL);

    [SetUp]
    public async Task SetUp()
    {
        connectionString = Environment.GetEnvironmentVariable("RULEMASK_TEST_POSTGRES")!;
        if (string.IsNullOrEmpty(connectionString)) Assert.Ignore("Set RULEMASK_TEST_POSTGRES to an explicitly disposable PostgreSQL database.");
        schema = "rulemask." + Guid.NewGuid().ToString("N");
        table = driver.QuoteIdentifier(schema) + "." + driver.QuoteIdentifier("People\" Records");
        await Execute($"CREATE SCHEMA {driver.QuoteIdentifier(schema)}");
        await Execute($"""
            CREATE TABLE {table} (
              "Tenant" integer, "Id" integer, "First.Name" text, "Last""Name" text,
              "untouched" text, "First.name" text, PRIMARY KEY ("Tenant", "Id"));
            INSERT INTO {table} VALUES
              (1, 7, 'Alice', 'Smith', 'keep-1', 'case-1'),
              (2, 7, 'Bob', 'Jones', 'keep-2', 'case-2'),
              (2, 8, NULL, 'Brown', 'keep-3', 'case-3');
            """);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (schema is not null) await Execute($"DROP SCHEMA {driver.QuoteIdentifier(schema)} CASCADE");
    }

    [Test]
    public async Task Yaml_to_engine_preserves_composite_keys_untargeted_columns_and_row_count()
    {
        var script = await LoadYaml();
        var description = await new DatabaseAnalyzer().DescribeDatabaseAsync(script.Database);
        var found = description.Tables.Single(t => t.Name == schema + ".People\" Records");
        Assert.That(found.RowCount, Is.EqualTo(3));
        Assert.That(found.Fields.Where(f => f.IsPrimaryKey).Select(f => f.Path), Is.EquivalentTo(new[] { "Tenant", "Id" }));
        await new ScriptRunner(new DatabaseAnalyzer(), new FixedFactory()).RunAsync(script, new Progress());
        Assert.That(await ReadRows(), Is.EqualTo(new[] {
            "1|7|Fake' first|Fake last|keep-1|case-1",
            "2|7|Fake' first|Fake last|keep-2|case-2",
            "2|8||Fake last|keep-3|case-3" }));
    }

    [TestCase(null, null)]
    [TestCase("", "")]
    [TestCase(null, "")]
    [TestCase(null, "Smith")]
    [TestCase("", "Smith")]
    [TestCase("Alice", null)]
    [TestCase("Alice", "")]
    [TestCase("Alice", "Smith")]
    [TestCase(" ", "\t")]
    public async Task Null_and_empty_fields_are_preserved_without_generating_or_updating_them(string? first, string? last)
    {
        await Execute($"DELETE FROM {table} WHERE NOT (\"Tenant\" = 1 AND \"Id\" = 7)");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var seed = new NpgsqlCommand($"UPDATE {table} SET \"First.Name\" = @first, \"Last\"\"Name\" = @last", connection))
        {
            seed.Parameters.AddWithValue("first", NpgsqlTypes.NpgsqlDbType.Text, (object?)first ?? DBNull.Value);
            seed.Parameters.AddWithValue("last", NpgsqlTypes.NpgsqlDbType.Text, (object?)last ?? DBNull.Value);
            await seed.ExecuteNonQueryAsync();
        }
        // Reject any assignment to a skipped column, even if it would write the same value.
        await Execute($"""
            CREATE FUNCTION {driver.QuoteIdentifier(schema)}.reject_update() RETURNS trigger
            LANGUAGE plpgsql AS 'BEGIN RAISE EXCEPTION ''Skipped field was updated''; END';
            """);
        var expectedCalls = 0;
        foreach (var (column, value) in new[] { ("First.Name", first), ("Last\"Name", last) })
        {
            if (string.IsNullOrEmpty(value))
                await Execute($"""
                    CREATE TRIGGER {driver.QuoteIdentifier("reject_" + column)} BEFORE UPDATE OF {driver.QuoteIdentifier(column)}
                    ON {table} FOR EACH ROW EXECUTE FUNCTION {driver.QuoteIdentifier(schema)}.reject_update();
                    """);
            else
                expectedCalls++;
        }
        var factory = new CountingFactory();
        var progress = new RecordingProgress();
        await new ScriptRunner(new DatabaseAnalyzer(), factory).RunAsync(await LoadYaml(), progress);

        await using var command = new NpgsqlCommand($"SELECT \"First.Name\", \"Last\"\"Name\", \"untouched\" FROM {table}", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.That(await reader.ReadAsync(), Is.True);
        Assert.That(reader.GetValue(0), Is.EqualTo(first is null ? (object)DBNull.Value : first.Length == 0 ? "" : "masked"));
        Assert.That(reader.GetValue(1), Is.EqualTo(last is null ? (object)DBNull.Value : last.Length == 0 ? "" : "masked"));
        Assert.That(reader.GetString(2), Is.EqualTo("keep-1"));
        Assert.That(await reader.ReadAsync(), Is.False);
        Assert.That(factory.Calls, Is.EqualTo(expectedCalls));
        Assert.That(progress.Reports, Is.EqualTo(new[] { (1, 1) }));
    }

    [Test]
    public async Task Mapped_parameters_use_original_source_even_when_source_is_anonymized_first()
    {
        var script = await LoadYaml();
        var source = new Rule(schema + ".People\" Records", "First.name", null, GeneratorType.Name);
        var target = script.Rules[0] with
        {
            Parameters = System.Collections.Immutable.ImmutableDictionary<string, GeneratorParameter>.Empty.Add("gender",
                new GeneratorParameter("First.name",
                    new Dictionary<string, string> { ["case-1"] = "Male", ["case-2"] = "Female" }
                        .ToImmutableDictionary(), "Unknown"))
        };
        script = script with { Rules = System.Collections.Immutable.ImmutableArray.Create(source, target) };
        await new ScriptRunner(new DatabaseAnalyzer(), new ContextFactory()).RunAsync(script, new Progress());
        Assert.That(await ReadRows(), Is.EqualTo(new[] {
            "1|7|Male|Smith|keep-1|changed",
            "2|7|Female|Jones|keep-2|changed",
            "2|8||Brown|keep-3|changed" }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Culture_parameters_use_original_row_for_direct_and_mapped_bindings(bool mapped)
    {
        await Execute($"UPDATE {table} SET \"First.name\" = CASE WHEN \"Tenant\" = 1 THEN 'fr-FR' ELSE 'en-GB' END");
        var script = await LoadYaml();
        var source = new Rule(schema + ".People\" Records", "First.name", null, GeneratorType.Name);
        var binding = new GeneratorParameter("First.name",
            mapped ? new Dictionary<string, string> { ["fr-FR"] = "fr-CA", ["en-GB"] = "de-DE" }.ToImmutableDictionary() : null,
            "fr-FR");
        var target = script.Rules[0] with
        {
            Generator = "CultureEcho",
            Parameters = ImmutableDictionary<string, GeneratorParameter>.Empty.Add("culture", binding)
        };
        script = script with { Rules = ImmutableArray.Create(source, target) };
        await new ScriptRunner(new DatabaseAnalyzer(), new ContextFactory()).RunAsync(script, new Progress());
        Assert.That(await ReadRows(), Is.EqualTo(new[] {
            $"1|7|{(mapped ? "fr-CA" : "fr-FR")}|Smith|keep-1|changed",
            $"2|7|{(mapped ? "de-DE" : "en-GB")}|Jones|keep-2|changed",
            "2|8||Brown|keep-3|changed" }));
    }

    [TestCase("Id")]
    [TestCase("missing")]
    public async Task Invalid_column_or_primary_key_rule_fails_before_writing(string column)
    {
        var before = await ReadRows();
        var script = await LoadYaml();
        script = script with { Rules = script.Rules.Add(new Rule(schema + ".People\" Records", column, null, GeneratorType.Name)) };
        Assert.ThrowsAsync<InvalidOperationException>(async () => await new ScriptRunner(new DatabaseAnalyzer(), new FixedFactory()).RunAsync(script, new Progress()));
        Assert.That(await ReadRows(), Is.EqualTo(before));
    }

    [Test]
    public async Task Generator_failure_rolls_back_current_table()
    {
        var before = await ReadRows();
        var script = await LoadYaml();
        Assert.ThrowsAsync<InvalidOperationException>(async () => await new ScriptRunner(new DatabaseAnalyzer(), new FailingFactory()).RunAsync(script, new Progress()));
        Assert.That(await ReadRows(), Is.EqualTo(before));
    }

    [TestCase("untouched")]
    [TestCase("Last\"Name")]
    public async Task Configured_key_supports_tables_without_primary_keys(string key)
    {
        await Execute($"ALTER TABLE {table} DROP CONSTRAINT {driver.QuoteIdentifier("People\" Records_pkey")}");
        var script = await LoadYaml();
        script = script with
        {
            Rules = script.Rules.Where(r => r.Column != key).ToImmutableArray(),
            TableKeys = ImmutableDictionary<string, string>.Empty.Add(schema + ".People\" Records", key)
        };
        await new ScriptRunner(new DatabaseAnalyzer(), new FixedFactory()).RunAsync(script, new Progress());
        Assert.That(await ReadRows(), Is.EqualTo(new[] {
            $"1|7|Fake' first|{(key == "untouched" ? "Fake last" : "Smith")}|keep-1|case-1",
            $"2|7|Fake' first|{(key == "untouched" ? "Fake last" : "Jones")}|keep-2|case-2",
            $"2|8||{(key == "untouched" ? "Fake last" : "Brown")}|keep-3|case-3" }));
        var description = await new DatabaseAnalyzer().DescribeDatabaseAsync(script.Database);
        Assert.That(description.Tables.Single(t => t.Name == schema + ".People\" Records").Fields.Any(f => f.IsPrimaryKey), Is.False);
    }

    [TestCase("Id")]
    [TestCase("First.Name")]
    public async Task Duplicate_or_null_configured_key_fails_before_writing(string key)
    {
        await Execute($"ALTER TABLE {table} DROP CONSTRAINT {driver.QuoteIdentifier("People\" Records_pkey")}");
        var script = await LoadYaml();
        script = script with
        {
            Rules = script.Rules.Where(r => r.Column != key).ToImmutableArray(),
            TableKeys = ImmutableDictionary<string, string>.Empty.Add(schema + ".People\" Records", key)
        };
        var before = await ReadRows();
        Assert.That(async () => await new ScriptRunner(new DatabaseAnalyzer(), new FixedFactory()).RunAsync(script, new Progress()),
            Throws.InvalidOperationException.With.Message.Contains("unique and non-null"));
        Assert.That(await ReadRows(), Is.EqualTo(before));
    }

    [Test]
    public async Task Invalid_configured_key_in_later_table_prevents_all_writes()
    {
        var otherTable = driver.QuoteIdentifier(schema) + ".\"NoKey\"";
        await Execute($"CREATE TABLE {otherTable} (\"Code\" integer, \"Name\" text); INSERT INTO {otherTable} VALUES (1, 'A'), (1, 'B')");
        var script = await LoadYaml();
        script = script with
        {
            Rules = script.Rules.Add(new Rule(schema + ".NoKey", "Name", null, GeneratorType.Name)),
            TableKeys = ImmutableDictionary<string, string>.Empty.Add(schema + ".NoKey", "Code")
        };
        var before = await ReadRows();
        Assert.That(async () => await new ScriptRunner(new DatabaseAnalyzer(), new FixedFactory()).RunAsync(script, new Progress()),
            Throws.InvalidOperationException.With.Message.Contains("unique and non-null"));
        Assert.That(await ReadRows(), Is.EqualTo(before));
    }

    [Test]
    public async Task Empty_table_with_configured_key_succeeds()
    {
        await Execute($"ALTER TABLE {table} DROP CONSTRAINT {driver.QuoteIdentifier("People\" Records_pkey")}; DELETE FROM {table}");
        var script = await LoadYaml();
        script = script with { TableKeys = ImmutableDictionary<string, string>.Empty.Add(schema + ".People\" Records", "Id") };
        await new ScriptRunner(new DatabaseAnalyzer(), new FixedFactory()).RunAsync(script, new Progress());
        Assert.That(await ReadRows(), Is.Empty);
    }

    [Test]
    public async Task Missing_primary_key_is_rejected()
    {
        await Execute($"ALTER TABLE {table} DROP CONSTRAINT {driver.QuoteIdentifier("People\" Records_pkey")}");
        var before = await ReadRows();
        Assert.ThrowsAsync<InvalidOperationException>(async () => await new ScriptRunner(new DatabaseAnalyzer(), new FixedFactory()).RunAsync(await LoadYaml(), new Progress()));
        Assert.That(await ReadRows(), Is.EqualTo(before));
    }

    [Test]
    public async Task Cancellation_after_first_update_rolls_back_current_table()
    {
        var before = await ReadRows();
        using var source = new CancellationTokenSource();
        var script = await LoadYaml();
        Assert.CatchAsync<OperationCanceledException>(async () => await new ScriptRunner(new DatabaseAnalyzer(), new FixedFactory())
            .RunAsync(script, new CancellingProgress(source), source.Token));
        Assert.That(await ReadRows(), Is.EqualTo(before));
    }

    private sealed class CancellingProgress(CancellationTokenSource source) : IProgressReporter
    {
        public Task ReportProgressAsync(string tableName, int step, int totalStepCount, CancellationToken cancellationToken = default)
        {
            source.Cancel();
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task Truncate_only_supports_tables_without_keys_and_preserves_other_tables()
    {
        await Execute($"ALTER TABLE {table} DROP CONSTRAINT {driver.QuoteIdentifier("People\" Records_pkey")}");
        var other = driver.QuoteIdentifier(schema) + ".untargeted";
        await Execute($"CREATE TABLE {other} (value text); INSERT INTO {other} VALUES ('keep')");
        var script = (await LoadYaml()) with { Rules = [], TruncateTables = [schema + ".People\" Records"] };
        await new ScriptRunner(new DatabaseAnalyzer(), new FixedFactory()).RunAsync(script, new Progress());
        Assert.That(await ReadRows(), Is.Empty);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT value FROM {other}", connection);
        Assert.That(await command.ExecuteScalarAsync(), Is.EqualTo("keep"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Truncate_related_tables_requires_every_referencing_table(bool includeChild)
    {
        var child = driver.QuoteIdentifier(schema) + ".events";
        await Execute($"CREATE TABLE {child} (tenant integer, id integer, FOREIGN KEY (tenant, id) REFERENCES {table}); INSERT INTO {child} VALUES (1, 7)");
        var script = (await LoadYaml()) with
        {
            Rules = [],
            TruncateTables = includeChild
                ? [schema + ".People\" Records", schema + ".events"]
                : [schema + ".People\" Records"]
        };
        var runner = new ScriptRunner(new DatabaseAnalyzer(), new FixedFactory());
        if (includeChild)
        {
            await runner.RunAsync(script, new Progress());
            Assert.That(await ReadRows(), Is.Empty);
        }
        else
        {
            Assert.ThrowsAsync<PostgresException>(() => runner.RunAsync(script, new Progress()));
            Assert.That((await ReadRows()).Length, Is.EqualTo(3));
        }
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT COUNT(*) FROM {child}", connection);
        Assert.That(await command.ExecuteScalarAsync(), Is.EqualTo(includeChild ? 0L : 1L));
    }

    [Test]
    public async Task Truncate_failure_rolls_back_all_targets()
    {
        var rejected = driver.QuoteIdentifier(schema) + ".rejected";
        await Execute($"""
            CREATE TABLE {rejected} (value text);
            INSERT INTO {rejected} VALUES ('keep');
            CREATE FUNCTION {driver.QuoteIdentifier(schema)}.reject_truncate() RETURNS trigger
            LANGUAGE plpgsql AS 'BEGIN RAISE EXCEPTION ''Truncation rejected''; END';
            CREATE TRIGGER reject_truncate AFTER TRUNCATE ON {rejected}
            FOR EACH STATEMENT EXECUTE FUNCTION {driver.QuoteIdentifier(schema)}.reject_truncate();
            """);
        var script = (await LoadYaml()) with
        {
            Rules = [], TruncateTables = [schema + ".People\" Records", schema + ".rejected"]
        };
        Assert.ThrowsAsync<PostgresException>(() =>
            new ScriptRunner(new DatabaseAnalyzer(), new FixedFactory()).RunAsync(script, new Progress()));
        Assert.That((await ReadRows()).Length, Is.EqualTo(3));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT value FROM {rejected}", connection);
        Assert.That(await command.ExecuteScalarAsync(), Is.EqualTo("keep"));
    }

    [Test]
    public async Task Truncate_preserves_postgresql_identity_and_combines_with_anonymization()
    {
        var events = driver.QuoteIdentifier(schema) + ".events";
        await Execute($"CREATE TABLE {events} (id integer GENERATED ALWAYS AS IDENTITY); INSERT INTO {events} DEFAULT VALUES");
        var script = (await LoadYaml()) with { TruncateTables = [schema + ".events"] };
        await new ScriptRunner(new DatabaseAnalyzer(), new FixedFactory()).RunAsync(script, new Progress());
        Assert.That((await ReadRows())[0], Does.Contain("Fake' first"));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var count = new NpgsqlCommand($"SELECT COUNT(*) FROM {events}", connection);
        Assert.That(await count.ExecuteScalarAsync(), Is.EqualTo(0L));
        await using var insert = new NpgsqlCommand($"INSERT INTO {events} DEFAULT VALUES RETURNING id", connection);
        Assert.That(await insert.ExecuteScalarAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task Invalid_configured_key_prevents_truncation()
    {
        var events = driver.QuoteIdentifier(schema) + ".events";
        await Execute($"CREATE TABLE {events} (value text); INSERT INTO {events} VALUES ('keep')");
        await Execute($"ALTER TABLE {table} DROP CONSTRAINT {driver.QuoteIdentifier("People\" Records_pkey")}");
        var script = (await LoadYaml()) with
        {
            TruncateTables = [schema + ".events"],
            TableKeys = ImmutableDictionary<string, string>.Empty.Add(schema + ".People\" Records", "Tenant")
        };
        Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ScriptRunner(new DatabaseAnalyzer(), new FixedFactory()).RunAsync(script, new Progress()));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT value FROM {events}", connection);
        Assert.That(await command.ExecuteScalarAsync(), Is.EqualTo("keep"));
    }

    [TestCase("redacted' ; --")]
    [TestCase("")]
    [TestCase(null)]
    public async Task Static_values_replace_every_cell_without_creating_generators(string? value)
    {
        await Execute($"UPDATE {table} SET \"First.Name\" = '' WHERE \"Tenant\" = 1");
        var script = await LoadYaml();
        script = script with
        {
            Rules = [new Rule(schema + ".People\" Records", "First.Name", null, (string?)null) { Value = value }]
        };
        await new ScriptRunner(new DatabaseAnalyzer(), new RejectingFactory()).RunAsync(script, new Progress());
        Assert.That(await ReadRows(), Is.EqualTo(new[] {
            $"1|7|{value}|Smith|keep-1|case-1",
            $"2|7|{value}|Jones|keep-2|case-2",
            $"2|8|{value}|Brown|keep-3|case-3" }));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT \"First.Name\" FROM {table}", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            Assert.That(reader.GetValue(0), Is.EqualTo((object?)value ?? DBNull.Value));
    }

    [Test]
    public async Task Yaml_static_types_and_generators_can_be_mixed()
    {
        await Execute($"ALTER TABLE {table} ADD COLUMN enabled boolean, ADD COLUMN score integer, ADD COLUMN amount numeric");
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, $"""
                database:
                  type: PostgreSQL
                  connectionString: '{connectionString.Replace("'", "''")}'
                rules:
                  - table: '{schema}.People" Records'
                    column: enabled
                    value: false
                  - table: '{schema}.People" Records'
                    column: score
                    value: 42
                  - table: '{schema}.People" Records'
                    column: amount
                    value: 12.5
                  - table: '{schema}.People" Records'
                    column: First.Name
                    generator: FirstName
                """);
            var script = await new YamlScriptDomProvider(path).LoadScriptAsync();
            await new ScriptRunner(new DatabaseAnalyzer(), new FixedFactory()).RunAsync(script, new Progress());
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"SELECT enabled, score, amount FROM {table}", connection);
            await using var reader = await command.ExecuteReaderAsync();
            var count = 0;
            while (await reader.ReadAsync())
            {
                Assert.That(reader.GetBoolean(0), Is.False);
                Assert.That(reader.GetInt32(1), Is.EqualTo(42));
                Assert.That(reader.GetDecimal(2), Is.EqualTo(12.5m));
                count++;
            }
            Assert.That(count, Is.EqualTo(3));
        }
        finally { File.Delete(path); }
    }

    private sealed class RejectingFactory : IDataGeneratorFactory
    {
        public IDataGenerator Create(GeneratorType type) => throw new AssertionException("Static rules must not create a generator.");
        public Sdk.IDataGenerator Create(string name) => throw new AssertionException("Static rules must not create a generator.");
    }

    private async Task<ScriptSpecification> LoadYaml()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, $"""
                database:
                  type: PostgreSQL
                  connectionString: '{connectionString.Replace("'", "''")}'
                rules:
                  - table: '{schema}.People" Records'
                    column: First.Name
                    generator: FirstName
                  - table: '{schema}.People" Records'
                    column: Last"Name
                    generator: Name
                """);
            return await new YamlScriptDomProvider(path).LoadScriptAsync();
        }
        finally { File.Delete(path); }
    }

    private async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string[]> ReadRows()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT * FROM {table} ORDER BY \"Tenant\", \"Id\"", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue)));
        return rows.ToArray();
    }

    private sealed class CountingFactory : IDataGeneratorFactory, Sdk.IDataGenerator
    {
        public int Calls { get; private set; }
        public IDataGenerator Create(GeneratorType type) => throw new InvalidOperationException("Use the SDK generator path");
        public Sdk.IDataGenerator Create(string name) => this;
        public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<object>("masked");
        }
    }
    private sealed class RecordingProgress : IProgressReporter
    {
        public List<(int Step, int Total)> Reports { get; } = [];
        public Task ReportProgressAsync(string tableName, int step, int totalStepCount, CancellationToken cancellationToken = default)
        {
            Reports.Add((step, totalStepCount));
            return Task.CompletedTask;
        }
    }
    private sealed class ContextFactory : IDataGeneratorFactory
    {
        public IDataGenerator Create(GeneratorType type) => new FixedGenerator("changed");
        public Sdk.IDataGenerator Create(string name)
            => name switch
            {
                "FirstName" => new GenderEcho(),
                "CultureEcho" => new CultureEcho(),
                _ => new FixedGenerator("changed")
            };
    }
    private sealed class CultureEcho : Sdk.IContextualDataGenerator
    {
        public void ValidateParameters(IReadOnlyDictionary<string, IReadOnlyCollection<string>> possibleValues) { }
        public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Expected row context");
        public Task<object> GenerateValueAsync(Sdk.GenerationContext context, CancellationToken cancellationToken = default)
            => Task.FromResult<object>(context.GetCulture()!.Name);
    }
    private sealed class GenderEcho : Sdk.IContextualDataGenerator
    {
        public void ValidateParameters(IReadOnlyDictionary<string, IReadOnlyCollection<string>> possibleValues) { }
        public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Expected row context");
        public Task<object> GenerateValueAsync(Sdk.GenerationContext context, CancellationToken cancellationToken = default)
            => Task.FromResult<object>(context.GetEnum("gender", Sdk.Gender.Unknown).ToString());
    }
    private sealed class FixedFactory : IDataGeneratorFactory
    {
        public IDataGenerator Create(GeneratorType type) => new FixedGenerator(type == GeneratorType.FirstName ? "Fake' first" : "Fake last");
    }
    private sealed class FixedGenerator(string value) : IDataGenerator
    {
        public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default) => Task.FromResult<object>(value);
    }
    private sealed class FailingFactory : IDataGeneratorFactory, IDataGenerator
    {
        private int calls;
        public IDataGenerator Create(GeneratorType type) => this;
        public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
            => ++calls > 2 ? throw new InvalidOperationException("Deliberate failure after one row") : Task.FromResult<object>("temporary");
    }
    private sealed class Progress : IProgressReporter
    {
        public Task ReportProgressAsync(string tableName, int step, int totalStepCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
