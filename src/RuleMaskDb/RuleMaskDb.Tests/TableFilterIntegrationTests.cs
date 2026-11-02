using System.Collections.Immutable;
using RuleMaskDb.Generators;
using RuleMaskDb.ScriptDom;

namespace RuleMaskDb.Tests;

/// <summary>
/// Exercises the complete filtered pipeline against disposable SQL Server and PostgreSQL databases.
/// </summary>
[TestFixture(DatabaseType.SqlServer)]
[TestFixture(DatabaseType.PostgreSQL)]
[NonParallelizable]
public class TableFilterIntegrationTests(DatabaseType databaseType)
{
    private readonly RelationalDriver driver = new(databaseType);
    private string connectionString = null!;
    private string tableName = null!;
    private string quotedTable = null!;

    [SetUp]
    public async Task SetUp()
    {
        var variable = databaseType == DatabaseType.SqlServer ? "RULEMASK_TEST_SQLSERVER" : "RULEMASK_TEST_POSTGRES";
        connectionString = Environment.GetEnvironmentVariable(variable)!;
        if (string.IsNullOrWhiteSpace(connectionString))
            Assert.Ignore($"Set {variable} to an explicitly disposable database.");

        var name = "filter_" + Guid.NewGuid().ToString("N");
        var schema = databaseType == DatabaseType.SqlServer ? "dbo" : "public";
        tableName = schema + "." + name;
        quotedTable = driver.QuoteIdentifier(schema) + "." + driver.QuoteIdentifier(name);
        await CreateTableAsync(primaryKey: true);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (quotedTable is not null)
            await ExecuteAsync($"DROP TABLE IF EXISTS {quotedTable}");
    }

    [Test]
    public async Task Mixed_generated_and_static_rules_only_update_selected_discriminator_values()
    {
        var script = CreateScript([new(tableName, "name", null, "Name"),
            new Rule(tableName, "note", null, (string?)null) { Value = "redacted" }]);
        var progress = new RecordingProgress();

        await RunAsync(script, progress);

        Assert.That(await ReadRowsAsync(), Is.EqualTo(new[]
        {
            "1|Person|masked|redacted", "2|Company|Bob|keep", "3|Partner|masked|redacted",
            "4|Person|masked|redacted", "5|Company|Eve|keep"
        }));
        Assert.That(progress.Updates, Is.EqualTo(new[] { (2, 3), (3, 3) }));
    }

    [Test]
    public async Task Static_key_pages_only_update_selected_rows_and_report_filtered_totals()
    {
        var script = CreateScript([new Rule(tableName, "name", null, (string?)null) { Value = "hidden" }])
            with { BatchSize = 1 };
        var progress = new RecordingProgress();

        await RunAsync(script, progress);

        Assert.That(await ReadRowsAsync(), Is.EqualTo(new[]
        {
            "1|Person|hidden|keep", "2|Company|Bob|keep", "3|Partner|hidden|keep",
            "4|Person|hidden|keep", "5|Company|Eve|keep"
        }));
        Assert.That(progress.Updates, Is.EqualTo(new[] { (1, 3), (2, 3), (3, 3) }));
    }

    [Test]
    public async Task Keyless_static_update_applies_the_filter_instead_of_updating_the_entire_table()
    {
        await ExecuteAsync($"DROP TABLE {quotedTable}");
        await CreateTableAsync(primaryKey: false);
        var script = CreateScript([new Rule(tableName, "name", null, (string?)null) { Value = "hidden" }]);
        var progress = new RecordingProgress();

        await RunAsync(script, progress);

        Assert.That(await ReadRowsAsync(), Is.EqualTo(new[]
        {
            "1|Person|hidden|keep", "2|Company|Bob|keep", "3|Partner|hidden|keep",
            "4|Person|hidden|keep", "5|Company|Eve|keep"
        }));
        Assert.That(progress.Updates, Is.EqualTo(new[] { (3, 3) }));
    }

    [Test]
    public async Task Configured_key_is_checked_within_filter_and_does_not_update_excluded_duplicate_keys()
    {
        await ExecuteAsync($"DROP TABLE {quotedTable}");
        await CreateTableAsync(primaryKey: false);
        await ExecuteAsync($"UPDATE {quotedTable} SET id = 1 WHERE id = 2");
        var script = CreateScript([new(tableName, "name", null, "Name")]) with
        {
            TableKeys = ImmutableDictionary<string, string>.Empty.Add(tableName, "id"),
            TableFilters = ImmutableDictionary<string, TableFilter>.Empty.Add(tableName, new("kind", ["Person"]))
        };

        await RunAsync(script);

        var rows = await ReadRowsAsync();
        Assert.That(rows, Does.Contain("1|Person|masked|keep"));
        Assert.That(rows, Does.Contain("1|Company|Bob|keep"));
        Assert.That(rows, Does.Contain("4|Person|masked|keep"));
        Assert.That(rows, Does.Contain("3|Partner|Carol|keep"));
    }

    [Test]
    public async Task Before_table_sql_can_change_filter_membership_before_counting_and_reading()
    {
        var script = CreateScript([new(tableName, "name", null, "Name")]) with
        {
            TableFilters = ImmutableDictionary<string, TableFilter>.Empty.Add(tableName, new("kind", ["Person"])),
            BeforeTableSql = ImmutableDictionary<string, string>.Empty.Add(
                tableName, $"UPDATE {quotedTable} SET kind = 'Person' WHERE id = 2")
        };
        var progress = new RecordingProgress();

        await RunAsync(script, progress);

        Assert.That(await ReadRowsAsync(), Is.EqualTo(new[]
        {
            "1|Person|masked|keep", "2|Person|masked|keep", "3|Partner|Carol|keep",
            "4|Person|masked|keep", "5|Company|Eve|keep"
        }));
        Assert.That(progress.Updates.Last(), Is.EqualTo((3, 3)));
    }

    [Test]
    public async Task Numeric_filter_values_are_parameterized_as_numbers()
    {
        var script = CreateScript([new Rule(tableName, "name", null, (string?)null) { Value = "hidden" }]) with
        {
            TableFilters = ImmutableDictionary<string, TableFilter>.Empty.Add(tableName, new("id", [1, 4]))
        };

        await RunAsync(script);

        Assert.That(await ReadRowsAsync(), Is.EqualTo(new[]
        {
            "1|Person|hidden|keep", "2|Company|Bob|keep", "3|Partner|Carol|keep",
            "4|Person|hidden|keep", "5|Company|Eve|keep"
        }));
    }

    private ScriptSpecification CreateScript(ImmutableArray<Rule> rules) =>
        new(new(databaseType, connectionString), rules)
        {
            BatchSize = 2,
            TableFilters = ImmutableDictionary<string, TableFilter>.Empty.Add(
                tableName, new("kind", ["Person", "Partner"]))
        };

    private Task RunAsync(ScriptSpecification script, IProgressReporter? progress = null)
        => new ScriptRunner(new DatabaseAnalyzer(), new FixedFactory())
            .RunAsync(script, progress ?? new RecordingProgress());

    private async Task CreateTableAsync(bool primaryKey)
    {
        await ExecuteAsync($"""
            CREATE TABLE {quotedTable} (
                id int {(primaryKey ? "PRIMARY KEY" : "")},
                kind varchar(80), name varchar(80), note varchar(80)
            );
            INSERT INTO {quotedTable} VALUES
                (1, 'Person', 'Alice', 'keep'), (2, 'Company', 'Bob', 'keep'),
                (3, 'Partner', 'Carol', 'keep'), (4, 'Person', 'Dora', 'keep'),
                (5, 'Company', 'Eve', 'keep');
            """);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = driver.CreateConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string[]> ReadRowsAsync()
    {
        await using var connection = driver.CreateConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id, kind, name, note FROM {quotedTable} ORDER BY id, kind";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add($"{reader.GetInt32(0)}|{reader.GetString(1)}|{reader.GetString(2)}|{reader.GetString(3)}");
        return rows.ToArray();
    }

    private sealed class FixedFactory : IDataGeneratorFactory, IDataGenerator
    {
        public IDataGenerator Create(GeneratorType generatorType) => this;
        public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<object>("masked");
    }

    private sealed class RecordingProgress : IProgressReporter
    {
        public List<(int Step, int Total)> Updates { get; } = [];

        public Task ReportProgressAsync(string tableName, int step, int totalStepCount,
            CancellationToken cancellationToken = default)
        {
            Updates.Add((step, totalStepCount));
            return Task.CompletedTask;
        }
    }
}
