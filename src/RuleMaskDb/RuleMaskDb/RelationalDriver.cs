using System.Data.Common;
using Microsoft.Data.SqlClient;
using Npgsql;
using RuleMaskDb.ScriptDom;
using RuleMaskDb.SqlDomain;

namespace RuleMaskDb;

public sealed partial class RelationalDriver(DatabaseType databaseType)
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

    /// <summary>
    /// Caps a batch to the provider's parameter budget.
    /// </summary>
    public int GetEffectiveBatchSize(int requestedSize, int parametersPerRow)
        => GetEffectiveBatchSize(requestedSize, parametersPerRow, 0);

    /// <summary>
    /// Caps a batch after reserving parameters shared by all rows, such as static assignments.
    /// </summary>
    public int GetEffectiveBatchSize(int requestedSize, int parametersPerRow, int reservedParameters)
    {
        if (requestedSize <= 0) throw new ArgumentOutOfRangeException(nameof(requestedSize));
        if (parametersPerRow <= 0) throw new ArgumentOutOfRangeException(nameof(parametersPerRow));
        if (reservedParameters < 0) throw new ArgumentOutOfRangeException(nameof(reservedParameters));
        // Leave room for SQL Server's command metadata below the 2,100 parameter limit.
        var limit = databaseType switch
        {
            DatabaseType.SqlServer => 2000,
            DatabaseType.PostgreSQL => 65535,
            _ => throw new NotSupportedException($"Unsupported database type: {databaseType}")
        };
        if (reservedParameters >= limit || parametersPerRow > limit - reservedParameters)
            throw new InvalidOperationException($"A single update row requires {parametersPerRow} parameters plus {reservedParameters} shared parameters; the supported limit is {limit}.");
        return Math.Min(requestedSize, (limit - reservedParameters) / parametersPerRow);
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
            command.CommandText = CreateJoinedUpdateSql(table, keys, string.Join(", ", assignments), source);
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    private string CreateJoinedUpdateSql(TableDescription table, IReadOnlyList<string> keys, string assignments, string source)
    {
        var predicates = keys.Select((key, i) => $"t.{QuoteIdentifier(key)} = s.{QuoteIdentifier($"c{i}")}");
        var join = string.Join(" AND ", predicates);
        return databaseType switch
        {
            DatabaseType.PostgreSQL => $"UPDATE {QuoteTable(table)} AS t SET {assignments} FROM {source} WHERE {join} RETURNING s.\"row_index\"",
            // OUTPUT INTO supports enabled UPDATE triggers and ignores their affected-row counts.
            DatabaseType.SqlServer => $"DECLARE @updated TABLE ([row_index] int); UPDATE t SET {assignments} OUTPUT s.[row_index] INTO @updated FROM {QuoteTable(table)} AS t INNER JOIN {source} ON {join}; SELECT [row_index] FROM @updated;",
            _ => throw new NotSupportedException($"Unsupported database type: {databaseType}")
        };
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
