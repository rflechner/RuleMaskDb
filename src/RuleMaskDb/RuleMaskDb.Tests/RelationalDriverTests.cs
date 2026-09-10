using RuleMaskDb.ScriptDom;
using RuleMaskDb.SqlDomain;

namespace RuleMaskDb.Tests;

public class RelationalDriverTests
{
    [TestCase(DatabaseType.SqlServer, "a]b", "[a]]b]")]
    [TestCase(DatabaseType.PostgreSQL, "a\"b", "\"a\"\"b\"")]
    public void Escapes_identifier_delimiters(DatabaseType type, string name, string expected)
        => Assert.That(new RelationalDriver(type).QuoteIdentifier(name), Is.EqualTo(expected));

    [TestCase(DatabaseType.SqlServer, "[a.b].[c.d]")]
    [TestCase(DatabaseType.PostgreSQL, "\"a.b\".\"c.d\"")]
    public void Schema_and_local_name_are_quoted_separately(DatabaseType type, string expected)
    {
        var table = new TableDescription("a.b.c.d", 0, []) { Schema = "a.b", LocalName = "c.d" };
        Assert.That(new RelationalDriver(type).QuoteTable(table), Is.EqualTo(expected));
    }

    [Test]
    public void Unsupported_database_fails_explicitly()
        => Assert.Throws<NotSupportedException>(() => new RelationalDriver(DatabaseType.RavenDb).CreateConnection(""));
}
