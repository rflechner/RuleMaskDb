using System.Collections.Immutable;
using Moq;
using RuleMaskDb.Generators;
using RuleMaskDb.ScriptDom;
using RuleMaskDb.SqlDomain;
using RuleMaskDb.Yaml;

namespace RuleMaskDb.Tests;

public sealed class BeforeTableSqlTests
{
    [TestCase("")]
    [TestCase("beforeTableSql: {}")]
    public async Task Existing_scripts_default_to_no_preparation(string yaml)
        => Assert.That((await LoadYamlAsync(yaml)).BeforeTableSql, Is.Empty);

    [Test]
    public async Task Yaml_preserves_multiline_sql_and_exact_table_names()
    {
        var script = await LoadYamlAsync("""
            beforeTableSql:
              dbo.Users: |
                -- Prepare the rows before generation.
                DELETE FROM [dbo].[Users] WHERE [IsTest] = 1;
                UPDATE [dbo].[Users] SET [Name] = 'O''Brien; test';
              'public.Other Table': 'UPDATE "public"."Other Table" SET "Name" = NULL;'
            rules:
              - table: dbo.Users
                column: Name
              - table: public.Other Table
                column: Name
            """);
        Assert.That(script.BeforeTableSql.Count, Is.EqualTo(2));
        Assert.That(script.BeforeTableSql["dbo.Users"], Is.EqualTo(
            "-- Prepare the rows before generation.\nDELETE FROM [dbo].[Users] WHERE [IsTest] = 1;\nUPDATE [dbo].[Users] SET [Name] = 'O''Brien; test';\n"));
        Assert.That(script.BeforeTableSql["public.Other Table"], Is.EqualTo("UPDATE \"public\".\"Other Table\" SET \"Name\" = NULL;"));
    }

    [TestCase("'': SELECT 1;", "non-empty")]
    [TestCase("dbo.Users: ''", "non-empty")]
    [TestCase("dbo.Users: '   '", "non-empty")]
    [TestCase("dbo.Users: null", "non-empty")]
    [TestCase("dbo.Missing: SELECT 1;", "no matching")]
    [TestCase("dbo.users: SELECT 1;", "no matching")]
    public void Invalid_yaml_hooks_are_rejected(string entry, string message)
        => Assert.That(async () => await LoadYamlAsync($"""
            beforeTableSql:
              {entry}
            rules:
              - table: dbo.Users
                column: Name
            """), Throws.InvalidOperationException.With.Message.Contains(message));

    [Test]
    public void Duplicate_table_entries_are_rejected()
        => Assert.That(async () => await LoadYamlAsync("""
            beforeTableSql:
              dbo.Users: SELECT 1;
              dbo.Users: SELECT 2;
            rules:
              - table: dbo.Users
                column: Name
            """), Throws.InstanceOf<YamlDotNet.Core.YamlException>());

    [TestCase("dbo.Users", " ", "non-empty")]
    [TestCase("dbo.Users", null, "non-empty")]
    [TestCase(" ", "SELECT 1;", "non-empty")]
    [TestCase("dbo.Other", "SELECT 1;", "no matching")]
    public void Programmatic_scripts_are_validated_before_database_access(string table, string? sql, string message)
    {
        var script = new ScriptSpecification(new(DatabaseType.SqlServer, "invalid"), [new("dbo.Users", "Name", null, "Name")])
        {
            BeforeTableSql = ImmutableDictionary<string, string>.Empty.Add(table, sql!)
        };
        var analyzer = new Mock<IDatabaseAnalyzer>(MockBehavior.Strict);
        Assert.That(async () => await new ScriptRunner(analyzer.Object, new BogusDataGeneratorFactory())
            .RunAsync(script, Mock.Of<IProgressReporter>()), Throws.InvalidOperationException.With.Message.Contains(message));
        analyzer.VerifyNoOtherCalls();
    }

    [Test]
    public void Truncate_only_tables_cannot_have_anonymization_hooks()
    {
        var script = new ScriptSpecification(new(DatabaseType.SqlServer, "invalid"), [])
        {
            TruncateTables = ["dbo.Users"],
            BeforeTableSql = ImmutableDictionary<string, string>.Empty.Add("dbo.Users", "SELECT 1;")
        };
        Assert.That(script.ValidateBeforeTableSql, Throws.InvalidOperationException.With.Message.Contains("no matching"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void All_rules_are_validated_before_any_hook_executes(bool ambiguous)
    {
        var database = new DatabaseSpecification(DatabaseType.SqlServer, "invalid connection string");
        var table = new TableDescription("dbo.Users", 0,
            [new("Id", DateType.Text, true), new("Name", DateType.Text, false)]);
        var analyzer = new Mock<IDatabaseAnalyzer>();
        analyzer.Setup(a => a.DescribeDatabaseAsync(database)).ReturnsAsync(
            new DatabaseDescription("test", ambiguous ? [table, table] : [table]));
        var script = new ScriptSpecification(database, [new(table.Name, "Missing", null, "Name")])
        {
            BeforeTableSql = ImmutableDictionary<string, string>.Empty.Add(table.Name, "DELETE FROM dbo.Users;")
        };
        Assert.That(async () => await new ScriptRunner(analyzer.Object, new BogusDataGeneratorFactory())
            .RunAsync(script, Mock.Of<IProgressReporter>()), Throws.InvalidOperationException.With.Message.Contains(
                ambiguous ? "missing or ambiguous" : "Unknown column"));
    }

    private static async Task<ScriptSpecification> LoadYamlAsync(string yaml)
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
