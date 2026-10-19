using Microsoft.SqlServer.TransactSql.ScriptDom;
using Moq;
using Npgsql;
using RuleMaskDb.Generators;
using RuleMaskDb.ScriptDom;
using RuleMaskDb.SqlDomain;

namespace RuleMaskDb.Tests;

public class StaticUpdateTests
{
    [TestCase(DatabaseType.SqlServer)]
    [TestCase(DatabaseType.PostgreSQL)]
    public void One_parameterized_update_sets_all_columns_without_a_key_or_where(DatabaseType type)
    {
        var driver = new RelationalDriver(type);
        using var connection = driver.CreateConnection("");
        var table = new TableDescription("unused", 1000000, [])
            { Schema = "odd.schema", LocalName = "People]\" Records" };
        var columns = new[] { "Text]\"", "Empty", "Nullable", "Enabled", "Score", "Amount" };
        object?[] values = ["redacted'; DROP TABLE People; --", "", null, false, 42, 12.5m];
        using var command = driver.CreateStaticUpdate(connection, table, columns, values);

        Assert.That(command.CommandText, Is.EqualTo($"UPDATE {driver.QuoteTable(table)} SET "
            + string.Join(", ", columns.Select((c, i) => $"{driver.QuoteIdentifier(c)} = @p{i}"))));
        Assert.That(command.CommandText, Does.Not.Contain("WHERE").And.Not.Contain("SELECT").And.Not.Contain("DROP"));
        Assert.That(command.Parameters.Count, Is.EqualTo(columns.Length));
        for (var i = 0; i < values.Length; i++)
            Assert.That(command.Parameters[i].Value, Is.EqualTo(values[i] ?? DBNull.Value));
        if (type == DatabaseType.PostgreSQL)
            Assert.That(((NpgsqlParameter)command.Parameters[2]).NpgsqlDbType, Is.EqualTo(NpgsqlTypes.NpgsqlDbType.Unknown));
        else
        {
            using var sql = new StringReader(command.CommandText);
            new TSql160Parser(true).Parse(sql, out var errors);
            Assert.That(errors, Is.Empty);
        }
    }

    [TestCase(DatabaseType.SqlServer, "varchar", "varchar")]
    [TestCase(DatabaseType.SqlServer, "nvarchar", "nvarchar")]
    [TestCase(DatabaseType.SqlServer, "char", "char")]
    [TestCase(DatabaseType.SqlServer, "nchar", "nchar")]
    [TestCase(DatabaseType.PostgreSQL, "varchar", "varchar")]
    [TestCase(DatabaseType.PostgreSQL, "bpchar", "char")]
    public void Static_update_keeps_text_truncation_and_valid_collation_syntax(DatabaseType type, string nativeType, string castType)
    {
        var driver = new RelationalDriver(type);
        var field = new FieldDescription("Value", DateType.Text, false)
        {
            NativeTypeName = nativeType, MaxLength = 5,
            Collation = type == DatabaseType.SqlServer ? "Latin1_General_100_CI_AS_SC_UTF8" : null
        };
        using var connection = driver.CreateConnection("");
        using var command = driver.CreateStaticUpdate(connection, new("People", 205, [field]), ["Value"], ["Alexander"]);
        var expression = field.Collation is null ? "@p0" : "@p0 COLLATE " + field.Collation;
        Assert.That(command.CommandText, Does.Contain($"CAST({expression} AS {castType}(5))"));
        Assert.That(command.Parameters[0].Value, Is.EqualTo("Alexander"));
        if (type == DatabaseType.SqlServer)
        {
            using var sql = new StringReader(command.CommandText);
            new TSql160Parser(true).Parse(sql, out var errors);
            Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(e => e.Message)));
        }
    }

    [TestCase("primary-key", "Cannot anonymize primary key")]
    [TestCase("configured-key", "Cannot anonymize configured row key")]
    [TestCase("unknown", "Unknown column")]
    [TestCase("duplicate", "Duplicate column rules")]
    [TestCase("conflict", "cannot be combined")]
    [TestCase("later-table", "has no primary key")]
    public void Static_rules_keep_preflight_validation_before_any_connection(string scenario, string error)
    {
        var database = new DatabaseSpecification(DatabaseType.SqlServer, "invalid connection string");
        var table = new TableDescription("People", 2,
            [new("Id", DateType.IntegerNumber, true), new("Name", DateType.Text, false)]);
        var other = new TableDescription("Other", 2, [new("Name", DateType.Text, false)]);
        var analyzer = new Mock<IDatabaseAnalyzer>();
        analyzer.Setup(a => a.DescribeDatabaseAsync(database)).ReturnsAsync(new DatabaseDescription("test", [table, other]));
        var rule = new Rule("People", scenario == "primary-key" ? "Id" : scenario == "unknown" ? "Missing" : "Name", null, (string?)null)
            { Value = "fixed" };
        var script = new ScriptSpecification(database, [rule]);
        script = scenario switch
        {
            "configured-key" => script with
            {
                Rules = [rule with { Table = "Other" }],
                TableKeys = script.TableKeys.Add("Other", "Name")
            },
            "duplicate" => script with { Rules = [rule, rule] },
            "conflict" => script with { Rules = [rule with { Generator = "Name" }] },
            "later-table" => script with { Rules = [rule, new("Other", "Name", null, "Name")] },
            _ => script
        };
        var factory = new Mock<IDataGeneratorFactory>(MockBehavior.Strict);
        Assert.That(() => new ScriptRunner(analyzer.Object, factory.Object)
            .RunAsync(script, new Mock<IProgressReporter>().Object),
            Throws.InvalidOperationException.With.Message.Contains(error));
        factory.VerifyNoOtherCalls();
    }
}
