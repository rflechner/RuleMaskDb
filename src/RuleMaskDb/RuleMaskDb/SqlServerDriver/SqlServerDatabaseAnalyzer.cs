using System.Collections.Immutable;
using Microsoft.Data.SqlClient;

namespace RuleMaskDb.SqlServerDriver;

public class SqlServerDatabaseAnalyzer : IDatabaseAnalyzer
{
    public async Task<DatabaseDescription> DescribeDatabaseAsync(DatabaseSpecification databaseSpecification)
    {
        if (databaseSpecification.DatabaseType != DatabaseType.SqlServer)
            throw new NotSupportedException($"Unsupported database type: {databaseSpecification.DatabaseType}");

        await using var connection = new SqlConnection(databaseSpecification.ConnectionString);
        await connection.OpenAsync();

        var tables = await GetTables(connection);

        var columnsByTable = await GetColumnsByTable(connection);

        // Build table descriptions with row counts
        var tableDescriptions = await GetTableDescriptions(tables, columnsByTable, connection);

        return new DatabaseDescription(Name: connection.Database, Tables: [..tableDescriptions]);
    }

    private static async Task<List<(string Schema, string Name)>> GetTables(SqlConnection connection)
    {
        // Load tables (schema + name)
        var tables = new List<(string Schema, string Name)>();
        const string tablesSql = @"SELECT TABLE_SCHEMA, TABLE_NAME
                                   FROM INFORMATION_SCHEMA.TABLES
                                   WHERE TABLE_TYPE = 'BASE TABLE'";
        await using (var cmd = new SqlCommand(tablesSql, connection))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tables.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        return tables;
    }

    private static async Task<Dictionary<(string Schema, string Name), List<FieldDescription>>> GetColumnsByTable(SqlConnection connection)
    {
        // Load columns for all tables
        var columnsByTable = new Dictionary<(string Schema, string Name), List<FieldDescription>>();
        const string colsSql = @"SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, DATA_TYPE
                                 FROM INFORMATION_SCHEMA.COLUMNS";
        await using var cmdCols = new SqlCommand(colsSql, connection);
        await using var r = await cmdCols.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var schema = r.GetString(0);
            var table = r.GetString(1);
            var column = r.GetString(2);
            var dataType = r.GetString(3);

            var key = (schema, table);
            if (!columnsByTable.TryGetValue(key, out var list))
            {
                list = new List<FieldDescription>();
                columnsByTable[key] = list;
            }

            list.Add(new FieldDescription(
                Path: column,
                DataType: MapSqlTypeToDateType(dataType)));
        }

        return columnsByTable;
    }

    private static async Task<List<TableDescription>> GetTableDescriptions(List<(string Schema, string Name)> tables, Dictionary<(string Schema, string Name), List<FieldDescription>> columnsByTable, SqlConnection connection)
    {
        var tableDescriptions = new List<TableDescription>(tables.Count);
        foreach (var (schema, name) in tables)
        {
            var fields = columnsByTable.TryGetValue((schema, name), out var fl)
                ? [..fl]
                : ImmutableArray<FieldDescription>.Empty;

            var rowCount = await GetApproxRowCountAsync(connection, schema, name);

            tableDescriptions.Add(new TableDescription(
                Name: $"{schema}.{name}",
                RowCount: (int)Math.Min(int.MaxValue, rowCount),
                Fields: fields));
        }

        return tableDescriptions;
    }

    private static DateType MapSqlTypeToDateType(string sqlType)
    {
        return sqlType.ToLowerInvariant() switch
        {
            "char" or "nchar" or "varchar" or "nvarchar" or "text" or "ntext" or "xml" or "uniqueidentifier" or "binary" or "varbinary"
                or "image" => DateType.Text,
            "tinyint" or "smallint" or "int" or "bigint" => DateType.IntegerNumber,
            "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => DateType.DecimalNumber,
            "date" => DateType.Date,
            "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" => DateType.DateTime,
            "bit" => DateType.Boolean,
            _ => DateType.Text
        };
    }

    private static async Task<long> GetApproxRowCountAsync(SqlConnection connection, string schema, string table)
    {
        // Use dm_db_partition_stats for a fast approximate count
        const string sql = @"SELECT SUM(p.row_count)
                             FROM sys.dm_db_partition_stats AS p
                             WHERE p.object_id = OBJECT_ID(@fullName) AND p.index_id IN (0,1)";
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@fullName", $"[{schema}].[{table}]");
        var obj = await cmd.ExecuteScalarAsync();
        if (obj is null or DBNull) return 0L;
        return Convert.ToInt64(obj);
    }
}