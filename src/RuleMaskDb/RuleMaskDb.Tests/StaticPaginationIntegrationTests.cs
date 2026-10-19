using RuleMaskDb.Generators;
using RuleMaskDb.ScriptDom;

namespace RuleMaskDb.Tests;

[TestFixture(DatabaseType.SqlServer)]
[TestFixture(DatabaseType.PostgreSQL)]
[NonParallelizable]
public class StaticPaginationIntegrationTests(DatabaseType databaseType)
{
    private readonly RelationalDriver driver = new(databaseType);
    private string connectionString = null!;
    private string? name;
    private string table = null!;

    [SetUp]
    public void SetUp()
    {
        var variable = databaseType == DatabaseType.SqlServer ? "RULEMASK_TEST_SQLSERVER" : "RULEMASK_TEST_POSTGRES";
        connectionString = Environment.GetEnvironmentVariable(variable)!;
        if (string.IsNullOrEmpty(connectionString)) Assert.Ignore($"Set {variable} to an explicitly disposable database.");
        var schema = databaseType == DatabaseType.SqlServer ? "dbo" : "public";
        var localName = "static_pages_" + Guid.NewGuid().ToString("N");
        name = schema + "." + localName;
        table = driver.QuoteIdentifier(schema) + "." + driver.QuoteIdentifier(localName);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (name is not null) await Execute($"DROP TABLE IF EXISTS {table}");
    }

    [TestCase(0)]
    [TestCase(6)]
    [TestCase(7)]
    public async Task Configured_text_key_visits_each_row_once_even_when_values_already_match(int rowCount)
    {
        await Execute($"CREATE TABLE {table} (code varchar(30) NOT NULL UNIQUE, value varchar(10))");
        for (var i = 0; i < rowCount; i++)
            await Execute($"INSERT INTO {table} VALUES ('key-{rowCount - i:D3}', 'fixed')");
        var script = CreateScript(3);
        script = script with { TableKeys = script.TableKeys.Add(name!, "code") };
        var progress = new RecordingProgress();

        await new ScriptRunner(new DatabaseAnalyzer(), new RejectingFactory()).RunAsync(script, progress);

        Assert.That(await Scalar($"SELECT COUNT(*) FROM {table} WHERE value = 'fixed'"), Is.EqualTo(rowCount));
        var steps = rowCount == 0 ? [0] : Enumerable.Range(0, rowCount).Chunk(3)
            .Select((_, i) => Math.Min((i + 1) * 3, rowCount)).ToArray();
        Assert.That(progress.Steps, Is.EqualTo(steps));
    }

    [Test]
    public async Task Composite_primary_key_uses_declared_key_order()
    {
        await Execute($"CREATE TABLE {table} (id int NOT NULL, tenant int NOT NULL, value varchar(10), PRIMARY KEY (tenant, id))");
        await Execute($"INSERT INTO {table} VALUES (1, 2, 'old'), (8, 1, 'old'), (7, 1, 'old'), (9, 2, 'old')");
        var script = CreateScript(1);
        var description = await new DatabaseAnalyzer().DescribeDatabaseAsync(script.Database);
        var fields = description.Tables.Single(t => t.Name == name).Fields;
        Assert.That(fields.Single(f => f.Path == "tenant").PrimaryKeyOrdinal, Is.EqualTo(1));
        Assert.That(fields.Single(f => f.Path == "id").PrimaryKeyOrdinal, Is.EqualTo(2));
        using var source = new CancellationTokenSource();

        Assert.CatchAsync<OperationCanceledException>(() => new ScriptRunner(new DatabaseAnalyzer(), new RejectingFactory())
            .RunAsync(script, new FirstPageProgress(async () =>
            {
                // PostgreSQL's uncommitted page is checked through the rollback test instead.
                if (databaseType == DatabaseType.SqlServer)
                    Assert.That(await Scalar($"SELECT COUNT(*) FROM {table} WHERE tenant = 1 AND id = 7 AND value = 'fixed'"), Is.EqualTo(1));
                source.Cancel();
            }), source.Token));
    }

    [Test]
    public async Task Oversized_requested_page_respects_provider_parameter_budget()
    {
        await Execute($"CREATE TABLE {table} (id int PRIMARY KEY, value varchar(10))");
        for (var start = 0; start < 2005; start += 500)
        {
            var rows = string.Join(", ", Enumerable.Range(start, Math.Min(500, 2005 - start)).Select(i => $"({i}, 'old')"));
            await Execute($"INSERT INTO {table} VALUES {rows}");
        }
        var progress = new RecordingProgress();
        await new ScriptRunner(new DatabaseAnalyzer(), new RejectingFactory()).RunAsync(CreateScript(int.MaxValue), progress);

        Assert.That(await Scalar($"SELECT COUNT(*) FROM {table} WHERE value = 'fixed'"), Is.EqualTo(2005));
        Assert.That(progress.Steps, Is.EqualTo(databaseType == DatabaseType.SqlServer ? new[] { 1999, 2005 } : [2005]));
    }

    [Test]
    public async Task Keyless_table_keeps_a_single_update()
    {
        await Execute($"CREATE TABLE {table} (value varchar(10))");
        await Execute($"INSERT INTO {table} VALUES ('old'), (NULL), ('')");
        var progress = new RecordingProgress();
        await new ScriptRunner(new DatabaseAnalyzer(), new RejectingFactory()).RunAsync(CreateScript(1), progress);

        Assert.That(await Scalar($"SELECT COUNT(*) FROM {table} WHERE value = 'fixed'"), Is.EqualTo(3));
        Assert.That(progress.Steps, Is.EqualTo(new[] { 3 }));
    }

    private ScriptSpecification CreateScript(int batchSize)
        => new(new(databaseType, connectionString),
            [new Rule(name!, "value", null, (string?)null) { Value = "fixed" }]) { BatchSize = batchSize };

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

    private sealed class RejectingFactory : IDataGeneratorFactory
    {
        public IDataGenerator Create(GeneratorType type) => throw new AssertionException("Static rules must not create a generator.");
        public Sdk.IDataGenerator Create(string name) => throw new AssertionException("Static rules must not create a generator.");
    }

    private sealed class RecordingProgress : IProgressReporter
    {
        public List<int> Steps { get; } = [];
        public Task ReportProgressAsync(string tableName, int step, int totalStepCount, CancellationToken cancellationToken = default)
        {
            Steps.Add(step);
            return Task.CompletedTask;
        }
    }

    private sealed class FirstPageProgress(Func<Task> onPage) : IProgressReporter
    {
        public Task ReportProgressAsync(string tableName, int step, int totalStepCount, CancellationToken cancellationToken = default)
            => onPage();
    }
}
