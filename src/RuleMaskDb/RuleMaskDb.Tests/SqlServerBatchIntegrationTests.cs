using Microsoft.Data.SqlClient;
using RuleMaskDb.Generators;
using RuleMaskDb.ScriptDom;

namespace RuleMaskDb.Tests;

[NonParallelizable]
public class SqlServerBatchIntegrationTests
{
    private string? connectionString;
    private string? name;
    private string table = null!;
    private string log = null!;

    [SetUp]
    public async Task SetUp()
    {
        connectionString = Environment.GetEnvironmentVariable("RULEMASK_TEST_SQLSERVER");
        if (string.IsNullOrEmpty(connectionString))
            Assert.Ignore("Set RULEMASK_TEST_SQLSERVER to an explicitly disposable SQL Server database.");
        name = "Batch]People_" + Guid.NewGuid().ToString("N");
        var driver = new RelationalDriver(DatabaseType.SqlServer);
        table = "dbo." + driver.QuoteIdentifier(name);
        log = "dbo." + driver.QuoteIdentifier(name + "_log");
        await Execute($"""
            CREATE TABLE {table} ([Tenant] int, [Id] int, [Name] nvarchar(100), [Enabled] bit, [Score] int, [Untouched] nvarchar(30), PRIMARY KEY ([Tenant], [Id]));
            CREATE TABLE {log} ([Sequence] int IDENTITY, [Size] int);
            WITH numbers AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM numbers WHERE n < 205)
            INSERT INTO {table} SELECT 1, n, CASE WHEN n = 1 THEN NULL WHEN n = 2 THEN '' ELSE 'Alice' END, 1, 7, 'keep' FROM numbers OPTION (MAXRECURSION 205);
            """);
        // This verifies OUTPUT works with enabled triggers and measures actual statement sizes.
        await Execute($"""
            CREATE TRIGGER {driver.QuoteIdentifier(name + "_trigger")} ON {table} AFTER UPDATE AS
            BEGIN
                SET NOCOUNT ON;
                IF UPDATE([Name]) AND EXISTS(SELECT 1 FROM deleted WHERE [Name] IS NULL OR [Name] = '')
                    THROW 50001, 'Preserved column was included in SET', 1;
                INSERT INTO {log} ([Size]) SELECT COUNT(*) FROM inserted;
            END
            """);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (name is not null) await Execute($"DROP TABLE IF EXISTS {table}; DROP TABLE IF EXISTS {log};");
    }

    [TestCase(100)]
    [TestCase(200)]
    public async Task Set_based_updates_preserve_skipped_fields_and_support_triggers(int size)
    {
        var script = new ScriptSpecification(new(DatabaseType.SqlServer, connectionString!),
            [new("dbo." + name, "Name", null, "Name"),
             new Rule("dbo." + name, "Enabled", null, (string?)null) { Value = false },
             new Rule("dbo." + name, "Score", null, (string?)null) { Value = null }]) { BatchSize = size };
        await new ScriptRunner(new DatabaseAnalyzer(), new FixedFactory()).RunAsync(script, new Progress());
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var verify = new SqlCommand($"""
            SELECT COUNT(*) FROM {table} WHERE [Enabled] = 0 AND [Score] IS NULL AND [Untouched] = 'keep'
            AND (([Id] = 1 AND [Name] IS NULL) OR ([Id] = 2 AND [Name] = '') OR ([Id] > 2 AND [Name] = 'masked'));
            SELECT [Size] FROM {log} ORDER BY [Sequence];
            """, connection);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.That(await reader.ReadAsync(), Is.True);
        Assert.That(reader.GetInt32(0), Is.EqualTo(205));
        Assert.That(await reader.NextResultAsync(), Is.True);
        var sizes = new List<int>();
        while (await reader.ReadAsync()) sizes.Add(reader.GetInt32(0));
        Assert.That(sizes.Sum(), Is.EqualTo(205));
        Assert.That(sizes.Max(), Is.LessThanOrEqualTo(size));
        Assert.That(sizes.Count, Is.LessThanOrEqualTo((int)Math.Ceiling(205d / size) + 2));
    }

    [TestCase(1)]
    [TestCase(200)]
    public async Task Static_only_table_uses_one_update_with_enabled_triggers(int batchSize)
    {
        var script = new ScriptSpecification(new(DatabaseType.SqlServer, connectionString!),
            [new Rule("dbo." + name, "Enabled", null, (string?)null) { Value = false },
             new Rule("dbo." + name, "Score", null, (string?)null) { Value = null }]) { BatchSize = batchSize };
        await new ScriptRunner(new DatabaseAnalyzer(), new FixedFactory()).RunAsync(script, new Progress());
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var verify = new SqlCommand($"""
            SELECT COUNT(*) FROM {table} WHERE [Enabled] = 0 AND [Score] IS NULL AND [Untouched] = 'keep';
            SELECT [Size] FROM {log};
            """, connection);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.That(await reader.ReadAsync(), Is.True);
        Assert.That(reader.GetInt32(0), Is.EqualTo(205));
        Assert.That(await reader.NextResultAsync(), Is.True);
        Assert.That(await reader.ReadAsync(), Is.True);
        Assert.That(reader.GetInt32(0), Is.EqualTo(205));
        Assert.That(await reader.ReadAsync(), Is.False);
    }

    private async Task Execute(string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FixedFactory : IDataGeneratorFactory, IDataGenerator
    {
        public IDataGenerator Create(GeneratorType type) => this;
        public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default) => Task.FromResult<object>("masked");
    }
    private sealed class Progress : IProgressReporter
    {
        public Task ReportProgressAsync(string tableName, int step, int totalStepCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
