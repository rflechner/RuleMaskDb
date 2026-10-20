using System.Collections.Immutable;
using System.Data.Common;
using System.Diagnostics;
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
    private string? firstTableName;
    private string secondTableName = null!;
    private string auditTableName = null!;
    private string quotedFirstTable = null!;
    private string quotedSecondTable = null!;
    private string quotedAuditTable = null!;

    [SetUp]
    public async Task SetUp()
    {
        var connectionVariable = databaseType == DatabaseType.SqlServer ? "RULEMASK_TEST_SQLSERVER" : "RULEMASK_TEST_POSTGRES";
        connectionString = Environment.GetEnvironmentVariable(connectionVariable)!;
        if (string.IsNullOrEmpty(connectionString))
            Assert.Ignore($"Set {connectionVariable} to an explicitly disposable database.");

        var prefix = "before_sql_" + Guid.NewGuid().ToString("N");
        var schema = databaseType == DatabaseType.SqlServer ? "dbo" : "public";
        firstTableName = schema + "." + prefix + "_first";
        secondTableName = schema + "." + prefix + "_second";
        auditTableName = schema + "." + prefix + "_log";
        quotedFirstTable = driver.QuoteIdentifier(schema) + "." + driver.QuoteIdentifier(prefix + "_first");
        quotedSecondTable = driver.QuoteIdentifier(schema) + "." + driver.QuoteIdentifier(prefix + "_second");
        quotedAuditTable = driver.QuoteIdentifier(schema) + "." + driver.QuoteIdentifier(prefix + "_log");

        await ExecuteSqlAsync($"""
            CREATE TABLE {quotedFirstTable} (id int PRIMARY KEY, name varchar(50), note varchar(50));
            CREATE TABLE {quotedSecondTable} (id int PRIMARY KEY, name varchar(50), note varchar(50));
            CREATE TABLE {quotedAuditTable} (marker varchar(50) PRIMARY KEY);
            INSERT INTO {quotedFirstTable} VALUES (1, 'Alice', 'original'), (2, NULL, 'original');
            INSERT INTO {quotedSecondTable} VALUES (1, 'Bob', 'original');
            """);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (firstTableName is not null)
            await ExecuteSqlAsync($"DROP TABLE IF EXISTS {quotedFirstTable}; DROP TABLE IF EXISTS {quotedSecondTable}; DROP TABLE IF EXISTS {quotedAuditTable};");
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
                  {secondTableName}: |
                    INSERT INTO {quotedAuditTable} SELECT 'second' WHERE (SELECT COUNT(*) FROM {quotedFirstTable} WHERE name = 'masked' AND note = 'redacted') = 3;
                  {firstTableName}: |
                    INSERT INTO {quotedAuditTable} VALUES ('first');
                    DELETE FROM {quotedFirstTable} WHERE id = 1;
                    UPDATE {quotedFirstTable} SET name = 'prepared' WHERE name IS NULL;
                    INSERT INTO {quotedFirstTable} VALUES (3, 'prepared', 'original'), (4, 'prepared', 'original');
                rules:
                  - table: {firstTableName}
                    column: name
                    generator: Name
                  - table: {secondTableName}
                    column: name
                    generator: Name
                  - table: {firstTableName}
                    column: note
                    value: redacted
                """);
            var script = (await new YamlScriptDomProvider(path).LoadScriptAsync()) with
            {
                Database = new(databaseType, connectionString),
                BatchSize = batchSize
            };
            var progress = new RecordingProgress();

            await RunScriptAsync(script, progress);

            Assert.That(await CountAsync($"SELECT COUNT(*) FROM {quotedAuditTable}"), Is.EqualTo(2));
            Assert.That(await CountAsync($"SELECT COUNT(*) FROM {quotedFirstTable} WHERE id IN (2, 3, 4) AND name = 'masked' AND note = 'redacted'"), Is.EqualTo(3));
            Assert.That(await CountAsync($"SELECT COUNT(*) FROM {quotedSecondTable} WHERE name = 'masked'"), Is.EqualTo(1));
            Assert.That(progress.ForTable(firstTableName!).Select(report => report.Total), Is.All.EqualTo(3));
            Assert.That(progress.ForTable(firstTableName!).Last().Step, Is.EqualTo(3));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Hook_runs_for_an_initially_empty_table()
    {
        await ExecuteSqlAsync($"DELETE FROM {quotedFirstTable}");

        await RunScriptAsync(CreateScript($"INSERT INTO {quotedFirstTable} VALUES (1, 'prepared', 'original');"));

        Assert.That(await CountAsync($"SELECT COUNT(*) FROM {quotedFirstTable} WHERE name = 'masked'"), Is.EqualTo(1));
    }

    [Test]
    public async Task Sql_failure_rolls_back_hook_and_stops_current_and_later_tables()
    {
        // A duplicate primary key fails after the first write on both providers.
        var script = CreateScript($"""
            UPDATE {quotedFirstTable} SET name = 'prepared';
            INSERT INTO {quotedFirstTable} VALUES (1, 'duplicate', 'original');
            """) with
        {
            Rules = [new(firstTableName!, "name", null, "Name"), new(secondTableName, "name", null, "Name")]
        };

        var exception = Assert.ThrowsAsync<InvalidOperationException>(async () => await RunScriptAsync(script));

        Assert.That(exception!.Message, Does.Contain(firstTableName));
        Assert.That(exception.InnerException, Is.InstanceOf<DbException>());
        Assert.That(await CountAsync($"SELECT COUNT(*) FROM {quotedFirstTable} WHERE name = 'Alice' OR name IS NULL"), Is.EqualTo(2));
        Assert.That(await CountAsync($"SELECT COUNT(*) FROM {quotedSecondTable} WHERE name = 'Bob'"), Is.EqualTo(1));
    }

    [Test]
    public async Task Invalid_later_rule_prevents_the_first_hook_from_running()
    {
        var script = CreateScript($"DELETE FROM {quotedFirstTable};") with
        {
            Rules = [new(firstTableName!, "name", null, "Name"), new(secondTableName, "missing", null, "Name")]
        };

        Assert.That(async () => await RunScriptAsync(script), Throws.InvalidOperationException.With.Message.Contains("Unknown column"));
        Assert.That(await CountAsync($"SELECT COUNT(*) FROM {quotedFirstTable}"), Is.EqualTo(2));
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task Hook_cannot_commit_duplicate_or_null_configured_keys(bool setNull, bool staticOnly)
    {
        await RecreateFirstTableWithoutPrimaryKeyAsync();

        var script = CreateScript($"UPDATE {quotedFirstTable} SET id = {(setNull ? "NULL" : "1")};") with
        {
            TableKeys = ImmutableDictionary<string, string>.Empty.Add(firstTableName!, "id"),
            Rules = [CreateNameRule(firstTableName!, staticOnly)]
        };

        Assert.That(async () => await RunScriptAsync(script), Throws.InvalidOperationException.With.Message.Contains("unique and non-null"));
        Assert.That(await CountAsync($"SELECT COUNT(*) FROM {quotedFirstTable} WHERE id = 2 AND name = 'Bob'"), Is.EqualTo(1));
    }

    [Test]
    public async Task Global_truncation_finishes_before_table_hooks()
    {
        await ExecuteSqlAsync($"INSERT INTO {quotedAuditTable} VALUES ('prepared');");
        var script = CreateScript($"INSERT INTO {quotedAuditTable} VALUES ('prepared');") with
        {
            TruncateTables = [auditTableName]
        };

        await RunScriptAsync(script);

        Assert.That(await CountAsync($"SELECT COUNT(*) FROM {quotedAuditTable}"), Is.EqualTo(1));
        Assert.That(await CountAsync($"SELECT COUNT(*) FROM {quotedFirstTable} WHERE name = 'masked'"), Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Configured_keys_of_later_tables_are_rechecked_after_an_earlier_hook(bool staticOnly)
    {
        await ExecuteSqlAsync($"DROP TABLE {quotedSecondTable};");
        await ExecuteSqlAsync($"""
            CREATE TABLE {quotedSecondTable} (id int, name varchar(50));
            INSERT INTO {quotedSecondTable} VALUES (1, 'Bob'), (2, 'Carol');
            """);
        var script = CreateScript($"UPDATE {quotedSecondTable} SET id = 1;") with
        {
            Rules = [CreateNameRule(firstTableName!, staticOnly), CreateNameRule(secondTableName, staticOnly)],
            TableKeys = ImmutableDictionary<string, string>.Empty.Add(secondTableName, "id")
        };

        Assert.That(async () => await RunScriptAsync(script), Throws.InvalidOperationException.With.Message.Contains("unique and non-null"));
        Assert.That(await CountAsync($"SELECT COUNT(*) FROM {quotedSecondTable} WHERE name IN ('Bob', 'Carol')"), Is.EqualTo(2));
    }

    [Test]
    public async Task Committed_hook_remains_when_anonymization_fails()
    {
        var script = CreateScript($"UPDATE {quotedFirstTable} SET note = 'prepared';");

        Assert.That(async () => await new ScriptRunner(new DatabaseAnalyzer(), new FailingGeneratorFactory()).RunAsync(script, new RecordingProgress()),
            Throws.InvalidOperationException.With.Message.EqualTo("Generator failed"));
        Assert.That(await CountAsync($"SELECT COUNT(*) FROM {quotedFirstTable} WHERE note = 'prepared'"), Is.EqualTo(2));
        Assert.That(await CountAsync($"SELECT COUNT(*) FROM {quotedFirstTable} WHERE name = 'Alice' OR name IS NULL"), Is.EqualTo(2));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Cancellation_before_a_later_table_prevents_its_hook(bool staticOnly)
    {
        using var source = new CancellationTokenSource();
        var script = CreateScript($"INSERT INTO {quotedAuditTable} VALUES ('first');") with
        {
            Rules = [CreateNameRule(firstTableName!, staticOnly), CreateNameRule(secondTableName, staticOnly)],
            BeforeTableSql = ImmutableDictionary<string, string>.Empty
                .Add(firstTableName!, $"INSERT INTO {quotedAuditTable} VALUES ('first');")
                .Add(secondTableName, $"INSERT INTO {quotedAuditTable} VALUES ('second');")
        };

        Assert.CatchAsync<OperationCanceledException>(async () => await new ScriptRunner(new DatabaseAnalyzer(), new FixedGeneratorFactory())
            .RunAsync(script, new CancellingProgressReporter(source), source.Token));
        Assert.That(await CountAsync($"SELECT COUNT(*) FROM {quotedAuditTable}"), Is.EqualTo(1));
        Assert.That(await CountAsync($"SELECT COUNT(*) FROM {quotedSecondTable} WHERE name = 'Bob'"), Is.EqualTo(1));
    }

    [TestCase(false, 1)]
    [TestCase(false, 200)]
    [TestCase(true, 1)]
    [TestCase(true, 200)]
    public async Task Static_pages_include_prepared_rows_once_and_refresh_progress(bool configuredKey, int batchSize)
    {
        if (configuredKey)
            await RecreateFirstTableWithoutPrimaryKeyAsync();

        var script = CreateScript($"""
            INSERT INTO {quotedAuditTable} VALUES ('prepared');
            DELETE FROM {quotedFirstTable} WHERE id = 1;
            INSERT INTO {quotedFirstTable} VALUES (3, 'Carol', 'original'), (4, 'Dan', 'original');
            """) with
        {
            BatchSize = batchSize,
            Rules = [CreateNameRule(firstTableName!, staticOnly: true)],
            TableKeys = configuredKey
                ? ImmutableDictionary<string, string>.Empty.Add(firstTableName!, "id")
                : ImmutableDictionary<string, string>.Empty
        };
        var progress = new RecordingProgress();

        await RunScriptAsync(script, progress);

        Assert.That(await CountAsync($"SELECT COUNT(*) FROM {quotedAuditTable}"), Is.EqualTo(1));
        Assert.That(await CountAsync($"SELECT COUNT(*) FROM {quotedFirstTable} WHERE id IN (2, 3, 4) AND name = 'masked'"), Is.EqualTo(3));
        Assert.That(progress.ForTable(firstTableName!).Select(report => report.Total), Is.All.EqualTo(3));
        Assert.That(progress.ForTable(firstTableName!).Select(report => report.Step),
            Is.EqualTo(batchSize == 1 ? new[] { 1, 2, 3 } : [3]));
    }

    [Test]
    public async Task Keyless_static_update_includes_rows_inserted_into_an_empty_table()
    {
        await RecreateFirstTableWithoutPrimaryKeyAsync();
        await ExecuteSqlAsync($"DELETE FROM {quotedFirstTable};");
        var script = CreateScript($"""
            INSERT INTO {quotedFirstTable} VALUES (1, NULL, 'prepared'), (1, '', 'prepared');
            """) with
        {
            Rules = [CreateNameRule(firstTableName!, staticOnly: true)]
        };
        var progress = new RecordingProgress();

        await RunScriptAsync(script, progress);

        Assert.That(await CountAsync($"SELECT COUNT(*) FROM {quotedFirstTable} WHERE name = 'masked'"), Is.EqualTo(2));
        Assert.That(progress.ForTable(firstTableName!).Single(), Is.EqualTo(new ProgressReport(firstTableName!, 2, 2)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Earlier_hook_refreshes_later_table_progress_without_a_later_hook(bool staticOnly)
    {
        var script = CreateScript($"INSERT INTO {quotedSecondTable} VALUES (2, 'Carol', 'original');") with
        {
            Rules = [CreateNameRule(firstTableName!, staticOnly), CreateNameRule(secondTableName, staticOnly)]
        };
        var progress = new RecordingProgress();

        await RunScriptAsync(script, progress);

        Assert.That(await CountAsync($"SELECT COUNT(*) FROM {quotedSecondTable} WHERE name = 'masked'"), Is.EqualTo(2));
        Assert.That(progress.ForTable(secondTableName).Last(), Is.EqualTo(new ProgressReport(secondTableName, 2, 2)));
    }

    [Test]
    public async Task Cancellation_during_preparation_rolls_back_its_writes()
    {
        using var cancellation = new CancellationTokenSource();
        var marker = Guid.NewGuid().ToString("N");
        var waitStatement = databaseType == DatabaseType.SqlServer
            ? $"WAITFOR DELAY '00:00:20' /* {marker} */;"
            : $"SELECT pg_sleep(20) /* {marker} */;";
        var script = CreateScript($"""
            UPDATE {quotedFirstTable} SET note = 'prepared';
            {waitStatement}
            """);

        var execution = new ScriptRunner(new DatabaseAnalyzer(), new FixedGeneratorFactory())
            .RunAsync(script, new RecordingProgress(), cancellation.Token);
        try
        {
            await WaitForPreparationDelayAsync(marker, execution);
        }
        finally
        {
            cancellation.Cancel();
            Assert.CatchAsync<OperationCanceledException>(() => execution);
        }

        Assert.That(await CountAsync($"SELECT COUNT(*) FROM {quotedFirstTable} WHERE note = 'original'"), Is.EqualTo(2));
        Assert.That(await CountAsync($"SELECT COUNT(*) FROM {quotedFirstTable} WHERE name = 'Alice' OR name IS NULL"), Is.EqualTo(2));
    }

    private async Task WaitForPreparationDelayAsync(string marker, Task execution)
    {
        // Observe the provider's delay after the UPDATE; cancelling earlier would not test rollback.
        var query = databaseType == DatabaseType.SqlServer
            ? $"""
              SELECT COUNT(*) FROM sys.dm_exec_requests AS request
              CROSS APPLY sys.dm_exec_sql_text(request.sql_handle) AS command
              WHERE request.wait_type = 'WAITFOR' AND command.text LIKE '%{marker}%'
              """
            : $"""
              SELECT COUNT(*) FROM pg_stat_activity
              WHERE wait_event = 'PgSleep' AND query LIKE '%{marker}%'
              """;
        var timeout = Stopwatch.StartNew();
        while (!execution.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (await CountAsync(query) == 1) return;

            await Task.Delay(50);
        }

        Assert.Fail("Preparation did not reach its SQL delay before cancellation.");
    }

    private async Task RecreateFirstTableWithoutPrimaryKeyAsync()
    {
        await ExecuteSqlAsync($"DROP TABLE {quotedFirstTable};");
        await ExecuteSqlAsync($"""
            CREATE TABLE {quotedFirstTable} (id int, name varchar(50), note varchar(50));
            INSERT INTO {quotedFirstTable} VALUES (1, 'Alice', 'original'), (2, 'Bob', 'original');
            """);
    }

    private static Rule CreateNameRule(string tableName, bool staticOnly)
        => staticOnly
            ? new Rule(tableName, "name", null, (string?)null) { Value = "masked" }
            : new Rule(tableName, "name", null, "Name");

    private ScriptSpecification CreateScript(string sql) => new(new(databaseType, connectionString),
        [new(firstTableName!, "name", null, "Name")])
    {
        BeforeTableSql = ImmutableDictionary<string, string>.Empty.Add(firstTableName!, sql)
    };

    private Task RunScriptAsync(ScriptSpecification script, RecordingProgress? progress = null)
        => new ScriptRunner(new DatabaseAnalyzer(), new FixedGeneratorFactory()).RunAsync(script, progress ?? new RecordingProgress());

    private async Task ExecuteSqlAsync(string sql)
    {
        await using var connection = driver.CreateConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> CountAsync(string sql)
    {
        await using var connection = driver.CreateConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private sealed class FixedGeneratorFactory : IDataGeneratorFactory, IDataGenerator
    {
        public IDataGenerator Create(GeneratorType type) => this;

        public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default) => Task.FromResult<object>("masked");
    }

    private sealed class FailingGeneratorFactory : IDataGeneratorFactory, IDataGenerator
    {
        public IDataGenerator Create(GeneratorType type) => this;

        public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Generator failed");
    }

    private sealed class RecordingProgress : IProgressReporter
    {
        private readonly List<ProgressReport> reports = [];

        public IEnumerable<ProgressReport> ForTable(string tableName)
            => reports.Where(report => report.TableName == tableName);

        public Task ReportProgressAsync(string tableName, int step, int totalStepCount, CancellationToken cancellationToken = default)
        {
            reports.Add(new ProgressReport(tableName, step, totalStepCount));
            return Task.CompletedTask;
        }
    }

    private sealed record ProgressReport(string TableName, int Step, int Total);

    private sealed class CancellingProgressReporter(CancellationTokenSource source) : IProgressReporter
    {
        public Task ReportProgressAsync(string tableName, int step, int totalStepCount, CancellationToken cancellationToken = default)
        {
            source.Cancel();
            return Task.CompletedTask;
        }
    }
}
