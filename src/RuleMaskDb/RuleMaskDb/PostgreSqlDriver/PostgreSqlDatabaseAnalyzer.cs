using Npgsql;
using RuleMaskDb.ScriptDom;
using RuleMaskDb.SqlDomain;

namespace RuleMaskDb.PostgreSqlDriver;

public sealed class PostgreSqlDatabaseAnalyzer : IDatabaseAnalyzer
{
    public async Task<DatabaseDescription> DescribeDatabaseAsync(DatabaseSpecification specification)
    {
        if (specification.DatabaseType != DatabaseType.PostgreSQL)
            throw new NotSupportedException($"Unsupported database type: {specification.DatabaseType}");
        await using var connection = new NpgsqlConnection(specification.ConnectionString);
        await connection.OpenAsync();
        const string sql = """
            SELECT n.nspname, c.relname, a.attname, t.typname,
                   EXISTS (SELECT 1 FROM pg_index i WHERE i.indrelid = c.oid
                           AND i.indisprimary AND a.attnum = ANY(i.indkey))
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid
            JOIN pg_type t ON t.oid = a.atttypid
            WHERE c.relkind IN ('r', 'p') AND NOT c.relispartition
              AND n.nspname <> 'information_schema' AND n.nspname !~ '^pg_'
              AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY n.nspname, c.relname, a.attnum
            """;
        var columns = new Dictionary<(string Schema, string Name), List<FieldDescription>>();
        await using (var command = new NpgsqlCommand(sql, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!columns.TryGetValue(key, out var fields)) columns[key] = fields = [];
                fields.Add(new FieldDescription(reader.GetString(2), MapType(reader.GetString(3)), reader.GetBoolean(4)));
            }
        }
        var tables = new List<TableDescription>();
        var driver = new RelationalDriver(DatabaseType.PostgreSQL);
        foreach (var (key, fields) in columns)
        {
            var table = new TableDescription($"{key.Schema}.{key.Name}", 0, [..fields])
                { Schema = key.Schema, LocalName = key.Name };
            await using var count = new NpgsqlCommand($"SELECT COUNT(*) FROM {driver.QuoteTable(table)}", connection);
            var rowCount = Convert.ToInt64(await count.ExecuteScalarAsync());
            tables.Add(table with { RowCount = (int)Math.Min(int.MaxValue, rowCount) });
        }
        return new DatabaseDescription(connection.Database, [..tables]);
    }

    private static DateType MapType(string type) => type switch
    {
        "int2" or "int4" or "int8" => DateType.IntegerNumber,
        "numeric" or "float4" or "float8" or "money" => DateType.DecimalNumber,
        "date" => DateType.Date,
        "timestamp" or "timestamptz" => DateType.DateTime,
        "bool" => DateType.Boolean,
        _ => DateType.Text
    };
}
