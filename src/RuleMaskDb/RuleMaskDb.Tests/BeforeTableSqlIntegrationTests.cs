using System.Collections.Immutable;
using System.Data.Common;
using RuleMaskDb.Generators;
using RuleMaskDb.ScriptDom;
using RuleMaskDb.Yaml;

namespace RuleMaskDb.Tests;

[TestFixture(DatabaseType.SqlServer)]
[TestFixture(DatabaseType.PostgreSQL)]
[NonParallelizable]
public class BeforeTableSqlIntegrationTests(DatabaseType databaseType)
{
    private readonly RelationalDriver driver = new(databaseType);
    private string connectionString = null!;
    private string? firstName;
    private string secondName = null!;
    private string first = null!;
    private string second = null!;
    private string log = null!;

    [SetUp]
    public async Task SetUp()
    {
        var variable = databaseType == DatabaseType.SqlServer ? "RULEMASK_TEST_SQLSERVER" : "RULEMASK_TEST_POSTGRES";
        connectionString = Environment.GetEnvironmentVariable(variable)!;
        if (string.IsNullOrEmpty(connectionString)) Assert.Ignore($"Set {variable} to an explicitly disposable database.");
        var prefix = "before_sql_" + Guid.NewGuid().ToString("N");
        var schema = databaseType == DatabaseType.SqlServer ? "dbo" : "public";
        firstName = schema + "." + prefix + "_first";
        secondName = schema + "." + prefix + "_second";
        first = driver.QuoteIdentifier(schema) + "." + driver.QuoteIdentifier(prefix + "_first");
        second = driver.QuoteIdentifier(schema) + "." + driver.QuoteIdentifier(prefix + "_second");
        log = driver.QuoteIdentifier(schema) + "." + driver.QuoteIdentifier(prefix + "_log");
        await Execute($"""
            CREATE TABLE {first} (id int PRIMARY KEY, name varchar(50), note varchar(50));
            CREATE TABLE {second} (id int PRIMARY KEY, name varchar(50), note varchar(50));
            CREATE TABLE {log} (marker varchar(50) PRIMARY KEY);
            INSERT INTO {first} VALUES (1, 'Alice', 'original'), (2, NULL, 'original');
            INSERT INTO {second} VALUES (1, 'Bob', 'original');
            """);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (firstName is not null)
            await Execute($"DROP TABLE IF EXISTS {first}; DROP TABLE IF EXISTS {second}; DROP TABLE IF EXISTS {log};");
    }

    [TestCase(1)]
    [TestCase(200)]
    public async Task Yaml_hooks_run_once_per_table_in_rule_order_before_reading_rows(int batchSize)
    {
        var path = Path.GetTempFileName();
        try
        {
            // Reverse mapping order: only the first appearance in rules controls execution.
            await File.WriteAllTextAsync(path, $"""
                beforeTableSql:
                  {secondName}: |
                    INSERT INTO {log} SELECT 'second' WHERE (SELECT COUNT(*) FROM {first} WHERE name = 'masked' AND note = 'redacted') = 3;
                  {firstName}: |
                    INSERT INTO {log} VALUES ('first');
                    DELETE FROM {first} WHERE id = 1;
                    UPDATE {first} SET name = 'prepared' WHERE name IS NULL;
                    INSERT INTO {first} VALUES (3, 'prepared', 'original'), (4, 'prepared', 'original');
                rules:
                  - table: {firstName}
                    column: name
                    generator: Name
                  - table: {secondName}
                    column: name
                    generator: Name
                  - table: {firstName}
                    column: note
                    value: redacted
                """);
            var script = (await new YamlScriptDomProvider(path).LoadScriptAsync()) with
            {
                Database = new(databaseType, connectionString), BatchSize = batchSize
            };
            var progress = new Progress();
            await Run(script, progress);
            Assert.That(await Scalar($"SELECT COUNT(*) FROM {log}"), Is.EqualTo(2));
            Assert.That(await Scalar($"SELECT COUNT(*) FROM {first} WHERE id IN (2, 3, 4) AND name = 'masked' AND note = 'redacted'"), Is.EqualTo(3));
            Assert.That(await Scalar($"SELECT COUNT(*) FROM {second} WHERE name = 'masked'"), Is.EqualTo(1));
            Assert.That(progress.Reports.Where(r => r.Table == firstName).Select(r => r.Total), Is.All.EqualTo(3));
            Assert.That(progress.Reports.Last(r => r.Table == firstName).Step, Is.EqualTo(3));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Hook_runs_for_an_initially_empty_table()
    {
        await Execute($"DELETE FROM {first}");
        await Run(Script($"INSERT INTO {first} VALUES (1, 'prepared', 'original');"));
        Assert.That(await Scalar($"SELECT COUNT(*) FROM {first} WHERE name = 'masked'"), Is.EqualTo(1));
    }

    [Test]
    public async Task Sql_failure_rolls_back_hook_and_stops_current_and_later_tables()
    {
        // A duplicate PK is a runtime error on both providers, after the first write.
        var script = Script($"UPDATE {first} SET name = 'prepared'; INSERT INTO {first} VALUES (1, 'duplicate', 'original');") with
        {
            Rules = [new(firstName!, "name", null, "Name"), new(secondName, "name", null, "Name")]
        };
        var exception = Assert.ThrowsAsync<InvalidOperationException>(async () => await Run(script));
        Assert.That(exception!.Message, Does.Contain(firstName));
        Assert.That(exception.InnerException, Is.InstanceOf<DbException>());
        Assert.That(await Scalar($"SELECT COUNT(*) FROM {first} WHERE name = 'Alice' OR name IS NULL"), Is.EqualTo(2));
        Assert.That(await Scalar($"SELECT COUNT(*) FROM {second} WHERE name = 'Bob'"), Is.EqualTo(1));
    }

    [Test]
    public async Task Invalid_later_rule_prevents_the_first_hook_from_running()
    {
        var script = Script($"DELETE FROM {first};") with
        {
            Rules = [new(firstName!, "name", null, "Name"), new(secondName, "missing", null, "Name")]
        };
        Assert.That(async () => await Run(script), Throws.InvalidOperationException.With.Message.Contains("Unknown column"));
        Assert.That(await Scalar($"SELECT COUNT(*) FROM {first}"), Is.EqualTo(2));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Hook_cannot_commit_duplicate_or_null_configured_keys(bool setNull)
    {
        await Execute($"DROP TABLE {first}; CREATE TABLE {first} (id int, name varchar(50), note varchar(50)); INSERT INTO {first} VALUES (1, 'Alice', 'original'), (2, 'Bob', 'original');");
        var script = Script($"UPDATE {first} SET id = {(setNull ? "NULL" : "1")};") with
        {
            TableKeys = ImmutableDictionary<string, string>.Empty.Add(firstName!, "id")
        };
        Assert.That(async () => await Run(script), Throws.InvalidOperationException.With.Message.Contains("unique and non-null"));
        Assert.That(await Scalar($"SELECT COUNT(*) FROM {first} WHERE id = 2 AND name = 'Bob'"), Is.EqualTo(1));
    }

    [Test]
    public async Task Global_truncation_finishes_before_table_hooks()
    {
        await Execute($"INSERT INTO {log} VALUES ('prepared');");
        var logName = firstName![..^"_first".Length] + "_log";
        var script = Script($"INSERT INTO {log} VALUES ('prepared');") with { TruncateTables = [logName] };
        await Run(script);
        Assert.That(await Scalar($"SELECT COUNT(*) FROM {log}"), Is.EqualTo(1));
        Assert.That(await Scalar($"SELECT COUNT(*) FROM {first} WHERE name = 'masked'"), Is.EqualTo(1));
    }

    [Test]
    public async Task Configured_keys_of_later_tables_are_rechecked_after_an_earlier_hook()
    {
        await Execute($"DROP TABLE {second}; CREATE TABLE {second} (id int, name varchar(50)); INSERT INTO {second} VALUES (1, 'Bob'), (2, 'Carol');");
        var script = Script($"UPDATE {second} SET id = 1;") with
        {
            Rules = [new(firstName!, "name", null, "Name"), new(secondName, "name", null, "Name")],
            TableKeys = ImmutableDictionary<string, string>.Empty.Add(secondName, "id")
        };
        Assert.That(async () => await Run(script), Throws.InvalidOperationException.With.Message.Contains("unique and non-null"));
        Assert.That(await Scalar($"SELECT COUNT(*) FROM {second} WHERE name IN ('Bob', 'Carol')"), Is.EqualTo(2));
    }

    [Test]
    public async Task Committed_hook_remains_when_anonymization_fails()
    {
        var script = Script($"UPDATE {first} SET note = 'prepared';");
        Assert.That(async () => await new ScriptRunner(new DatabaseAnalyzer(), new FailingFactory()).RunAsync(script, new Progress()),
            Throws.InvalidOperationException.With.Message.EqualTo("Generator failed"));
        Assert.That(await Scalar($"SELECT COUNT(*) FROM {first} WHERE note = 'prepared'"), Is.EqualTo(2));
        Assert.That(await Scalar($"SELECT COUNT(*) FROM {first} WHERE name = 'Alice' OR name IS NULL"), Is.EqualTo(2));
    }

    [Test]
    public async Task Cancellation_before_a_later_table_prevents_its_hook()
    {
        using var source = new CancellationTokenSource();
        var script = Script($"INSERT INTO {log} VALUES ('first');") with
        {
            Rules = [new(firstName!, "name", null, "Name"), new(secondName, "name", null, "Name")],
            BeforeTableSql = ImmutableDictionary<string, string>.Empty
                .Add(firstName!, $"INSERT INTO {log} VALUES ('first');")
                .Add(secondName, $"INSERT INTO {log} VALUES ('second');")
        };
        Assert.CatchAsync<OperationCanceledException>(async () => await new ScriptRunner(new DatabaseAnalyzer(), new FixedFactory())
            .RunAsync(script, new CancellingProgress(source), source.Token));
        Assert.That(await Scalar($"SELECT COUNT(*) FROM {log}"), Is.EqualTo(1));
        Assert.That(await Scalar($"SELECT COUNT(*) FROM {second} WHERE name = 'Bob'"), Is.EqualTo(1));
    }

    private ScriptSpecification Script(string sql) => new(new(databaseType, connectionString),
        [new(firstName!, "name", null, "Name")])
    {
        BeforeTableSql = ImmutableDictionary<string, string>.Empty.Add(firstName!, sql)
    };

    private Task Run(ScriptSpecification script, Progress? progress = null)
        => new ScriptRunner(new DatabaseAnalyzer(), new FixedFactory()).RunAsync(script, progress ?? new Progress());

    private async Task Execute(string sql)
    {
        await using var connection = driver.CreateConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> Scalar(string sql)
    {
        await using var connection = driver.CreateConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private sealed class FixedFactory : IDataGeneratorFactory, IDataGenerator
    {
        public IDataGenerator Create(GeneratorType type) => this;
        public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default) => Task.FromResult<object>("masked");
    }

    private sealed class FailingFactory : IDataGeneratorFactory, IDataGenerator
    {
        public IDataGenerator Create(GeneratorType type) => this;
        public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Generator failed");
    }

    private sealed class Progress : IProgressReporter
    {
        public List<(string Table, int Step, int Total)> Reports { get; } = [];
        public Task ReportProgressAsync(string tableName, int step, int totalStepCount, CancellationToken cancellationToken = default)
        {
            Reports.Add((tableName, step, totalStepCount));
            return Task.CompletedTask;
        }
    }

    private sealed class CancellingProgress(CancellationTokenSource source) : IProgressReporter
    {
        public Task ReportProgressAsync(string tableName, int step, int totalStepCount, CancellationToken cancellationToken = default)
        {
            source.Cancel();
            return Task.CompletedTask;
        }
    }
}
