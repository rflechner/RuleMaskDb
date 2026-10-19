using Microsoft.SqlServer.TransactSql.ScriptDom;
using Npgsql;
using RuleMaskDb.ScriptDom;
using RuleMaskDb.SqlDomain;

namespace RuleMaskDb.Tests;

public class StaticPaginationTests
{
    [TestCase(DatabaseType.SqlServer, false)]
    [TestCase(DatabaseType.SqlServer, true)]
    [TestCase(DatabaseType.PostgreSQL, false)]
    [TestCase(DatabaseType.PostgreSQL, true)]
    public void Key_page_uses_strict_composite_order_and_parameterized_continuation(DatabaseType type, bool continuation)
    {
        var driver = new RelationalDriver(type);
        using var connection = driver.CreateConnection("");
        var table = new TableDescription("unused", 0, []) { Schema = "s.]\"", LocalName = "t.]\"" };
        string[] keys = ["Tenant]\"", "Id]\""];
        const string lastValue = "O'Brien'; DROP TABLE People; --";
        using var command = driver.CreateKeyPage(connection, table, keys, 100, continuation ? [7, lastValue] : null);
        var first = driver.QuoteIdentifier(keys[0]);
        var second = driver.QuoteIdentifier(keys[1]);

        Assert.That(command.CommandText, Does.Contain($"ORDER BY {first}, {second}"));
        Assert.That(command.CommandText, Does.Contain(driver.QuoteTable(table)));
        Assert.That(command.CommandText, Does.Not.Contain("OFFSET").And.Not.Contain(lastValue));
        Assert.That(command.Parameters[0].Value, Is.EqualTo(100));
        Assert.That(command.Parameters.Count, Is.EqualTo(continuation ? 3 : 1));
        if (continuation)
        {
            Assert.That(command.CommandText, Does.Contain($"WHERE ({first} > @last0) OR ({first} = @last0 AND {second} > @last1)"));
            Assert.That(command.Parameters[2].Value, Is.EqualTo(lastValue));
        }
        else
            Assert.That(command.CommandText, Does.Not.Contain("WHERE"));

        Assert.That(command.CommandText, Does.Contain(type == DatabaseType.SqlServer ? "TOP (@pageSize)" : "LIMIT @pageSize"));
        if (type == DatabaseType.SqlServer) AssertValidSqlServer(command.CommandText);
    }

    [TestCase(DatabaseType.SqlServer)]
    [TestCase(DatabaseType.PostgreSQL)]
    public void Static_page_sends_values_once_and_reports_each_updated_key(DatabaseType type)
    {
        var driver = new RelationalDriver(type);
        using var connection = driver.CreateConnection(type == DatabaseType.SqlServer
            ? "Command Timeout=0" : "Host=localhost;Command Timeout=0");
        var table = new TableDescription("unused", 0,
            [new("Text", DateType.Text, false) { MaxLength = 5, NativeTypeName = "varchar" }])
            { Schema = "odd.schema", LocalName = "t]\"" };
        using var command = driver.CreateStaticUpdateBatch(connection, table, ["Tenant", "Id"], ["Text", "Nullable"],
            [[1, 7], [2, 7]], ["Alexander'; --", null]);

        Assert.That(command.Parameters.Count, Is.EqualTo(6));
        Assert.That(command.Parameters[0].Value, Is.EqualTo("Alexander'; --"));
        Assert.That(command.Parameters[1].Value, Is.EqualTo(DBNull.Value));
        Assert.That(command.CommandTimeout, Is.Zero);
        Assert.That(command.CommandText, Does.Contain("CAST(@p0 AS varchar(5))").And.Not.Contain("Alexander"));
        Assert.That(command.CommandText, Does.Contain("(VALUES (0, @key0_0, @key0_1), (1, @key1_0, @key1_1))"));
        foreach (var (key, index) in new[] { ("Tenant", 0), ("Id", 1) })
            Assert.That(command.CommandText, Does.Contain($"t.{driver.QuoteIdentifier(key)} = s.{driver.QuoteIdentifier($"c{index}")}"));

        if (type == DatabaseType.SqlServer)
        {
            Assert.That(command.CommandText, Does.Contain("OUTPUT s.[row_index] INTO @updated"));
            AssertValidSqlServer(command.CommandText);
        }
        else
        {
            Assert.That(((NpgsqlParameter)command.Parameters[1]).NpgsqlDbType, Is.EqualTo(NpgsqlTypes.NpgsqlDbType.Unknown));
            Assert.That(command.CommandText, Does.Contain("RETURNING s.\"row_index\""));
        }
    }

    [TestCase(DatabaseType.SqlServer, 999)]
    [TestCase(DatabaseType.PostgreSQL, 32766)]
    public void Static_page_reserves_shared_values_in_parameter_budget(DatabaseType type, int expected)
        => Assert.That(new RelationalDriver(type).GetEffectiveBatchSize(int.MaxValue, 2, 2), Is.EqualTo(expected));

    [Test]
    public void Oversized_static_pages_are_rejected_before_execution()
    {
        var driver = new RelationalDriver(DatabaseType.SqlServer);
        using var connection = driver.CreateConnection("");
        Assert.Throws<ArgumentException>(() => driver.CreateStaticUpdateBatch(connection, new("People", 0, []),
            ["Id"], ["Value"], Enumerable.Range(0, 2000).Select(i => new object?[] { i }).ToArray(), ["x"]));
    }

    private static void AssertValidSqlServer(string commandText)
    {
        using var sql = new StringReader(commandText);
        new TSql160Parser(true).Parse(sql, out var errors);
        Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(error => error.Message)));
    }
}
