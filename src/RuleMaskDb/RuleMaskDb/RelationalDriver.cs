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

    public string QuoteTable(TableDescription table)
    {
        if (table.Schema is not null && table.LocalName is not null)
            return QuoteIdentifier(table.Schema) + "." + QuoteIdentifier(table.LocalName);
        // Compatibility for descriptions supplied by existing callers.
        return string.Join(".", table.Name.Split('.').Select(QuoteIdentifier));
    }
}
