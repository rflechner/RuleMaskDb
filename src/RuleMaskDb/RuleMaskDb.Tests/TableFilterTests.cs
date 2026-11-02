using System.Collections.Immutable;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Moq;
using RuleMaskDb.Generators;
using RuleMaskDb.ScriptDom;
using RuleMaskDb.SqlDomain;
using RuleMaskDb.Yaml;

namespace RuleMaskDb.Tests;

public class TableFilterTests
{
    private static readonly TableFilter Filter = new("Discriminator", ["Person", "Partner' OR 1=1 --"]);

    [Test]
    public async Task Yaml_parses_table_filters_without_changing_flat_rules()
    {
        const string yaml = """
            database:
              type: PostgreSQL
              connectionString: demo
            tables:
              - name: public.customers
                filter:
                  column: Discriminator
                  values:
                    - IndividualCustomer
                    - BusinessCustomer
            rules:
              - table: public.customers
                column: name
                generator: Name
            """;
        var script = await ParseYamlAsync(yaml);

        Assert.That(script.Rules, Has.Length.EqualTo(1));
        Assert.That(script.TableFilters.Keys, Is.EquivalentTo(["public.customers"]));
        Assert.That(script.TableFilters["public.customers"].Column, Is.EqualTo("Discriminator"));
        Assert.That(script.TableFilters["public.customers"].Values.ToArray(),
            Is.EqualTo(new object?[] { "IndividualCustomer", "BusinessCustomer" }));
    }

    [Test]
    public async Task Yaml_without_tables_preserves_unfiltered_behavior()
    {
        var script = await ParseYamlAsync("rules:\n  - table: Customers\n    column: Name\n    value: hidden");
        Assert.That(script.TableFilters.IsEmpty, Is.True);
    }

    [TestCase("{}", "column must not be empty")]
    [TestCase("{column: Kind, values: []}", "values must not be empty")]
    [TestCase("{column: Kind, values: [null]}", "non-null scalars")]
    [TestCase("{column: Kind, values: [[A, B]]}", "non-null scalars")]
    public void Invalid_yaml_filters_fail_during_loading(string filter, string expectedError)
    {
        var yaml = $"tables:\n  - name: Customers\n    filter: {filter}\nrules:\n  - table: Customers\n    column: Name\n    value: hidden";
        Assert.That(async () => await ParseYamlAsync(yaml),
            Throws.InvalidOperationException.With.Message.Contains(expectedError));
    }

    [Test]
    public void Duplicate_or_unmatched_table_configs_are_rejected()
    {
        const string duplicate = """
            tables:
              - name: Customers
                filter: {column: Kind, values: [Person]}
              - name: Customers
                filter: {column: Kind, values: [Company]}
            rules:
              - table: Customers
                column: Name
                value: hidden
            """;
        Assert.That(async () => await ParseYamlAsync(duplicate),
            Throws.InvalidOperationException.With.Message.Contains("Duplicate tables entry"));

        const string unmatched = """
            tables:
              - name: Other
                filter: {column: Kind, values: [Person]}
            rules:
              - table: Customers
                column: Name
                value: hidden
            """;
        Assert.That(async () => await ParseYamlAsync(unmatched),
            Throws.InvalidOperationException.With.Message.Contains("no matching anonymization rule"));
    }

    [TestCase(DatabaseType.SqlServer)]
    [TestCase(DatabaseType.PostgreSQL)]
    public void Filtered_key_page_combines_predicate_and_composite_continuation_safely(DatabaseType type)
    {
        var driver = new RelationalDriver(type);
        using var connection = driver.CreateConnection("");
        var table = new TableDescription("dbo.Customers", 5, []);
        using var command = driver.CreateKeyPage(connection, table, ["Tenant", "Id"], 2, [1, 4], Filter);
        var key1 = driver.QuoteIdentifier("Tenant");
        var key2 = driver.QuoteIdentifier("Id");

        Assert.That(command.CommandText, Does.Contain(
            $"WHERE {driver.QuoteIdentifier(Filter.Column)} IN (@filter0, @filter1) AND (({key1} > @last0) OR ({key1} = @last0 AND {key2} > @last1))"));
        Assert.That(command.CommandText, Does.Not.Contain("OR 1=1"));
        Assert.That(command.Parameters.Count, Is.EqualTo(5));
        Assert.That(command.Parameters[4].Value, Is.EqualTo(Filter.Values[1]));
        AssertSqlServerParses(type, command.CommandText);
    }

    [TestCase(DatabaseType.SqlServer)]
    [TestCase(DatabaseType.PostgreSQL)]
    public void Generated_and_static_batches_include_the_filter_in_the_update_join(DatabaseType type)
    {
        var driver = new RelationalDriver(type);
        using var connection = driver.CreateConnection("");
        var table = new TableDescription("dbo.Customers", 5, []);
        var predicate = $"t.{driver.QuoteIdentifier(Filter.Column)} IN (@filter0, @filter1)";

        using var generated = driver.CreateUpdateBatch(connection, table, ["Id"], ["Name"],
            [[1, "masked"]], ["int", "varchar"], Filter);
        Assert.That(generated.CommandText, Does.Contain(predicate));
        Assert.That(generated.CommandText, Does.Not.Contain("OR 1=1"));
        Assert.That(generated.Parameters.Count, Is.EqualTo(4));
        AssertSqlServerParses(type, generated.CommandText);

        using var statics = driver.CreateStaticUpdateBatch(connection, table, ["Id"], ["Name"],
            [[1], [4]], ["hidden"], Filter);
        Assert.That(statics.CommandText, Does.Contain(predicate));
        Assert.That(statics.Parameters.Count, Is.EqualTo(5));
        AssertSqlServerParses(type, statics.CommandText);
    }

    [TestCase(DatabaseType.SqlServer)]
    [TestCase(DatabaseType.PostgreSQL)]
    public void Keyless_static_update_filters_in_its_where_clause(DatabaseType type)
    {
        var driver = new RelationalDriver(type);
        using var connection = driver.CreateConnection("");
        var table = new TableDescription("dbo.Customers", 5, []);
        using var command = driver.CreateStaticUpdate(connection, table, ["Name"], ["hidden"], Filter);

        Assert.That(command.CommandText, Does.EndWith(
            $"WHERE {driver.QuoteIdentifier(Filter.Column)} IN (@filter0, @filter1)"));
        Assert.That(command.Parameters.Count, Is.EqualTo(3));
        Assert.That(command.CommandText, Does.Not.Contain("OR 1=1"));
        AssertSqlServerParses(type, command.CommandText);
    }

    [TestCase("unknown", "Unknown filter column")]
    [TestCase("target", "Cannot anonymize filter column")]
    [TestCase("orphan", "no matching anonymization rule")]
    [TestCase("empty", "values must not be empty")]
    public void Invalid_filter_config_fails_preflight_before_any_connection(string scenario, string expectedError)
    {
        var database = new DatabaseSpecification(DatabaseType.SqlServer, "not a connection string");
        var table = new TableDescription("Customers", 5,
            [new("Id", DateType.IntegerNumber, true), new("Name", DateType.Text, false),
             new("Discriminator", DateType.Text, false)]);
        var analyzer = new Mock<IDatabaseAnalyzer>();
        analyzer.Setup(a => a.DescribeDatabaseAsync(database)).ReturnsAsync(new DatabaseDescription("db", [table]));
        var rules = new Rule[]
        {
            new("Customers", scenario == "target" ? "Discriminator" : "Name", null, (string?)null) { Value = "hidden" }
        };
        var filter = scenario switch
        {
            "unknown" => new TableFilter("Missing", ["Person"]),
            "empty" => new TableFilter("Discriminator", []),
            _ => new TableFilter("Discriminator", ["Person"])
        };
        var script = new ScriptSpecification(database, [..rules])
        {
            TableFilters = ImmutableDictionary<string, TableFilter>.Empty.Add(
                scenario == "orphan" ? "Other" : "Customers", filter)
        };
        var factory = new Mock<IDataGeneratorFactory>(MockBehavior.Strict);
        Assert.That(async () => await new ScriptRunner(analyzer.Object, factory.Object)
                .RunAsync(script, Mock.Of<IProgressReporter>()),
            Throws.InvalidOperationException.With.Message.Contains(expectedError));
        factory.VerifyNoOtherCalls();
    }

    private static void AssertSqlServerParses(DatabaseType type, string query)
    {
        if (type != DatabaseType.SqlServer) return;
        using var reader = new StringReader(query);
        new TSql160Parser(true).Parse(reader, out var errors);
        Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(error => error.Message)));
    }

    private static async Task<ScriptSpecification> ParseYamlAsync(string yaml)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, yaml);
            return await new YamlScriptDomProvider(path).LoadScriptAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
