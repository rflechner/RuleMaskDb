using Moq;
using RuleMaskDb.Generators;
using RuleMaskDb.ScriptDom;
using RuleMaskDb.SqlDomain;
using RuleMaskDb.Yaml;

namespace RuleMaskDb.Tests;

public sealed class TruncateTests
{
    [TestCase("rules: []", 0)]
    [TestCase("truncateTables: []", 0)]
    [TestCase("truncateTables:\n  - dbo.AuditEvents\n  - dbo.Sessions", 2)]
    public async Task Yaml_supports_truncate_only_and_existing_scripts(string yaml, int count)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, yaml);
            var script = await new YamlScriptDomProvider(path).LoadScriptAsync();
            Assert.That(script.TruncateTables.Length, Is.EqualTo(count));
            Assert.That(script.Rules, Is.Empty);
            if (count > 0)
                Assert.That(script.TruncateTables, Is.EqualTo(new[] { "dbo.AuditEvents", "dbo.Sessions" }));
        }
        finally { File.Delete(path); }
    }

    [TestCase("missing", "missing or ambiguous")]
    [TestCase("ambiguous", "missing or ambiguous")]
    [TestCase("blank", "non-empty")]
    [TestCase("null", "non-empty")]
    [TestCase("duplicate", "Duplicate")]
    [TestCase("overlap", "both truncateTables and rules")]
    [TestCase("invalid-rule", "Unknown column")]
    public void Invalid_targets_fail_before_opening_connections(string scenario, string message)
    {
        var database = new DatabaseSpecification(DatabaseType.SqlServer, "invalid connection string");
        var table = new TableDescription("dbo.AuditEvents", 2, []);
        var other = new TableDescription("dbo.Customers", 2,
            [new FieldDescription("Id", DateType.Text, true)]);
        var analyzer = new Mock<IDatabaseAnalyzer>();
        analyzer.Setup(a => a.DescribeDatabaseAsync(database)).ReturnsAsync(
            new DatabaseDescription("test", scenario == "ambiguous" ? [table, table, other] : [table, other]));
        var script = new ScriptSpecification(database, scenario switch
        {
            "overlap" => [new Rule(table.Name, "Message", null, "Name")],
            "invalid-rule" => [new Rule(other.Name, "Missing", null, "Name")],
            _ => []
        })
        {
            TruncateTables = scenario switch
            {
                "missing" => ["dbo.Missing"],
                "blank" => [" "],
                "null" => [null!],
                "duplicate" => [table.Name, table.Name],
                _ => [table.Name]
            }
        };
        Assert.That(async () => await new ScriptRunner(analyzer.Object, new BogusDataGeneratorFactory())
                .RunAsync(script, new Mock<IProgressReporter>().Object),
            Throws.InvalidOperationException.With.Message.Contains(message));
    }

    [TestCase(DatabaseType.PostgreSQL)]
    [TestCase(DatabaseType.SqlServer)]
    public void Truncate_sql_quotes_metadata_names_and_never_cascades(DatabaseType type)
    {
        TableDescription[] tables =
        [
            new("ignored", 0, []) { Schema = "app.data", LocalName = "Audit\"]Events" },
            new("ignored", 0, []) { Schema = "app", LocalName = "Sessions" }
        ];
        var commands = new RelationalDriver(type).CreateTruncateCommands(tables);
        Assert.That(commands, Is.EqualTo(type == DatabaseType.PostgreSQL
            ? new[] { "TRUNCATE TABLE \"app.data\".\"Audit\"\"]Events\", \"app\".\"Sessions\" CONTINUE IDENTITY RESTRICT" }
            : new[] { "TRUNCATE TABLE [app.data].[Audit\"]]Events]", "TRUNCATE TABLE [app].[Sessions]" }));
    }

    [Test]
    public void No_targets_produce_no_commands()
        => Assert.That(new RelationalDriver(DatabaseType.SqlServer).CreateTruncateCommands([]), Is.Empty);
}
