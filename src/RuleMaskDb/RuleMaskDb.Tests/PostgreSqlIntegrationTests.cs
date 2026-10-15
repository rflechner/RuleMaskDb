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
