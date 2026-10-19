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

    public int GetEffectiveBatchSize(int requestedSize, int parametersPerRow)
    {
        if (requestedSize <= 0) throw new ArgumentOutOfRangeException(nameof(requestedSize));
        if (parametersPerRow <= 0) throw new ArgumentOutOfRangeException(nameof(parametersPerRow));
        // Leave room for SQL Server's command metadata below the 2,100 parameter limit.
        var limit = databaseType switch
        {
            DatabaseType.SqlServer => 2000,
            DatabaseType.PostgreSQL => 65535,
            _ => throw new NotSupportedException($"Unsupported database type: {databaseType}")
        };
        if (parametersPerRow > limit)
            throw new InvalidOperationException($"A single update row requires {parametersPerRow} parameters; the supported limit is {limit}.");
        return Math.Min(requestedSize, limit / parametersPerRow);
    }

    public DbCommand CreateUpdateBatch(DbConnection connection, TableDescription table,
        IReadOnlyList<string> keys, IReadOnlyList<string> columns,
        IReadOnlyList<object?[]> rows, IReadOnlyList<string> dataTypeNames)
    {
        var width = keys.Count + columns.Count;
        if (keys.Count == 0 || columns.Count == 0 || rows.Count == 0
            || dataTypeNames.Count != width || rows.Any(r => r.Length != width))
            throw new ArgumentException("An update batch requires keys, columns, and equally sized rows and type metadata.");
        if (GetEffectiveBatchSize(rows.Count, width) != rows.Count)
            throw new ArgumentException("Update batch exceeds the parameter budget.");

        var command = connection.CreateCommand();
        try
        {
            var tuples = new List<string>();
            for (var row = 0; row < rows.Count; row++)
            {
                var names = new List<string> { row.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                for (var column = 0; column < width; column++)
                {
                    var name = $"p{row}_{column}";
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = name;
                    parameter.Value = rows[row][column] ?? DBNull.Value;
                    // VALUES cannot infer the target type when a whole PostgreSQL column is NULL.
                    if (parameter.Value is DBNull && parameter is NpgsqlParameter postgresParameter)
                        postgresParameter.DataTypeName = dataTypeNames[column];
                    command.Parameters.Add(parameter);
                    names.Add("@" + name);
                }
                tuples.Add("(" + string.Join(", ", names) + ")");
            }
            var aliases = new[] { "row_index" }.Concat(Enumerable.Range(0, width).Select(i => $"c{i}"));
            var source = $"(VALUES {string.Join(", ", tuples)}) AS s ({string.Join(", ", aliases.Select(QuoteIdentifier))})";
            var assignments = columns.Select((column, i) =>
                $"{QuoteIdentifier(column)} = {FitColumnValue(table, column, $"s.{QuoteIdentifier($"c{keys.Count + i}")}")}");
            var predicates = keys.Select((key, i) => $"t.{QuoteIdentifier(key)} = s.{QuoteIdentifier($"c{i}")}");
            var set = string.Join(", ", assignments);
            var join = string.Join(" AND ", predicates);
            command.CommandText = databaseType switch
            {
                DatabaseType.PostgreSQL => $"UPDATE {QuoteTable(table)} AS t SET {set} FROM {source} WHERE {join} RETURNING s.\"row_index\"",
                // OUTPUT INTO also supports target tables with enabled UPDATE triggers.
                DatabaseType.SqlServer => $"DECLARE @updated TABLE ([row_index] int); UPDATE t SET {set} OUTPUT s.[row_index] INTO @updated FROM {QuoteTable(table)} AS t INNER JOIN {source} ON {join}; SELECT [row_index] FROM @updated;",
                _ => throw new NotSupportedException($"Unsupported database type: {databaseType}")
            };
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    public DbCommand CreateStaticUpdate(DbConnection connection, TableDescription table,
        IReadOnlyList<string> columns, IReadOnlyList<object?> values)
    {
        if (columns.Count == 0 || columns.Count != values.Count)
            throw new ArgumentException("A static update requires columns and an equally sized list of values.");
        GetEffectiveBatchSize(1, columns.Count);

        var command = connection.CreateCommand();
        try
        {
            var assignments = new List<string>();
            for (var i = 0; i < columns.Count; i++)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = $"p{i}";
                parameter.Value = values[i] ?? DBNull.Value;
                // A direct SET assignment lets PostgreSQL infer NULL's type from the
                // destination column, including types absent from custom metadata.
                if (parameter.Value is DBNull && parameter is NpgsqlParameter postgresParameter)
                    postgresParameter.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Unknown;
                command.Parameters.Add(parameter);
                assignments.Add($"{QuoteIdentifier(columns[i])} = {FitColumnValue(table, columns[i], "@" + parameter.ParameterName)}");
            }
            command.CommandText = $"UPDATE {QuoteTable(table)} SET {string.Join(", ", assignments)}";
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    private string FitColumnValue(TableDescription table, string column, string expression)
    {
        var field = table.Fields.SingleOrDefault(f => f.Path == column);
        if (field?.MaxLength is not > 0) return expression;
        // Only allow known text types: never interpolate a provider-supplied SQL type.
        var type = (databaseType, field.NativeTypeName) switch
        {
            (DatabaseType.SqlServer, "char" or "varchar" or "nchar" or "nvarchar") => field.NativeTypeName,
            (DatabaseType.PostgreSQL, "varchar") => "varchar",
            (DatabaseType.PostgreSQL, "bpchar") => "char",
            _ => null
        };
        if (type is null) return expression;
        // Let the database apply its character/byte semantics. In particular, varchar
        // must use the destination code page (including UTF-8), not the database default.
        if (databaseType == DatabaseType.SqlServer && field.Collation is not null)
        {
            // COLLATE requires a bare name, not a bracket-delimited identifier.
            // Validate custom analyzer metadata before including it in command text.
            var collation = field.Collation;
            if (collation.Length is 0 or > 128 || !char.IsAsciiLetter(collation[0])
                || collation.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
                throw new InvalidOperationException($"Invalid SQL Server collation name for column '{column}'.");
            expression += " COLLATE " + collation;
        }
        var length = field.MaxLength.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"CAST({expression} AS {type}({length}))";
    }

    public string QuoteTable(TableDescription table)
    {
        if (table.Schema is not null && table.LocalName is not null)
            return QuoteIdentifier(table.Schema) + "." + QuoteIdentifier(table.LocalName);
        // Compatibility for descriptions supplied by existing callers.
        return string.Join(".", table.Name.Split('.').Select(QuoteIdentifier));
    }
}
