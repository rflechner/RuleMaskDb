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
    public void SqlServer_uses_escaped_destination_collation_before_cast()
    {
        var field = new FieldDescription("Value", DateType.Text, false)
            { NativeTypeName = "varchar", MaxLength = 5, Collation = "Latin1_General_100_CI_AS_SC_UTF8" };
        var driver = new RelationalDriver(DatabaseType.SqlServer);
        using var connection = driver.CreateConnection("");
        using var command = driver.CreateUpdateBatch(connection, new("People", 0, [field]),
            ["Id"], ["Value"], [[1, "ééé"]], ["int", "varchar"]);
        Assert.That(command.CommandText,
            Does.Contain("CAST(s.[c1] COLLATE [Latin1_General_100_CI_AS_SC_UTF8] AS varchar(5))"));
    }
}
