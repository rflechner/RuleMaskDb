using System.Data.Common;
using RuleMaskDb.Generators;
using RuleMaskDb.ScriptDom;

namespace RuleMaskDb.Tests;

[TestFixture(DatabaseType.SqlServer)]
[TestFixture(DatabaseType.PostgreSQL)]
[NonParallelizable]
public class TextTruncationIntegrationTests(DatabaseType databaseType)
{
    private readonly RelationalDriver driver = new(databaseType);
    private string connectionString = null!;
    private string? tableName;
    private string table = null!;

    [SetUp]
    public async Task SetUp()
    {
        var variable = databaseType == DatabaseType.SqlServer ? "RULEMASK_TEST_SQLSERVER" : "RULEMASK_TEST_POSTGRES";
        connectionString = Environment.GetEnvironmentVariable(variable)!;
        if (string.IsNullOrEmpty(connectionString)) Assert.Ignore($"Set {variable} to an explicitly disposable database.");
        var name = "truncation_" + Guid.NewGuid().ToString("N");
        var schema = databaseType == DatabaseType.SqlServer ? "dbo" : "public";
        tableName = schema + "." + name;
        table = driver.QuoteIdentifier(schema) + "." + driver.QuoteIdentifier(name);
        var unicode = databaseType == DatabaseType.SqlServer
            ? "nvarchar(3) COLLATE Latin1_General_100_CI_AS_SC" : "varchar(2)";
        var encoded = databaseType == DatabaseType.SqlServer
            ? "varchar(5) COLLATE Latin1_General_100_CI_AS_SC_UTF8" : "varchar(2)";
        var unlimited = databaseType == DatabaseType.SqlServer ? "nvarchar(max)" : "text";
        await Execute($"""
            CREATE TABLE {table} (
                id int PRIMARY KEY, generated varchar(5), fixed char(5),
                unicode_value {unicode}, encoded_value {encoded},
                unlimited {unlimited}, nullable_value varchar(5), empty_value varchar(5));
            """);
        var rows = string.Join(", ", Enumerable.Range(1, 205).Select(i => $"({i}, 'old', 'old', 'x', 'x', 'old', NULL, '')"));
        await Execute($"INSERT INTO {table} VALUES {rows}");
    }

    [TearDown]
    public async Task TearDown()
    {
        if (tableName is not null) await Execute($"DROP TABLE IF EXISTS {table}");
    }

    [TestCase(1, false)]
    [TestCase(100, false)]
    [TestCase(200, false)]
    [TestCase(1, true)]
    [TestCase(200, true)]
    public async Task Generated_and_static_text_fit_actual_columns_in_all_batches(int batchSize, bool staticOnly)
    {
        var script = new ScriptSpecification(new(databaseType, connectionString),
        [
            new(tableName!, "generated", null, "Name"),
            new Rule(tableName!, "fixed", null, (string?)null) { Value = "Alexander" },
            new Rule(tableName!, "unicode_value", null, (string?)null) { Value = "A😀B" },
            new Rule(tableName!, "encoded_value", null, (string?)null) { Value = "ééé" },
            new Rule(tableName!, "unlimited", null, (string?)null) { Value = new string('x', 9000) },
            new(tableName!, "nullable_value", null, "Name"),
            new(tableName!, "empty_value", null, "Name")
        ]) { BatchSize = batchSize };
        if (staticOnly)
            script = script with
            {
                Rules = [..script.Rules.Select(r => r.HasValue ? r : r with
                {
                    Generator = null,
                    Value = r.Column == "nullable_value" ? null : r.Column == "empty_value" ? "" : "Alexander"
                })]
            };
        var analyzer = new DatabaseAnalyzer();
        var description = await analyzer.DescribeDatabaseAsync(script.Database);
        var fields = description.Tables.Single(t => t.Name == tableName).Fields;
        Assert.That(fields.Single(f => f.Path == "generated").MaxLength, Is.EqualTo(5));
        await new ScriptRunner(analyzer, new FixedFactory()).RunAsync(script, new Progress());

        await using var connection = driver.CreateConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT generated, fixed, unicode_value, encoded_value, unlimited, nullable_value, empty_value FROM {table}";
        await using var reader = await command.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync())
        {
            Assert.That(reader.GetString(0), Is.EqualTo("Alexa"));
            Assert.That(reader.GetString(1), Is.EqualTo("Alexa"));
            Assert.That(reader.GetString(2), Is.EqualTo("A😀"));
            Assert.That(reader.GetString(3), Is.EqualTo("éé"));
            Assert.That(reader.GetString(4), Has.Length.EqualTo(9000));
            Assert.That(reader.IsDBNull(5), Is.True);
            Assert.That(reader.GetString(6), Is.Empty);
            count++;
        }
        Assert.That(count, Is.EqualTo(205));
    }

    [Test]
    public async Task Constraint_errors_are_not_suppressed()
    {
        await Execute($"ALTER TABLE {table} ADD CHECK (generated <> 'Alexa')");
        var script = new ScriptSpecification(new(databaseType, connectionString),
            [new(tableName!, "generated", null, "Name")]);
        Assert.That(async () => await new ScriptRunner(new DatabaseAnalyzer(), new FixedFactory())
            .RunAsync(script, new Progress()), Throws.InstanceOf<DbException>());
    }

    private async Task Execute(string sql)
    {
        await using var connection = driver.CreateConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FixedFactory : IDataGeneratorFactory, IDataGenerator
    {
        public IDataGenerator Create(GeneratorType type) => this;
        public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<object>("Alexander");
    }

    private sealed class Progress : IProgressReporter
    {
        public Task ReportProgressAsync(string tableName, int step, int totalStepCount,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
