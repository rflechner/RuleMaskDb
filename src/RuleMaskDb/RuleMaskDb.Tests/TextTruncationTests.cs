using TSql160Parser = Microsoft.SqlServer.TransactSql.ScriptDom.TSql160Parser;
using RuleMaskDb.ScriptDom;
using RuleMaskDb.SqlDomain;

namespace RuleMaskDb.Tests;

public class TextTruncationTests
{
    [TestCase(DatabaseType.SqlServer, "varchar", "varchar")]
    [TestCase(DatabaseType.SqlServer, "nvarchar", "nvarchar")]
    [TestCase(DatabaseType.SqlServer, "char", "char")]
    [TestCase(DatabaseType.SqlServer, "nchar", "nchar")]
    [TestCase(DatabaseType.PostgreSQL, "varchar", "varchar")]
    [TestCase(DatabaseType.PostgreSQL, "bpchar", "char")]
    public void Bounded_text_is_cast_to_destination_length(DatabaseType databaseType, string nativeType, string castType)
    {
        var field = new FieldDescription("Value", DateType.Text, false)
            { NativeTypeName = nativeType, MaxLength = 5 };
        var driver = new RelationalDriver(databaseType);
        using var connection = driver.CreateConnection("");
        using var command = driver.CreateUpdateBatch(connection, new("People", 0, [field]),
            ["Id"], ["Value"], [[1, "Alexander"], [2, null]], ["integer", nativeType]);
        Assert.That(command.CommandText, Does.Contain($"CAST(s.{driver.QuoteIdentifier("c1")} AS {castType}(5))"));
        Assert.That(command.Parameters[1].Value, Is.EqualTo("Alexander"));
        Assert.That(command.Parameters[3].Value, Is.EqualTo(DBNull.Value));
        Assert.That(command.CommandText, Does.Not.Contain("Alexander"));
        Assert.That(command.CommandText, Does.Not.Contain($"CAST(s.{driver.QuoteIdentifier("c0")}"));
    }

    [TestCase(DatabaseType.SqlServer, "nvarchar", -1)]
    [TestCase(DatabaseType.SqlServer, "text", 2147483647)]
    [TestCase(DatabaseType.SqlServer, "varbinary", 5)]
    [TestCase(DatabaseType.PostgreSQL, "varchar", null)]
    [TestCase(DatabaseType.PostgreSQL, "text", null)]
    [TestCase(DatabaseType.PostgreSQL, "numeric", 5)]
    [TestCase(DatabaseType.SqlServer, "varchar); DROP TABLE People; --", 5)]
    public void Unbounded_or_non_text_types_are_not_cast(DatabaseType type, string nativeType, int? length)
    {
        var field = new FieldDescription("Value", DateType.Text, false)
            { NativeTypeName = nativeType, MaxLength = length };
        var driver = new RelationalDriver(type);
        using var connection = driver.CreateConnection("");
        using var command = driver.CreateUpdateBatch(connection, new("People", 0, [field]),
            ["Id"], ["Value"], [[1, "unchanged"]], ["integer", nativeType]);
        Assert.That(command.CommandText, Does.Not.Contain("CAST("));
    }

    [Test]
    public void SqlServer_collation_batches_have_valid_TSql(
        [Values("Latin1_General_CI_AS", "SQL_Latin1_General_CP1_CI_AS",
            "Latin1_General_100_CI_AS_SC", "Latin1_General_100_CI_AS_SC_UTF8")] string collation,
        [Values("char", "varchar", "nchar", "nvarchar")] string nativeType)
    {
        var field = new FieldDescription("Value", DateType.Text, false)
            { NativeTypeName = nativeType, MaxLength = 5, Collation = collation };
        var driver = new RelationalDriver(DatabaseType.SqlServer);
        using var connection = driver.CreateConnection("");
        using var command = driver.CreateUpdateBatch(connection, new("People", 0, [field]),
            ["Id"], ["Value"], [[1, "ééé"], [2, null]], ["int", nativeType]);
        Assert.That(command.CommandText,
            Does.Contain($"CAST(s.[c1] COLLATE {collation} AS {nativeType}(5))"));
        // Parse the whole generated command so invalid syntax cannot be blessed by
        // a string assertion, even when a live SQL Server is not configured.
        using var sql = new StringReader(command.CommandText);
        new TSql160Parser(true).Parse(sql, out var errors);
        Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(e => e.Message)));
    }

    [Test]
    public void Regression_bracketed_collation_is_rejected_by_TSql_parser()
    {
        using var sql = new StringReader(
            "SELECT CAST(N'Alexander' COLLATE [Latin1_General_CI_AS] AS varchar(5));");
        new TSql160Parser(true).Parse(sql, out var errors);
        Assert.That(errors, Is.Not.Empty);
    }

    [TestCase("")]
    [TestCase(" Latin1_General_CI_AS")]
    [TestCase("Latin1_General_CI_AS\n")]
    [TestCase("[Latin1_General_CI_AS]")]
    [TestCase("Latin1_General_CI_AS; SELECT 1; --")]
    [TestCase("Latin1_General_CI_AS/**/")]
    [TestCase("1Latin1_General_CI_AS")]
    [TestCase("Latín1_General_CI_AS")]
    public void SqlServer_rejects_invalid_collation_metadata(string collation)
    {
        var field = new FieldDescription("Value", DateType.Text, false)
            { NativeTypeName = "varchar", MaxLength = 5, Collation = collation };
        var driver = new RelationalDriver(DatabaseType.SqlServer);
        using var connection = driver.CreateConnection("");
        Assert.That(() => driver.CreateUpdateBatch(connection, new("People", 0, [field]),
            ["Id"], ["Value"], [[1, "Alexander"]], ["int", "varchar"]),
            Throws.InvalidOperationException.With.Message.Contains("collation"));
    }
}
