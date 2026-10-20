using System.Collections.Immutable;
using RuleMaskDb.Generators;
using RuleMaskDb.ScriptDom;

namespace RuleMaskDb.Tests;

[TestFixture(DatabaseType.SqlServer)]
[TestFixture(DatabaseType.PostgreSQL)]
[NonParallelizable]
public sealed class ExecutionReportIntegrationTests(DatabaseType databaseType)
{
    private readonly RelationalDriver driver = new(databaseType);
    private string connectionString = null!;
    private TestTable? generatedTable;
    private TestTable staticTable = null!;
    private TestTable emptyTable = null!;
    private TestTable auditTable = null!;

    [SetUp]
    public async Task SetUp()
    {
        var connectionVariable = databaseType == DatabaseType.SqlServer ? "RULEMASK_TEST_SQLSERVER" : "RULEMASK_TEST_POSTGRES";
        connectionString = Environment.GetEnvironmentVariable(connectionVariable)!;
        if (string.IsNullOrEmpty(connectionString))
            Assert.Ignore($"Set {connectionVariable} to an explicitly disposable database.");

        generatedTable = CreateTableReference("generated");
        staticTable = CreateTableReference("static");
        emptyTable = CreateTableReference("empty");
        auditTable = CreateTableReference("audit");

        await ExecuteSqlAsync($"""
            CREATE TABLE {generatedTable.SqlName} (id int PRIMARY KEY, name varchar(50), note varchar(50));
            CREATE TABLE {staticTable.SqlName} (name varchar(50));
            CREATE TABLE {emptyTable.SqlName} (id int PRIMARY KEY, name varchar(50));
            CREATE TABLE {auditTable.SqlName} (id int PRIMARY KEY);
            INSERT INTO {generatedTable.SqlName} VALUES (1, 'Alice', 'original');
            INSERT INTO {staticTable.SqlName} VALUES ('Bob');
            INSERT INTO {auditTable.SqlName} VALUES (1);
            """);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (generatedTable is null) return;

        await ExecuteSqlAsync($"""
            DROP TABLE IF EXISTS {generatedTable.SqlName};
            DROP TABLE IF EXISTS {staticTable.SqlName};
            DROP TABLE IF EXISTS {emptyTable.SqlName};
            DROP TABLE IF EXISTS {auditTable.SqlName};
            """);
    }

    [Test]
    public async Task Report_covers_preparation_mixed_rules_static_updates_empty_tables_and_truncation()
    {
        var preparationDelay = databaseType == DatabaseType.SqlServer
            ? "WAITFOR DELAY '00:00:00.250';"
            : "SELECT pg_sleep(0.25);";
        var script = new ScriptSpecification(new(databaseType, connectionString),
            [
                new(generatedTable!.Name, "name", null, "Name"),
                new Rule(staticTable.Name, "name", null, (string?)null) { Value = "fixed" },
                new Rule(generatedTable.Name, "note", null, (string?)null) { Value = "redacted" },
                new(emptyTable.Name, "name", null, "Name")
            ])
        {
            BatchSize = 1,
            TruncateTables = [auditTable.Name],
            BeforeTableSql = ImmutableDictionary<string, string>.Empty
                .Add(generatedTable.Name, preparationDelay)
        };

        var report = await RunAsync(script);

        Assert.That(report.Tables.Select(table => table.TableName),
            Is.EqualTo(new[] { generatedTable.Name, staticTable.Name, emptyTable.Name }));
        Assert.That(report.Tables[0].Duration, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(200)),
            "The table timer must start before its preparation SQL.");
        Assert.That(report.Tables.Select(table => table.Duration), Is.All.GreaterThan(TimeSpan.Zero));
        Assert.That(report.Truncation, Is.Not.Null);
        Assert.That(report.Truncation!.TableNames, Is.EqualTo(new[] { auditTable.Name }));
        Assert.That(report.TotalDuration, Is.GreaterThanOrEqualTo(
            report.Truncation.Duration + report.Tables.Aggregate(TimeSpan.Zero, (elapsed, table) => elapsed + table.Duration)));
    }

    [Test]
    public async Task Truncate_only_script_reports_one_shared_transaction()
    {
        var script = new ScriptSpecification(new(databaseType, connectionString), [])
        {
            TruncateTables = [generatedTable!.Name, auditTable.Name]
        };

        var report = await RunAsync(script);

        Assert.That(report.Tables, Is.Empty);
        Assert.That(report.Truncation, Is.Not.Null);
        Assert.That(report.Truncation!.TableNames, Is.EqualTo(script.TruncateTables));
        Assert.That(report.Truncation.Duration, Is.GreaterThan(TimeSpan.Zero));
        Assert.That(report.TotalDuration, Is.GreaterThanOrEqualTo(report.Truncation.Duration));
    }

    private Task<ScriptExecutionReport> RunAsync(ScriptSpecification script)
        => new ScriptRunner(new DatabaseAnalyzer(), new BogusDataGeneratorFactory())
            .RunAsync(script, new SilentProgressReporter());

    private TestTable CreateTableReference(string suffix)
    {
        var schema = databaseType == DatabaseType.SqlServer ? "dbo" : "public";
        var localName = $"timing_{Guid.NewGuid():N}_{suffix}";
        return new TestTable($"{schema}.{localName}",
            $"{driver.QuoteIdentifier(schema)}.{driver.QuoteIdentifier(localName)}");
    }

    private async Task ExecuteSqlAsync(string sql)
    {
        await using var connection = driver.CreateConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed record TestTable(string Name, string SqlName);

    private sealed class SilentProgressReporter : IProgressReporter
    {
        public Task ReportProgressAsync(string tableName, int step, int totalStepCount,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
