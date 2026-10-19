using Npgsql;
using RuleMaskDb.ScriptDom;
using RuleMaskDb.SqlDomain;
using RuleMaskDb.Yaml;

namespace RuleMaskDb.Tests;

public class BatchUpdateTests
{
    [TestCase("", 200)]
    [TestCase("batchSize: 100", 100)]
    [TestCase("batchSize: 200", 200)]
    [TestCase("batchSize: 1", 1)]
    public async Task Yaml_configures_batch_size(string yaml, int expected)
        => Assert.That((await Load(yaml)).BatchSize, Is.EqualTo(expected));

    [TestCase(0)]
    [TestCase(-1)]
    public void Invalid_batch_sizes_are_rejected(int size)
    {
        Assert.That(async () => await Load($"batchSize: {size}"), Throws.InvalidOperationException);
        Assert.Throws<InvalidOperationException>(() => new ScriptSpecification(new(DatabaseType.SqlServer, ""), [])
            { BatchSize = size }.ValidateBatchSize());
    }

    [TestCase(DatabaseType.SqlServer, 200, 3, 200)]
    [TestCase(DatabaseType.SqlServer, 200, 21, 95)]
    [TestCase(DatabaseType.SqlServer, int.MaxValue, 2, 1000)]
    [TestCase(DatabaseType.PostgreSQL, 200, 3, 200)]
    [TestCase(DatabaseType.PostgreSQL, int.MaxValue, 2, 32767)]
    public void Batch_size_obeys_parameter_budget(DatabaseType type, int requested, int width, int expected)
        => Assert.That(new RelationalDriver(type).GetEffectiveBatchSize(requested, width), Is.EqualTo(expected));

    [TestCase(DatabaseType.SqlServer)]
    [TestCase(DatabaseType.PostgreSQL)]
    public void Batch_is_one_parameterized_update_with_composite_key_join(DatabaseType type)
    {
        var driver = new RelationalDriver(type);
        using var connection = driver.CreateConnection("");
        var table = new TableDescription("ignored", 0, []) { Schema = "s.ch]e\"ma", LocalName = "t.ab]l\"e" };
        var keys = new[] { "tenant]id", "row\"id" };
        var columns = new[] { "na]m\"e" };
        const string value = "O'Brien'); DELETE FROM Users; --";
        using var command = driver.CreateUpdateBatch(connection, table, keys, columns,
            [ [1, 7, value], [2, 7, "second"] ], ["integer", "integer", "text"]);
        Assert.That(command.Parameters.Count, Is.EqualTo(6));
        Assert.That(command.Parameters[2].Value, Is.EqualTo(value));
        Assert.That(command.CommandText, Does.Not.Contain(value));
        Assert.That(command.CommandText, Does.Contain(driver.QuoteTable(table)));
        Assert.That(command.CommandText, Does.Contain("t." + driver.QuoteIdentifier(keys[0]) + " = s." + driver.QuoteIdentifier("c0")));
        Assert.That(command.CommandText, Does.Contain("t." + driver.QuoteIdentifier(keys[1]) + " = s." + driver.QuoteIdentifier("c1")));
        Assert.That(command.CommandText.Split("UPDATE").Length - 1, Is.EqualTo(1));
        Assert.That(command.CommandText, Does.Contain("(VALUES (0, @p0_0, @p0_1, @p0_2), (1, @p1_0, @p1_1, @p1_2))"));
        Assert.That(command.CommandText, Does.Contain(type == DatabaseType.PostgreSQL ? "RETURNING" : "OUTPUT s.[row_index] INTO @updated"));
    }

    [TestCase("integer")]
    [TestCase("boolean")]
    [TestCase("uuid")]
    [TestCase("timestamp without time zone")]
    public void PostgreSql_nulls_keep_the_target_type(string typeName)
    {
        var driver = new RelationalDriver(DatabaseType.PostgreSQL);
        using var connection = driver.CreateConnection("");
        using var command = driver.CreateUpdateBatch(connection, new("People", 0, []), ["Id"], ["Value"],
            [[1, null], [2, DBNull.Value]], ["integer", typeName]);
        Assert.That(((NpgsqlParameter)command.Parameters[1]).DataTypeName, Is.EqualTo(typeName));
        Assert.That(((NpgsqlParameter)command.Parameters[3]).DataTypeName, Is.EqualTo(typeName));
    }

    [Test]
    public void Oversized_batches_and_rows_fail_before_execution()
    {
        var driver = new RelationalDriver(DatabaseType.SqlServer);
        using var connection = driver.CreateConnection("");
        Assert.Throws<InvalidOperationException>(() => driver.GetEffectiveBatchSize(100, 2001));
        Assert.Throws<ArgumentException>(() => driver.CreateUpdateBatch(connection, new("People", 0, []),
            ["Id"], ["Value"], Enumerable.Range(0, 1001).Select(i => new object?[] { i, "x" }).ToArray(), ["int", "nvarchar"]));
    }

    private static async Task<ScriptSpecification> Load(string yaml)
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
