using System.Collections.Immutable;
using Moq;
using RuleMaskDb.Generators;
using RuleMaskDb.ScriptDom;
using RuleMaskDb.SqlDomain;
using RuleMaskDb.Yaml;

namespace RuleMaskDb.Tests;

public sealed class ConfiguredRowKeyTests
{
    [Test]
    public async Task Yaml_parses_table_keys_without_changing_rules()
    {
        var script = await LoadYaml("""
            tableKeys:
              dbo.LegacyCustomers: CustomerCode
            rules:
              - table: dbo.LegacyCustomers
                column: FirstName
                generator: FirstName
            """);
        Assert.That(script.TableKeys["dbo.LegacyCustomers"], Is.EqualTo("CustomerCode"));
        Assert.That(script.Rules.Single().Generator, Is.EqualTo("FirstName"));
    }

    [Test]
    public async Task Existing_yaml_has_no_configured_keys()
        => Assert.That((await LoadYaml("rules: []")).TableKeys, Is.Empty);

    [TestCase("dbo.LegacyCustomers: ''")]
    [TestCase("dbo.LegacyCustomers: null")]
    [TestCase("'': CustomerCode")]
    public void Yaml_rejects_empty_key_names(string entry)
        => Assert.ThrowsAsync<InvalidOperationException>(async () => await LoadYaml($"tableKeys:\n  {entry}\nrules: []"));

    [TestCase("missing")]
    [TestCase("targeted")]
    [TestCase("existing-primary-key")]
    [TestCase("unused")]
    [TestCase("blank")]
    public void Invalid_configuration_fails_before_opening_connections(string scenario)
    {
        var database = new DatabaseSpecification(DatabaseType.SqlServer, "invalid connection string");
        var fields = ImmutableArray.Create(
            new FieldDescription("CustomerCode", DateType.Text, scenario == "existing-primary-key"),
            new FieldDescription("FirstName", DateType.Text, false));
        var table = new TableDescription("dbo.LegacyCustomers", 2, fields);
        var analyzer = new Mock<IDatabaseAnalyzer>();
        analyzer.Setup(a => a.DescribeDatabaseAsync(database))
            .ReturnsAsync(new DatabaseDescription("test", ImmutableArray.Create(table)));
        var key = scenario switch { "missing" => "Missing", "targeted" => "FirstName", "blank" => "", _ => "CustomerCode" };
        var script = new ScriptSpecification(database, ImmutableArray.Create(
            new Rule(table.Name, "FirstName", null, "FirstName")))
        {
            TableKeys = ImmutableDictionary<string, string>.Empty.Add(
                scenario == "unused" ? "dbo.Typo" : table.Name, key)
        };
        var exception = Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ScriptRunner(analyzer.Object, new BogusDataGeneratorFactory())
                .RunAsync(script, new Mock<IProgressReporter>().Object));
        Assert.That(exception!.Message, Does.Contain(scenario switch
        {
            "missing" => "Unknown configured row key",
            "targeted" => "Cannot anonymize configured row key",
            "existing-primary-key" => "already has a primary key",
            "unused" => "no matching rule",
            _ => "non-empty"
        }));
    }

    [TestCase("CustomerCode", false)]
    [TestCase("FirstName", true)]
    public async Task Configured_key_is_not_reported_as_anonymizable(string column, bool expected)
    {
        var table = new TableDescription("dbo.LegacyCustomers", 1, []);
        var script = new ScriptSpecification(new DatabaseSpecification(DatabaseType.SqlServer, ""),
            ImmutableArray.Create(new Rule(table.Name, column, null, "Name")))
        {
            TableKeys = ImmutableDictionary<string, string>.Empty.Add(table.Name, "CustomerCode")
        };
        var runner = new ScriptRunner(new Mock<IDatabaseAnalyzer>().Object, new BogusDataGeneratorFactory());
        Assert.That(await runner.IsImpactedAsync(script, table, new FieldDescription(column, DateType.Text, false)),
            Is.EqualTo(expected));
    }

    [Test]
    public void Missing_key_error_explains_how_to_configure_one()
    {
        var database = new DatabaseSpecification(DatabaseType.PostgreSQL, "invalid connection string");
        var table = new TableDescription("public.customers", 0,
            ImmutableArray.Create(new FieldDescription("name", DateType.Text, false)));
        var analyzer = new Mock<IDatabaseAnalyzer>();
        analyzer.Setup(a => a.DescribeDatabaseAsync(database)).ReturnsAsync(
            new DatabaseDescription("test", ImmutableArray.Create(table)));
        var runner = new ScriptRunner(analyzer.Object, new BogusDataGeneratorFactory());
        var script = new ScriptSpecification(database,
            ImmutableArray.Create(new Rule(table.Name, "name", null, "Name")));
        Assert.That(async () => await runner.RunAsync(script, new Mock<IProgressReporter>().Object),
            Throws.InvalidOperationException.With.Message.Contains("tableKeys"));
    }

    private static async Task<ScriptSpecification> LoadYaml(string yaml)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, yaml);
            return await new YamlScriptDomProvider(path).LoadScriptAsync();
        }
        finally { File.Delete(path); }
    }
}
