using System.Data.Common;
using Microsoft.Data.SqlClient;
using Npgsql;
using RuleMaskDb.ScriptDom;
using RuleMaskDb.SqlDomain;

namespace RuleMaskDb;

public sealed class RelationalDriver(DatabaseType databaseType)
{
    public DbConnection CreateConnection(string connectionString) => databaseType switch
    {
        DatabaseType.SqlServer => new SqlConnection(connectionString),
        DatabaseType.PostgreSQL => new NpgsqlConnection(connectionString),
        _ => throw new NotSupportedException($"Unsupported database type: {databaseType}")
    };

    public string QuoteIdentifier(string name) => databaseType switch
    {
        DatabaseType.SqlServer => "[" + name.Replace("]", "]]") + "]",
        DatabaseType.PostgreSQL => "\"" + name.Replace("\"", "\"\"") + "\"",
        _ => throw new NotSupportedException($"Unsupported database type: {databaseType}")
    };

    public string[] CreateTruncateCommands(IReadOnlyList<TableDescription> tables)
    {
        if (tables.Count == 0) return [];
        var names = tables.Select(QuoteTable).ToArray();
        return databaseType switch
        {
            // One statement allows explicitly selected related tables to be truncated together.
            DatabaseType.PostgreSQL => [$"TRUNCATE TABLE {string.Join(", ", names)} CONTINUE IDENTITY RESTRICT"],
            DatabaseType.SqlServer => names.Select(name => $"TRUNCATE TABLE {name}").ToArray(),
            _ => throw new NotSupportedException($"Unsupported database type: {databaseType}")
        };
    }

    public string QuoteTable(TableDescription table)
    {
        if (table.Schema is not null && table.LocalName is not null)
            return QuoteIdentifier(table.Schema) + "." + QuoteIdentifier(table.LocalName);
        // Compatibility for descriptions supplied by existing callers.
        return string.Join(".", table.Name.Split('.').Select(QuoteIdentifier));
    }
}
